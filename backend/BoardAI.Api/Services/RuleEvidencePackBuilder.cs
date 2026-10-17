using System.Diagnostics;
using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// 把查询计划结果和候选的正式规则内容整理成有界事实包。
/// 这里只读取正式规则文档并做确定性投影；候选摘要不会获得 evidence_id。
/// </summary>
internal sealed class RuleEvidencePackBuilder
{
    private readonly GameRulesService _rulesService;
    private readonly LLMOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<string, RuleEvidenceItem> _evidenceByPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> _executedQuerySignatures = new(StringComparer.Ordinal);
    private int _nextEvidenceNumber = 1;

    public RuleEvidencePackBuilder(GameRulesService rulesService, LLMOptions options, ILogger logger)
    {
        _rulesService = rulesService;
        _options = options;
        _logger = logger;
    }

    public int CandidateReadFailureCount { get; private set; }
    public int OmittedEvidenceCount { get; private set; }
    public int EvidenceReadCount => _evidenceByPath.Count;
    public int LastPackChars { get; private set; }
    public double LastRetrievalMs { get; private set; }

    public async Task<RuleEvidencePack> BuildAsync(
        string game,
        IReadOnlyList<ProgramSubQuestion> questions,
        CancellationToken cancellationToken)
    {
        var pack = new RuleEvidencePack
        {
            PackId = "pack_" + Guid.NewGuid().ToString("N")[..10],
            Game = game
        };
        foreach (var question in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddQuestionAsync(pack, game, question, cancellationToken);
        }
        ApplyBudget(pack);
        return pack;
    }

    public async Task<bool> AddSupplementAsync(
        RuleEvidencePack pack,
        string game,
        ProgramSubQuestion question,
        CancellationToken cancellationToken)
    {
        var signature = QuerySignature(question);
        if (_executedQuerySignatures.Contains(signature))
            return false;

        var existing = pack.Questions.FirstOrDefault(q =>
            string.Equals(q.QuestionId, question.QuestionId, StringComparison.Ordinal));
        if (existing == null)
            return false;

        var before = pack.Questions.Count;
        await AddQuestionAsync(pack, game, question, cancellationToken);
        if (pack.Questions.Count <= before)
            return false;
        var added = pack.Questions[^1];
        pack.Questions.RemoveAt(pack.Questions.Count - 1);

        existing.Warnings.AddRange(added.Warnings.Where(w => !existing.Warnings.Contains(w)));
        existing.OmittedEvidenceCount += added.OmittedEvidenceCount;
        foreach (var candidate in added.Candidates)
        {
            if (!existing.Candidates.Any(c => string.Equals(c.Path, candidate.Path, StringComparison.Ordinal)))
                existing.Candidates.Add(candidate);
        }
        foreach (var evidence in added.Evidence)
        {
            if (!existing.Evidence.Any(e => string.Equals(e.EvidenceId, evidence.EvidenceId, StringComparison.Ordinal)))
                existing.Evidence.Add(evidence);
        }
        ApplyBudget(pack);
        return true;
    }

    private async Task AddQuestionAsync(
        RuleEvidencePack pack,
        string game,
        ProgramSubQuestion question,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _executedQuerySignatures.Add(QuerySignature(question));
        var questionPack = new EvidenceQuestionPack
        {
            QuestionId = question.QuestionId,
            Entity = question.Entity,
            Relation = question.Relation,
            Intent = question.Intent,
            UserContext = question.UserContext
        };
        pack.Questions.Add(questionPack);

        if (!IsKnownRelation(question.Relation))
        {
            questionPack.RetrievalStatus = "unsupported";
            questionPack.Warnings.Add("relation 不在 explain/condition/ordering/boundary/flow/list/identify 中。");
            return;
        }

        PlanExecutionResult planResult;
        try
        {
            var plan = JsonSerializer.SerializeToElement(new
            {
                queries = new[]
                {
                    new { relation = question.Relation, entity = question.Entity, intent = question.Intent }
                }
            });
            planResult = await _rulesService.ExecutePlanAsync(
                game, plan, question.Intent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            questionPack.RetrievalStatus = "read_failed";
            CandidateReadFailureCount++;
            questionPack.Warnings.Add("规则查询失败：" + ex.Message);
            _logger.LogWarning(ex, "[Orchestration] plan execution failed for question {QuestionId}", question.QuestionId);
            return;
        }
        finally
        {
            sw.Stop();
            LastRetrievalMs += sw.Elapsed.TotalMilliseconds;
        }

        if (!string.IsNullOrWhiteSpace(planResult.RulesVersion))
        {
            if (pack.RulesVersion == null)
                pack.RulesVersion = planResult.RulesVersion;
            else if (!string.Equals(pack.RulesVersion, planResult.RulesVersion, StringComparison.Ordinal))
                pack.Warnings.Add("同一请求内出现不同规则版本；补充读取已拒绝。");
        }

        var item = planResult.Results.FirstOrDefault();
        if (item == null)
        {
            questionPack.RetrievalStatus = "no_match";
            questionPack.Warnings.Add("程序没有返回查询结果。");
            return;
        }

        questionPack.RetrievalStatus = item.Status;
        questionPack.RetrievalSource = item.Source;
        questionPack.Candidates = (item.Candidates ?? new List<ConceptSummary>())
            .Take(Math.Max(1, _options.MaxCandidatesPerQuestion))
            .Select(candidate => new CandidateSummary
            {
                Id = candidate.Id,
                Path = string.IsNullOrWhiteSpace(candidate.Path) ? candidate.Id : candidate.Path,
                Name = candidate.Name,
                Type = candidate.Type,
                Score = candidate.Score,
                ReadStatus = "not_read",
                Reason = candidate.MatchReason
            })
            .ToList();

        if (item.Status == "ok")
        {
            AddMatchedEvidence(game, questionPack, question, item.Matched, item, "target");
            foreach (var related in item.Related.Take(Math.Max(1, _options.MaxEvidencePerQuestion)))
                AddFormalEvidence(game, questionPack, question, related, "related", null);
            return;
        }

        if (item.Status is "unresolved" or "no_match")
        {
            await ReadCandidatesAsync(game, questionPack, question, cancellationToken);
            return;
        }

        questionPack.Warnings.Add(string.IsNullOrWhiteSpace(item.Message)
            ? "规则查询未完成。"
            : item.Message);
    }

    private void AddMatchedEvidence(
        string game,
        EvidenceQuestionPack questionPack,
        ProgramSubQuestion question,
        IEnumerable<JsonElement> matched,
        PlanItemResult planItem,
        string role)
    {
        var first = true;
        foreach (var element in matched)
        {
            var evidence = AddFormalEvidence(game, questionPack, question, element, role, null);
            if (evidence == null) continue;
            if (first)
            {
                evidence.Condition = planItem.Condition;
                evidence.Cost = planItem.Cost;
                evidence.Target = planItem.Target;
                evidence.Boundary = planItem.Boundary == null
                    ? null
                    : JsonSerializer.SerializeToElement(planItem.Boundary);
                evidence.Siblings = planItem.Siblings.Count > 0 ? planItem.Siblings : null;
                evidence.PositionIndex = planItem.PositionIndex >= 0 ? planItem.PositionIndex : null;
                evidence.Loop = planItem.Loop;
                evidence.FlowContext = planItem.FlowContext.Count > 0 ? planItem.FlowContext : null;
                evidence.Facts = planItem.Facts;
                first = false;
            }
        }
    }

    private Task ReadCandidatesAsync(
        string game,
        EvidenceQuestionPack questionPack,
        ProgramSubQuestion question,
        CancellationToken cancellationToken)
    {
        var maxPerQuestion = Math.Max(1, _options.MaxEvidencePerQuestion);
        var read = 0;
        foreach (var candidate in questionPack.Candidates)
        {
            if (read >= maxPerQuestion) break;
            cancellationToken.ThrowIfCancellationRequested();
            var path = string.IsNullOrWhiteSpace(candidate.Path) ? candidate.Id : candidate.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                candidate.ReadStatus = "missing";
                continue;
            }

            try
            {
                var concept = _rulesService.GetConceptsWithExpansion(game, path);
                if (concept.Matched.Count == 0)
                {
                    candidate.ReadStatus = "missing";
                    continue;
                }

                candidate.ReadStatus = "ok";
                foreach (var matched in concept.Matched)
                {
                    if (read >= maxPerQuestion) break;
                    if (AddFormalEvidence(game, questionPack, question, matched, "candidate", candidate) != null)
                        read++;
                }
                foreach (var related in concept.Related)
                {
                    if (read >= maxPerQuestion) break;
                    if (AddFormalEvidence(game, questionPack, question, related, "related", candidate) != null)
                        read++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                candidate.ReadStatus = "read_failed";
                CandidateReadFailureCount++;
                questionPack.Warnings.Add($"候选 {path} 读取失败。");
                _logger.LogWarning(ex, "[Orchestration] candidate read failed: game={GameId}, path={Path}", game, path);
            }
        }
        return Task.CompletedTask;
    }

    private RuleEvidenceItem? AddFormalEvidence(
        string game,
        EvidenceQuestionPack questionPack,
        ProgramSubQuestion question,
        JsonElement source,
        string role,
        CandidateSummary? candidate)
    {
        if (questionPack.Evidence.Count >= Math.Max(1, _options.MaxEvidencePerQuestion))
        {
            questionPack.OmittedEvidenceCount++;
            OmittedEvidenceCount++;
            return null;
        }

        var id = RulesTextUtils.GetElementId(source);
        var entityPath = (question.Entity ?? "").Trim();
        var canonical = !string.IsNullOrWhiteSpace(candidate?.Path)
            ? candidate.Path
            : (!string.IsNullOrWhiteSpace(entityPath) && entityPath.Contains('.')
                ? entityPath
                : id);
        if (string.IsNullOrWhiteSpace(canonical))
            canonical = entityPath;
        if (string.IsNullOrWhiteSpace(canonical))
            canonical = $"{question.QuestionId}.evidence{questionPack.Evidence.Count + 1}";

        var key = $"{question.QuestionId}|{canonical}|{role}";
        if (_evidenceByPath.TryGetValue(key, out var existing))
        {
            if (!questionPack.Evidence.Contains(existing))
                questionPack.Evidence.Add(existing);
            return existing;
        }

        var name = ExtractName(source, canonical);
        var projected = ProjectSource(source);
        var item = new RuleEvidenceItem
        {
            EvidenceId = $"e{_nextEvidenceNumber++}",
            CanonicalPath = canonical,
            SourceRef = $"{game}::{canonical}",
            Name = name,
            Role = role,
            ReadStatus = "ok",
            Source = projected.Source,
            Projection = projected.Projection
        };
        _evidenceByPath[key] = item;
        questionPack.Evidence.Add(item);
        if (candidate != null && candidate.ReadStatus != "ok")
            candidate.ReadStatus = "ok";
        return item;
    }

    private (JsonElement Source, string? Projection) ProjectSource(JsonElement source)
    {
        var serialized = JsonSerializer.Serialize(source, RulesJsonUtils.RelaxedJsonOptions);
        if (serialized.Length <= Math.Max(500, _options.MaxEvidenceJsonChars))
            return (source.Clone(), null);

        var id = RulesTextUtils.GetElementId(source);
        var name = ExtractName(source, id);
        var description = source.TryGetProperty("description", out var desc) ? desc.Clone() : (JsonElement?)null;
        var condition = source.TryGetProperty("<ontology::condition>", out var cond) ? cond.Clone() : (JsonElement?)null;
        var cost = source.TryGetProperty("<ontology::cost>", out var cst) ? cst.Clone() : (JsonElement?)null;
        var target = source.TryGetProperty("target", out var tgt) ? tgt.Clone() : (JsonElement?)null;
        var content = source.TryGetProperty("<ontology::content>", out var cnt) ? cnt.Clone() : (JsonElement?)null;
        var projected = JsonSerializer.SerializeToElement(new
        {
            id,
            name,
            description,
            condition,
            cost,
            target,
            content
        });
        return (projected, "essential_fields");
    }

    private static string ExtractName(JsonElement source, string fallback)
    {
        if (source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String)
                return zh.GetString() ?? fallback;
            if (name.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                return en.GetString() ?? fallback;
        }
        return fallback;
    }

    private void ApplyBudget(RuleEvidencePack pack)
    {
        var all = pack.Questions.SelectMany(q => q.Evidence).ToList();
        var total = Math.Max(1, _options.MaxEvidenceTotal);
        if (all.Count > total)
        {
            // 优先保留 target，其次 candidate，最后 related；同类型保留先读到的。
            var keep = all
                .OrderBy(e => RolePriority(e.Role))
                .ThenBy(e => e.EvidenceId, StringComparer.Ordinal)
                .Take(total)
                .Select(e => e.EvidenceId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var question in pack.Questions)
            {
                var before = question.Evidence.Count;
                question.Evidence = question.Evidence.Where(e => keep.Contains(e.EvidenceId)).ToList();
                var removed = before - question.Evidence.Count;
                question.OmittedEvidenceCount += removed;
                OmittedEvidenceCount += removed;
            }
        }

        var packJson = JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions);
        var maxPackChars = Math.Max(4000, _options.MaxEvidencePackChars);
        while (packJson.Length > maxPackChars)
        {
            var optional = pack.Questions
                .SelectMany(q => q.Evidence.Select(e => (Question: q, Evidence: e)))
                .Where(x => x.Evidence.Role != "target")
                .OrderBy(x => RolePriority(x.Evidence.Role))
                .ThenByDescending(x => x.Evidence.EvidenceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (optional.Evidence == null)
                break;
            optional.Question.Evidence.Remove(optional.Evidence);
            optional.Question.OmittedEvidenceCount++;
            OmittedEvidenceCount++;
            packJson = JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions);
        }
        pack.OmittedEvidenceCount = OmittedEvidenceCount;
        LastPackChars = packJson.Length;
    }

    private static int RolePriority(string role) => role switch
    {
        "target" => 0,
        "candidate" => 1,
        "related" => 2,
        _ => 3
    };

    private static string QuerySignature(ProgramSubQuestion question) =>
        $"{question.QuestionId}|{question.Relation}|{question.Entity}|{question.Intent}";

    private static bool IsKnownRelation(string relation) => relation is
        "explain" or "condition" or "ordering" or "boundary" or "flow" or "list" or "identify";
}

