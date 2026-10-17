using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// Deterministically turns query-plan results into a bounded evidence pack.
/// Content identity and per-question bindings are separate: one canonical rule
/// content item may be referenced by multiple parents/questions, while every
/// binding keeps its own role and parent path.
/// </summary>
internal sealed class RuleEvidencePackBuilder
{
    private const int AbsoluteMinPackChars = 1024;
    private readonly GameRulesService _rulesService;
    private readonly LLMOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<string, RuleEvidenceItem> _evidenceByContentKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _executedQuerySignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _omittedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _contentHashesByPath = new(StringComparer.Ordinal);
    private RuleEvidencePack? _currentPack;
    private int _nextEvidenceNumber = 1;
    private int _readAttempts;
    private int _successfulReads;
    private int _bindingReuses;

    public RuleEvidencePackBuilder(GameRulesService rulesService, LLMOptions options, ILogger logger)
    {
        _rulesService = rulesService;
        _options = options;
        _logger = logger;
    }

    public int CandidateReadFailureCount { get; private set; }
    public int OmittedEvidenceCount => _currentPack?.OmittedItems.Count ?? 0;
    public int EvidenceReadCount => _currentPack?.EvidenceCatalog.Count ?? 0;
    public int LastPackChars { get; private set; }
    public double LastRetrievalMs { get; private set; }
    public RuleEvidenceStats LastStats { get; private set; } = new();

    public async Task<RuleEvidencePack> BuildAsync(
        string game,
        IReadOnlyList<ProgramSubQuestion> questions,
        CancellationToken cancellationToken)
    {
        _currentPack = new RuleEvidencePack
        {
            PackId = "pack_" + Guid.NewGuid().ToString("N")[..10],
            Game = game
        };
        _evidenceByContentKey.Clear();
        _executedQuerySignatures.Clear();
        _omittedKeys.Clear();
        _contentHashesByPath.Clear();
        _nextEvidenceNumber = 1;
        _readAttempts = 0;
        _successfulReads = 0;
        _bindingReuses = 0;
        CandidateReadFailureCount = 0;
        LastRetrievalMs = 0;

        var states = new List<QuestionBuildState>();
        foreach (var question in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await PrepareQuestionAsync(_currentPack, game, question, cancellationToken);
            states.Add(state);
        }

        await FillPackAsync(_currentPack, states, cancellationToken);
        ApplyCandidateReadStatuses(_currentPack);
        UpdateCoverage(_currentPack);
        ApplyCharBudget(_currentPack);
        ApplyCandidateReadStatuses(_currentPack);
        UpdateCoverage(_currentPack);
        ApplyCharBudget(_currentPack);
        FinalizeStats(_currentPack);
        return _currentPack;
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

        _currentPack = pack;
        var before = pack.Questions.Count;
        var state = await PrepareQuestionAsync(pack, game, question, cancellationToken);
        if (pack.Questions.Count <= before)
            return false;

        var added = state.Pack;
        pack.Questions.RemoveAt(pack.Questions.Count - 1);
        await FillPackAsync(pack, new List<QuestionBuildState> { state }, cancellationToken, supplementRound: true);
        existing.Warnings.AddRange(added.Warnings.Where(w => !existing.Warnings.Contains(w)));
        existing.OmittedItems.AddRange(added.OmittedItems.Where(item => !existing.OmittedItems.Any(x => x.CanonicalPath == item.CanonicalPath && x.Role == item.Role)));
        existing.OmittedEvidenceCount = existing.OmittedItems.Count;
        foreach (var candidate in state.Candidates)
        {
            if (!existing.Candidates.Any(c => string.Equals(c.Path, candidate.Path, StringComparison.Ordinal)))
                existing.Candidates.Add(candidate);
        }
        var existingEvidenceIds = existing.EvidenceBindings
            .Select(binding => binding.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var binding in added.EvidenceBindings)
        {
            var alreadyBound = existingEvidenceIds.Contains(binding.EvidenceId);
            if (!alreadyBound && existingEvidenceIds.Count >= EffectiveMaxEvidencePerQuestion)
            {
                RecordOmission(pack, existing, existing.QuestionId,
                    ResolveBindingPath(pack, binding), binding.Role,
                    "per_question_budget", "补充取证后该子问题仍受证据条数上限约束；未装入该绑定。");
                continue;
            }
            if (existing.EvidenceBindings.Any(b => b.EvidenceId == binding.EvidenceId
                                                    && b.Role == binding.Role
                                                    && b.ParentPath == binding.ParentPath))
                continue;
            existing.EvidenceBindings.Add(binding);
            existingEvidenceIds.Add(binding.EvidenceId);
        }
        RemoveOrphanEvidence(pack);
        ApplyCandidateReadStatuses(pack);
        UpdateCoverage(pack);
        ApplyCharBudget(pack);
        ApplyCandidateReadStatuses(pack);
        UpdateCoverage(pack);
        ApplyCharBudget(pack);
        FinalizeStats(pack);
        return true;
    }

    private async Task<QuestionBuildState> PrepareQuestionAsync(
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
        var state = new QuestionBuildState { Question = question, Pack = questionPack };

        if (!IsKnownRelation(question.Relation))
        {
            questionPack.RetrievalStatus = "unsupported";
            questionPack.Warnings.Add("relation 不在 explain/condition/ordering/boundary/flow/list/identify 中。");
            return state;
        }

        PlanExecutionResult planResult;
        try
        {
            var plan = JsonSerializer.SerializeToElement(new
            {
                queries = new[] { new { relation = question.Relation, entity = question.Entity, intent = question.Intent } }
            });
            planResult = await _rulesService.ExecutePlanAsync(game, plan, question.Intent, cancellationToken);
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
            return state;
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
            return state;
        }

        state.PlanItem = item;
        questionPack.RetrievalStatus = item.Status;
        questionPack.RetrievalSource = item.Source;
        state.Candidates = (item.Candidates ?? new List<ConceptSummary>())
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
        questionPack.Candidates = state.Candidates;

        if (item.Status == "ok")
        {
            var matchedIndex = 0;
            foreach (var matched in item.Matched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonicalPath = ResolveMatchedPath(game, question, item, matched);
                if (string.IsNullOrWhiteSpace(canonicalPath))
                {
                    RecordOmission(pack, questionPack, question.QuestionId, $"<unverified:{matchedIndex}>", "target", "unverifiable_path", "目标内容无规范路径，不能作为正式证据。");
                    matchedIndex++;
                    continue;
                }

                state.Primary.Add(new ReadSource
                {
                    Source = matched.Clone(),
                    CanonicalPath = canonicalPath,
                    Role = "target",
                    ParentPath = null,
                    RelationFields = item,
                    Candidate = null
                });
                await QueueRelatedForPathAsync(pack, state, game, canonicalPath, cancellationToken);
                matchedIndex++;
            }
            return state;
        }

        if (item.Status is "unresolved" or "no_match")
        {
            foreach (var candidate in state.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadCandidateAsync(pack, state, game, candidate, cancellationToken);
                if (state.Primary.Count >= Math.Max(1, _options.MaxEvidencePerQuestion))
                    break;
            }
            return state;
        }

        questionPack.Warnings.Add(string.IsNullOrWhiteSpace(item.Message) ? "规则查询未完成。" : item.Message);
        return state;
    }

    private async Task ReadCandidateAsync(
        RuleEvidencePack pack,
        QuestionBuildState state,
        string game,
        CandidateSummary candidate,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(candidate.Path) ? candidate.Id : candidate.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            candidate.ReadStatus = "missing";
            RecordOmission(pack, state.Pack, state.Question.QuestionId, "<missing>", "candidate", "missing_path", "候选没有规范路径。");
            return;
        }

        _readAttempts++;
        try
        {
            var read = _rulesService.ReadConceptWithExpansionWithPaths(game, path);
            if (read.Matched.Count == 0)
            {
                candidate.ReadStatus = "missing";
                CandidateReadFailureCount++;
                RecordOmission(pack, state.Pack, state.Question.QuestionId, path, "candidate", "read_missing", "候选正式内容未找到，不能引用候选摘要。");
                return;
            }

            _successfulReads++;
            candidate.ReadStatus = "ok";
            foreach (var matched in read.Matched)
            {
                // A candidate can have a relation-specific computed projection just like a
                // direct match. Reuse the plan result with the candidate's actual canonical path.
                PlanItemResult? candidatePlan = null;
                try
                {
                    var plan = JsonSerializer.SerializeToElement(new
                    {
                        queries = new[] { new { relation = state.Question.Relation, entity = matched.CanonicalPath, intent = state.Question.Intent } }
                    });
                    var planResult = await _rulesService.ExecutePlanAsync(
                        game, plan, state.Question.Intent, cancellationToken);
                    candidatePlan = planResult.Results.FirstOrDefault();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Orchestration] candidate relation projection failed for {Path}", matched.CanonicalPath);
                }

                state.Primary.Add(new ReadSource
                {
                    Source = matched.Element.Clone(),
                    CanonicalPath = matched.CanonicalPath,
                    Role = "candidate",
                    ParentPath = path,
                    RelationFields = candidatePlan,
                    Candidate = candidate
                });
            }
            foreach (var related in read.Related)
            {
                state.Related.Enqueue(new ReadSource
                {
                    Source = related.Element.Clone(),
                    CanonicalPath = related.CanonicalPath,
                    Role = "related",
                    ParentPath = related.ParentPath ?? path,
                    RelationFields = null,
                    Candidate = candidate
                });
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
            RecordOmission(pack, state.Pack, state.Question.QuestionId, path, "candidate", "read_failed", "候选正式读取失败：" + ex.Message);
            _logger.LogWarning(ex, "[Orchestration] candidate read failed: game={GameId}, path={Path}", game, path);
        }
    }

    private Task QueueRelatedForPathAsync(
        RuleEvidencePack pack,
        QuestionBuildState state,
        string game,
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        _readAttempts++;
        try
        {
            var read = _rulesService.ReadConceptWithExpansionWithPaths(game, canonicalPath);
            if (read.Matched.Count == 0)
            {
                RecordOmission(pack, state.Pack, state.Question.QuestionId, canonicalPath, "related", "read_missing", "目标正式内容关联读取失败。");
                return Task.CompletedTask;
            }
            _successfulReads++;
            foreach (var related in read.Related)
            {
                state.Related.Enqueue(new ReadSource
                {
                    Source = related.Element.Clone(),
                    CanonicalPath = related.CanonicalPath,
                    Role = "related",
                    ParentPath = related.ParentPath ?? canonicalPath,
                    RelationFields = null,
                    Candidate = null
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CandidateReadFailureCount++;
            RecordOmission(pack, state.Pack, state.Question.QuestionId, canonicalPath, "related", "read_failed", "关联正式读取失败：" + ex.Message);
            _logger.LogWarning(ex, "[Orchestration] related read failed: game={GameId}, path={Path}", game, canonicalPath);
        }
        return Task.CompletedTask;
    }

    private Task FillPackAsync(
        RuleEvidencePack pack,
        IReadOnlyList<QuestionBuildState> states,
        CancellationToken cancellationToken,
        bool supplementRound = false)
    {
        // First pass: guarantee one primary evidence binding per question before any
        // question can consume the shared budget with related/candidate extras.
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Primary.Count == 0) continue;
            var source = state.Primary[0];
            state.Primary.RemoveAt(0);
            AddEvidenceBinding(pack, state, source, supplementRound ? "supplement" : "initial");
        }

        // Second pass: remaining primary candidate/target contents round-robin.
        // We attempt every source even after the total budget is full so that a
        // duplicate content item can still add its relation edge without consuming
        // another unique-content slot. Genuine budget failures are recorded by
        // AddEvidenceBinding and the source is consumed exactly once.
        while (states.Any(s => s.Primary.Count > 0))
        {
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Primary.Count == 0) continue;
                var source = state.Primary[0];
                state.Primary.RemoveAt(0);
                AddEvidenceBinding(pack, state, source, supplementRound ? "supplement" : "initial");
            }
        }

        // Third pass: related contents round-robin, only after primary coverage.
        while (states.Any(s => s.Related.Count > 0))
        {
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Related.Count == 0) continue;
                var source = state.Related.Dequeue();
                AddEvidenceBinding(pack, state, source, supplementRound ? "supplement" : "initial");
            }
        }
        return Task.CompletedTask;
    }

    private void AddEvidenceBinding(
        RuleEvidencePack pack,
        QuestionBuildState state,
        ReadSource source,
        string addedInRound)
    {
        var contentHash = Hash(JsonSerializer.Serialize(source.Source, RulesJsonUtils.RelaxedJsonOptions));
        if (!_contentHashesByPath.TryGetValue(source.CanonicalPath, out var hashes))
        {
            hashes = new HashSet<string>(StringComparer.Ordinal);
            _contentHashesByPath[source.CanonicalPath] = hashes;
        }
        if (hashes.Count > 0 && !hashes.Contains(contentHash))
            pack.Warnings.Add($"规范路径 {source.CanonicalPath} 对应多个不同正式内容，已保留独立证据 ID 并记录冲突。");
        hashes.Add(contentHash);
        var projectionKey = ProjectionKey(source);
        var contentKey = $"{pack.Game}|{pack.RulesVersion}|{source.CanonicalPath}|{contentHash}|{projectionKey}";

        if (_evidenceByContentKey.TryGetValue(contentKey, out var existing))
        {
            var alreadyBound = state.Pack.EvidenceBindings.Any(binding =>
                string.Equals(binding.EvidenceId, existing.EvidenceId, StringComparison.Ordinal));
            if (!alreadyBound && !CanBindAnother(state.Pack))
            {
                RecordOmission(pack, state.Pack, state.Question.QuestionId, source.CanonicalPath, source.Role, "per_question_budget", "该子问题已达到证据条数上限。");
                return;
            }
            if (AddBindingIfMissing(state.Pack, existing.EvidenceId, source, addedInRound))
                _bindingReuses++;
            return;
        }

        if (!CanBindAnother(state.Pack))
        {
            RecordOmission(pack, state.Pack, state.Question.QuestionId, source.CanonicalPath, source.Role, "per_question_budget", "该子问题已达到证据条数上限。");
            return;
        }

        if (pack.EvidenceCatalog.Count >= EffectiveMaxEvidenceTotal)
        {
            RecordOmission(pack, state.Pack, state.Question.QuestionId, source.CanonicalPath, source.Role, "total_budget", "已达到事实包总证据上限。");
            return;
        }

        var (projectedSource, projection, projectionComplete) = ProjectSource(source.Source);
        var evidence = new RuleEvidenceItem
        {
            EvidenceId = $"e{_nextEvidenceNumber++}",
            CanonicalPath = source.CanonicalPath,
            SourceRef = $"{pack.Game}::{source.CanonicalPath}",
            Name = ExtractName(source.Source, source.CanonicalPath),
            ReadStatus = "ok",
            ContentHash = contentHash,
            Projection = projection,
            ProjectionComplete = projectionComplete,
            Source = projectedSource,
            Condition = source.RelationFields?.Condition,
            Cost = source.RelationFields?.Cost,
            Target = source.RelationFields?.Target,
            Boundary = source.RelationFields?.Boundary == null ? null : JsonSerializer.SerializeToElement(source.RelationFields.Boundary),
            Siblings = source.RelationFields?.Siblings.Count > 0 ? source.RelationFields.Siblings : null,
            PositionIndex = source.RelationFields?.PositionIndex >= 0 ? source.RelationFields.PositionIndex : null,
            Loop = source.RelationFields?.Loop,
            FlowContext = source.RelationFields?.FlowContext.Count > 0 ? source.RelationFields.FlowContext : null,
            Facts = source.RelationFields?.Facts
        };
        pack.EvidenceCatalog.Add(evidence);
        _evidenceByContentKey[contentKey] = evidence;
        AddBindingIfMissing(state.Pack, evidence.EvidenceId, source, addedInRound);
    }

    private static bool AddBindingIfMissing(
        EvidenceQuestionPack questionPack,
        string evidenceId,
        ReadSource source,
        string addedInRound)
    {
        var exists = questionPack.EvidenceBindings.Any(binding =>
            string.Equals(binding.EvidenceId, evidenceId, StringComparison.Ordinal)
            && string.Equals(binding.Role, source.Role, StringComparison.Ordinal)
            && string.Equals(binding.ParentPath, source.ParentPath, StringComparison.Ordinal));
        if (exists) return false;

        questionPack.EvidenceBindings.Add(new RuleEvidenceBinding
        {
            EvidenceId = evidenceId,
            Role = source.Role,
            ParentPath = source.ParentPath,
            Rank = questionPack.EvidenceBindings.Count + 1,
            AddedInRound = addedInRound
        });
        return true;
    }

    private static string ProjectionKey(ReadSource source)
    {
        if (source.Role != "target" && source.Role != "candidate")
            return "base";
        if (source.RelationFields == null) return "plain";
        var fields = new
        {
            relation = source.Role,
            source.RelationFields.Condition,
            source.RelationFields.Cost,
            source.RelationFields.Target,
            source.RelationFields.Boundary,
            source.RelationFields.Siblings,
            source.RelationFields.PositionIndex,
            source.RelationFields.Loop,
            source.RelationFields.FlowContext,
            source.RelationFields.Facts
        };
        return "relation:" + Hash(JsonSerializer.Serialize(fields, RulesJsonUtils.RelaxedJsonOptions));
    }

    private string ResolveMatchedPath(string game, ProgramSubQuestion question, PlanItemResult item, JsonElement element)
    {
        _ = item;
        var targetJson = JsonSerializer.Serialize(element, RulesJsonUtils.RelaxedJsonOptions);
        var queryCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(question.Entity))
            queryCandidates.Add(question.Entity.Trim());
        var elementId = RulesTextUtils.GetElementId(element);
        if (!string.IsNullOrWhiteSpace(elementId))
            queryCandidates.Add(elementId.Trim());

        // The canonical path must come from the catalog resolution, never from a
        // user phrase or a child's local id. Compare the resolved concrete node to
        // the exact plan item; if the node is ambiguous, report no verifiable path.
        var resolvedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queryCandidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var read = _rulesService.ReadConceptWithExpansionWithPaths(game, query);
                foreach (var hit in read.Matched)
                {
                    var hitJson = JsonSerializer.Serialize(hit.Element, RulesJsonUtils.RelaxedJsonOptions);
                    if (string.Equals(hitJson, targetJson, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(hit.CanonicalPath))
                    {
                        resolvedPaths.Add(hit.CanonicalPath);
                    }
                }
            }
            catch
            {
                // The caller records the unverifiable path as omitted.
            }
        }
        return resolvedPaths.Count == 1 ? resolvedPaths.First() : "";
    }

    private (JsonElement Source, string? Projection, bool Complete) ProjectSource(JsonElement source)
    {
        var serialized = JsonSerializer.Serialize(source, RulesJsonUtils.RelaxedJsonOptions);
        if (serialized.Length <= EffectiveMaxEvidenceJsonChars)
            return (source.Clone(), null, true);

        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            // Identity / human-readable explanation.
            "id", "name", "description", "definition", "abstract", "type", "appearance",
            "aliases", "media", "component", "owner", "owner_path",
            // Ontology interaction and boundary semantics.
            "requirement", "requirements", "condition", "cost", "target", "content",
            "constraints", "required", "optional", "prerequisite", "prerequisites",
            "<ontology::condition>", "<ontology::cost>", "<ontology::content>",
            "<ontology::instant_content>", "<ontology::continuous_content>",
            "<ontology::instant_effect>", "<ontology::continuous_effect>",
            "<ontology::state_change>", "<ontology::substitution>",
            "<ontology::overflow_compensation>", "<ontology::underflow_compensation>",
            "<ontology::lap_event>", "<ontology::track>", "<ontology::push_track>",
            // Ordering / flow / timing.
            "options", "events", "pipeline", "step", "order", "position", "stage",
            "do_after", "when", "until", "duration", "timing", "loop", "flow",
            "trigger", "triggers", "actions", "conditions", "procedures",
            // Numeric / resource / scoring semantics.
            "quantity", "capacity", "price", "sell_price", "vp", "vp_value",
            "score", "scoring", "success_points", "values", "value", "formula",
            "expression", "expr", "amount", "count", "copies", "numeric", "number",
            "minimum", "maximum", "unit", "per", "level", "level_effects",
            "effect", "effect_category", "gain", "transfer", "rows", "columns",
            "table", "entries", "examples", "slots",
            // Common game-specific identity/type fields.
            "card_type", "module_type", "feature", "feature_type", "territory_type",
            "building_type", "track_type", "track_kind", "tile_number"
        };
        var root = new JsonObject();
        var complete = true;
        foreach (var prop in source.EnumerateObject())
        {
            if (!keep.Contains(prop.Name))
            {
                complete = false;
                continue;
            }
            root[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        var projected = JsonSerializer.SerializeToElement(root);
        var projectedText = JsonSerializer.Serialize(projected, RulesJsonUtils.RelaxedJsonOptions);
        if (projectedText.Length <= EffectiveMaxEvidenceJsonChars)
            return (projected, complete ? null : "semantic_fields", complete);

        // Last-resort projection: keep the semantic core. If that still does
        // not fit, truncate long string leaves recursively and mark the item as
        // incomplete. A projected item must never claim completeness when data
        // had to be cut.
        var coreKeys = new[]
        {
            "id", "name", "description", "<ontology::condition>", "<ontology::cost>",
            "target", "<ontology::content>", "<ontology::instant_content>",
            "<ontology::continuous_content>", "quantity", "capacity", "score", "formula"
        };
        var core = new JsonObject();
        foreach (var name in coreKeys)
        {
            if (source.TryGetProperty(name, out var value))
                core[name] = JsonNode.Parse(value.GetRawText());
        }
        var minimalText = SerializeNode(core);
        if (minimalText.Length <= EffectiveMaxEvidenceJsonChars)
            return (JsonSerializer.SerializeToElement(core), "essential_fields", false);

        var limit = Math.Max(16, EffectiveMaxEvidenceJsonChars / 3);
        JsonNode? truncated = JsonNode.Parse(minimalText);
        while (limit >= 16 && SerializeNode(truncated).Length > EffectiveMaxEvidenceJsonChars)
        {
            truncated = TruncateJsonStrings(truncated, limit);
            limit /= 2;
        }
        var truncatedText = SerializeNode(truncated);
        if (truncatedText.Length > EffectiveMaxEvidenceJsonChars)
        {
            // Keep only identity and the most important interaction field(s);
            // the caller still gets a deterministic, explicitly incomplete slice.
            var lastResort = new JsonObject();
            foreach (var name in new[] { "id", "name", "<ontology::condition>", "<ontology::content>" })
            {
                if (source.TryGetProperty(name, out var value))
                    lastResort[name] = JsonNode.Parse(value.GetRawText());
            }
            limit = Math.Max(8, EffectiveMaxEvidenceJsonChars / 6);
            var truncatedLastResort = TruncateJsonStrings(lastResort, limit);
            while (SerializeNode(truncatedLastResort).Length > EffectiveMaxEvidenceJsonChars && limit > 8)
            {
                limit /= 2;
                truncatedLastResort = TruncateJsonStrings(lastResort, limit);
            }
            return (JsonSerializer.SerializeToElement(truncatedLastResort), "essential_fields_truncated", false);
        }
        return (JsonSerializer.SerializeToElement(truncated!), "essential_fields_truncated", false);
    }

    private static string SerializeNode(JsonNode? node) =>
        node?.ToJsonString(RulesJsonUtils.RelaxedJsonOptions) ?? "null";

    private static JsonNode? TruncateJsonStrings(JsonNode? node, int maxStringChars)
    {
        if (node is JsonObject obj)
        {
            var clone = new JsonObject();
            foreach (var property in obj)
                clone[property.Key] = TruncateJsonStrings(property.Value, maxStringChars);
            return clone;
        }
        if (node is JsonArray array)
        {
            var clone = new JsonArray();
            foreach (var item in array.Take(200))
                clone.Add(TruncateJsonStrings(item, maxStringChars));
            return clone;
        }
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && text.Length > maxStringChars)
            return JsonValue.Create(text[..maxStringChars] + "…");
        return node?.DeepClone();
    }

    private void ApplyCharBudget(RuleEvidencePack pack)
    {
        // Stats and omission metadata are part of the serialized pack. Refresh
        // them before every length check so the effective budget is measured
        // against exactly what Respond will receive.
        RefreshStats(pack);
        while (SerializePack(pack).Length > EffectiveMaxPackChars)
        {
            var removal = FindBindingToTrim(pack);
            if (removal == null) break;
            removal.Value.Question.EvidenceBindings.Remove(removal.Value.Binding);
            RecordOmission(pack, removal.Value.Question, removal.Value.Question.QuestionId,
                ResolveBindingPath(pack, removal.Value.Binding), removal.Value.Binding.Role,
                "pack_char_budget", "事实包字符超限，移除低优先级关联/候选；目标未完整装入。");
            RemoveOrphanEvidence(pack);
            RefreshStats(pack);
        }

        // If only target bindings remain, try projecting all sources before
        // dropping any target. This keeps semantic fields but never sends an
        // over-budget pack.
        if (SerializePack(pack).Length > EffectiveMaxPackChars)
        {
            ProjectAllEvidence(pack);
            RefreshStats(pack);
        }

        while (SerializePack(pack).Length > EffectiveMaxPackChars)
        {
            var target = FindTargetBindingToTrim(pack);
            if (target == null) break;
            target.Value.Question.EvidenceBindings.Remove(target.Value.Binding);
            RecordOmission(pack, target.Value.Question, target.Value.Question.QuestionId,
                ResolveBindingPath(pack, target.Value.Binding), target.Value.Binding.Role,
                "pack_char_budget", "事实包字符超限，目标证据无法完整装入。");
            RemoveOrphanEvidence(pack);
            RefreshStats(pack);
        }

        if (SerializePack(pack).Length > EffectiveMaxPackChars)
            BuildMetadataBudgetFallback(pack);

        RefreshStats(pack);
        LastPackChars = SerializePack(pack).Length;
    }

    private void BuildMetadataBudgetFallback(RuleEvidencePack pack)
    {
        if (!pack.MetadataBudgetExceeded)
        {
            pack.MetadataBudgetExceeded = true;
            pack.Warnings.Add("事实包元信息本身超过配置字符上限；已清空证据并标记 budget_insufficient_metadata。");
        }
        pack.RetainedOmittedCount ??= pack.OmittedItems.Count;

        pack.EvidenceCatalog.Clear();
        foreach (var question in pack.Questions)
        {
            question.EvidenceBindings.Clear();
            question.Candidates.Clear();
            question.UserContext = null;
            question.Entity = TruncateForPack(question.Entity, 160);
            question.Intent = TruncateForPack(question.Intent, 240);
        }
        RefreshStats(pack);

        // If long prose or an unusually large omission ledger still exceeds the
        // floor, keep the explicit state but strip secondary ledger detail. The
        // aggregate count is retained in stats and omitted_evidence_count.
        if (SerializePack(pack).Length > EffectiveMaxPackChars)
        {
            pack.RetainedOmittedCount = pack.OmittedItems.Count;
            pack.OmittedItems.Clear();
            foreach (var question in pack.Questions)
                question.OmittedItems.Clear();
            if (pack.Warnings.Count > 1)
                pack.Warnings = new List<string> { pack.Warnings[^1] };
            RefreshStats(pack);
        }

        if (SerializePack(pack).Length > EffectiveMaxPackChars)
        {
            var omitted = pack.RetainedOmittedCount ?? 0;
            pack.Questions = pack.Questions
                .Select(question => new EvidenceQuestionPack
                {
                    QuestionId = question.QuestionId,
                    RetrievalStatus = "budget_insufficient_metadata",
                    CoverageStatus = "budget_insufficient_metadata",
                    OmittedEvidenceCount = omitted,
                    Warnings = new List<string>()
                })
                .ToList();
            pack.Warnings = new List<string>
            {
                "事实包元信息超过配置字符上限；无可用证据，返回显式预算不足状态。"
            };
            RefreshStats(pack);
        }
    }

    private static string TruncateForPack(string value, int maxChars) =>
        string.IsNullOrEmpty(value) || value.Length <= maxChars ? value : value[..maxChars] + "…";

    internal string DebugResolveMatchedPath(string game, ProgramSubQuestion question, PlanItemResult item, JsonElement element) =>
        ResolveMatchedPath(game, question, item, element);

    internal (EvidenceQuestionPack Question, RuleEvidenceBinding Binding)? DebugFindBindingToTrim(RuleEvidencePack pack) =>
        FindBindingToTrim(pack);

    internal (EvidenceQuestionPack Question, RuleEvidenceBinding Binding)? DebugFindTargetBindingToTrim(RuleEvidencePack pack) =>
        FindTargetBindingToTrim(pack);

    internal void DebugApplyCharBudget(RuleEvidencePack pack)
    {
        UpdateCoverage(pack);
        ApplyCharBudget(pack);
        UpdateCoverage(pack);
        if (SerializePack(pack).Length > EffectiveMaxPackChars)
            ApplyCharBudget(pack);
        LastPackChars = SerializePack(pack).Length;
        LastStats = pack.Stats;
    }

    private (EvidenceQuestionPack Question, RuleEvidenceBinding Binding)? FindBindingToTrim(RuleEvidencePack pack)
    {
        var candidates = new List<(EvidenceQuestionPack Question, RuleEvidenceBinding Binding, int QuestionIndex)>();
        for (var i = 0; i < pack.Questions.Count; i++)
        {
            foreach (var binding in pack.Questions[i].EvidenceBindings)
            {
                if (binding.Role is "related" or "candidate")
                    candidates.Add((pack.Questions[i], binding, i));
            }
        }
        if (candidates.Count == 0) return null;
        return candidates
            .OrderBy(x => x.Binding.Role == "related" ? 0 : 1)
            .ThenByDescending(x => x.Binding.Rank)
            .ThenByDescending(x => x.QuestionIndex)
            .Select(x => ((EvidenceQuestionPack Question, RuleEvidenceBinding Binding)?)(x.Question, x.Binding))
            .FirstOrDefault();
    }

    private static (EvidenceQuestionPack Question, RuleEvidenceBinding Binding)? FindTargetBindingToTrim(RuleEvidencePack pack)
    {
        var candidates = new List<(EvidenceQuestionPack Question, RuleEvidenceBinding Binding, int QuestionIndex)>();
        for (var i = 0; i < pack.Questions.Count; i++)
        {
            foreach (var binding in pack.Questions[i].EvidenceBindings.Where(b => b.Role == "target"))
                candidates.Add((pack.Questions[i], binding, i));
        }
        return candidates
            .OrderByDescending(x => x.Binding.Rank)
            .ThenByDescending(x => x.QuestionIndex)
            .Select(x => ((EvidenceQuestionPack Question, RuleEvidenceBinding Binding)?)(x.Question, x.Binding))
            .FirstOrDefault();
    }

    private static string ResolveBindingPath(RuleEvidencePack pack, RuleEvidenceBinding binding) =>
        pack.EvidenceCatalog.FirstOrDefault(e => e.EvidenceId == binding.EvidenceId)?.CanonicalPath ?? binding.EvidenceId;

    private static void RemoveOrphanEvidence(RuleEvidencePack pack)
    {
        var referenced = pack.Questions
            .SelectMany(q => q.EvidenceBindings)
            .Select(b => b.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        pack.EvidenceCatalog = pack.EvidenceCatalog.Where(e => referenced.Contains(e.EvidenceId)).ToList();
    }

    private void ProjectAllEvidence(RuleEvidencePack pack)
    {
        foreach (var evidence in pack.EvidenceCatalog)
        {
            if (evidence.Source == null) continue;
            var projected = ProjectSource(evidence.Source.Value);
            evidence.Source = projected.Source;
            evidence.Projection = projected.Projection;
            evidence.ProjectionComplete = projected.Complete;
        }
    }

    private void ApplyCandidateReadStatuses(RuleEvidencePack pack)
    {
        foreach (var question in pack.Questions)
        {
            foreach (var candidate in question.Candidates)
            {
                var hasBoundCandidate = question.EvidenceBindings.Any(binding =>
                    binding.Role == "candidate"
                    && binding.ParentPath == candidate.Path
                    && pack.EvidenceCatalog.Any(e => e.EvidenceId == binding.EvidenceId));
                if (!hasBoundCandidate && candidate.ReadStatus == "ok")
                    candidate.ReadStatus = "budget_skipped";
            }
        }
    }

    private void UpdateCoverage(RuleEvidencePack pack)
    {
        foreach (var question in pack.Questions)
        {
            var distinct = DistinctBindingCount(question);
            question.OmittedEvidenceCount = pack.MetadataBudgetExceeded
                ? (pack.RetainedOmittedCount ?? question.OmittedItems.Count)
                : question.OmittedItems.Count;
            if (pack.MetadataBudgetExceeded)
            {
                question.CoverageStatus = "budget_insufficient_metadata";
                continue;
            }
            question.CoverageStatus = question.OmittedItems.Count > 0
                ? "partial_evidence"
                : distinct == 0
                    ? (question.RetrievalStatus is "no_match" or "unsupported" or "read_failed" ? question.RetrievalStatus : "no_evidence")
                    : "evidence_available";
        }
        pack.OmittedEvidenceCount = pack.RetainedOmittedCount ?? pack.OmittedItems.Count;
    }

    private void FinalizeStats(RuleEvidencePack pack)
    {
        RefreshStats(pack);
        LastPackChars = SerializePack(pack).Length;
    }

    private void RefreshStats(RuleEvidencePack pack)
    {
        var omitted = pack.RetainedOmittedCount ?? pack.OmittedItems.Count;
        pack.OmittedEvidenceCount = omitted;
        pack.Stats = new RuleEvidenceStats
        {
            ReadAttempts = _readAttempts,
            SuccessfulReads = _successfulReads,
            UniqueContents = pack.EvidenceCatalog.Count,
            BindingReuses = _bindingReuses,
            PackItems = pack.EvidenceCatalog.Count,
            Omitted = omitted,
            EffectiveMaxPackChars = EffectiveMaxPackChars,
            EffectiveMaxEvidenceTotal = EffectiveMaxEvidenceTotal,
            EffectiveMaxEvidencePerQuestion = EffectiveMaxEvidencePerQuestion,
            EffectiveMaxCandidatesPerQuestion = EffectiveMaxCandidatesPerQuestion
        };
        LastStats = pack.Stats;
    }

    private void RecordOmission(
        RuleEvidencePack pack,
        EvidenceQuestionPack question,
        string questionId,
        string canonicalPath,
        string role,
        string reason,
        string impact)
    {
        var key = $"{questionId}|{canonicalPath}|{role}|{reason}";
        if (!_omittedKeys.Add(key)) return;
        var info = new OmittedEvidenceInfo
        {
            QuestionId = questionId,
            CanonicalPath = canonicalPath,
            Role = role,
            Reason = reason,
            DependencyImpact = impact
        };
        question.OmittedItems.Add(info);
        pack.OmittedItems.Add(info);
        question.OmittedEvidenceCount = question.OmittedItems.Count;
    }

    private static int DistinctBindingCount(EvidenceQuestionPack question) =>
        question.EvidenceBindings.Select(b => b.EvidenceId).Distinct(StringComparer.Ordinal).Count();

    private bool CanBindAnother(EvidenceQuestionPack question) =>
        DistinctBindingCount(question) < EffectiveMaxEvidencePerQuestion;

    private int EffectiveMaxPackChars => Math.Max(AbsoluteMinPackChars, _options.MaxEvidencePackChars);
    private int EffectiveMaxEvidenceTotal => Math.Max(1, _options.MaxEvidenceTotal);
    private int EffectiveMaxEvidencePerQuestion => Math.Max(1, _options.MaxEvidencePerQuestion);
    private int EffectiveMaxCandidatesPerQuestion => Math.Max(1, _options.MaxCandidatesPerQuestion);
    private int EffectiveMaxEvidenceJsonChars => Math.Max(500, _options.MaxEvidenceJsonChars);

    private static string SerializePack(RuleEvidencePack pack) =>
        JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

    private static string QuerySignature(ProgramSubQuestion question) =>
        $"{question.QuestionId}|{question.Relation}|{question.Entity}|{question.Intent}";

    private static bool IsKnownRelation(string relation) => relation is
        "explain" or "condition" or "ordering" or "boundary" or "flow" or "list" or "identify";

    private sealed class QuestionBuildState
    {
        public required ProgramSubQuestion Question { get; init; }
        public required EvidenceQuestionPack Pack { get; init; }
        public PlanItemResult? PlanItem { get; set; }
        public List<CandidateSummary> Candidates { get; set; } = new();
        public List<ReadSource> Primary { get; } = new();
        public Queue<ReadSource> Related { get; } = new();
    }

    private sealed class ReadSource
    {
        public required JsonElement Source { get; init; }
        public required string CanonicalPath { get; init; }
        public required string Role { get; init; }
        public string? ParentPath { get; init; }
        public PlanItemResult? RelationFields { get; init; }
        public CandidateSummary? Candidate { get; init; }
    }
}
