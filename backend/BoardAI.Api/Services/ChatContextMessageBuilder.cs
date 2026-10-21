using System.Globalization;
using System.Text;
using BoardAI.Api.Models;
using Microsoft.Extensions.Logging;

namespace BoardAI.Api.Services;

/// <summary>
/// 教程播放上下文的消息构造与截断。输出文本、空值处理和分隔符保持原有行为；
/// 不参与模型调用、工具执行或证据收集。
/// </summary>
internal static class ChatContextMessageBuilder
{
    internal static string? Build(ChatContext? context, ILogger logger)
    {
        if (context == null) return null;

        var groupPath = CleanPath(context.GroupPath);
        var recentCues = context.RecentCues?
            .Where(HasRecentCueContent)
            .Take(5)
            .ToList() ?? new List<RecentCueContext>();

        var hasContent = !string.IsNullOrWhiteSpace(context.GameName)
            || !string.IsNullOrWhiteSpace(context.CueId)
            || !string.IsNullOrWhiteSpace(context.CueText)
            || context.CueIndex.HasValue
            || context.Position.HasValue
            || groupPath.Count > 0
            || recentCues.Count > 0;

        if (!hasContent) return null;

        var builder = new StringBuilder();
        builder.AppendLine("当前上下文（仅用于理解客人提问，不要逐字复述）：");

        if (!string.IsNullOrWhiteSpace(context.GameName))
            builder.AppendLine($"游戏：{context.GameName}");

        if (groupPath.Count > 0)
            builder.AppendLine($"当前小节：{string.Join(" > ", groupPath)}");

        if (!string.IsNullOrWhiteSpace(context.CueId))
            builder.AppendLine($"当前 cue：{context.CueId}");

        if (!string.IsNullOrWhiteSpace(context.CueText))
            builder.AppendLine($"当前台词：{Truncate(context.CueText.Trim(), 300)}");

        if (context.Position.HasValue)
            builder.AppendLine(
                $"当前播放位置：{context.Position.Value.ToString("0.##", CultureInfo.InvariantCulture)} 秒");

        if (recentCues.Count > 0)
        {
            var hasActionSummaries = recentCues.Any(cue =>
                cue.Actions?.Any(action => !string.IsNullOrWhiteSpace(action)) == true);
            if (hasActionSummaries)
            {
                builder.AppendLine("以下“动画脚本动作”只用于理解当前教程画面里演示了什么。"
                    + "它们是预先编排的演示，不代表客人自己的操作记录，也不代表系统支持玩家拖动或点击组件。"
                    + "回答时请顺着客人问题的正常意图解释规则或画面，不要主动补充“这不是玩家真实操作”之类的话；"
                    + "只有客人明确把动画动作当成自己的操作或当前游戏状态时，才简短说明这是教程演示。");
            }

            builder.AppendLine();
            builder.AppendLine("最近观看内容（按时间顺序，最后一条是客人提问时的当前 cue）：");

            foreach (var cue in recentCues)
            {
                AppendRecentCue(builder, cue);
            }

            logger.LogInformation(
                "[Chat] context injected: cues={Count}, current={CueId}",
                recentCues.Count,
                context.CueId ?? recentCues.LastOrDefault()?.Id ?? "");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendRecentCue(StringBuilder builder, RecentCueContext cue)
    {
        var indexText = cue.Index?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var path = CleanPath(cue.GroupPath);

        var header = new StringBuilder();
        header.Append("[cue ").Append(indexText).Append(']');
        if (cue.IsCurrent) header.Append(" 当前 cue");
        if (path.Count > 0)
            header.Append(cue.IsCurrent ? "：" : " ").Append(string.Join(" > ", path));
        builder.AppendLine(header.ToString());

        if (!string.IsNullOrWhiteSpace(cue.Text))
            builder.AppendLine($"口播：{Truncate(cue.Text.Trim(), 300)}");

        var refs = cue.Refs?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();
        if (refs?.Count > 0)
            builder.AppendLine($"引用：{string.Join("、", refs)}");

        var actions = cue.Actions?
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .Take(8)
            .ToList();
        if (actions?.Count > 0)
        {
            builder.AppendLine("动画脚本动作（演示，非玩家手动操作）：");
            foreach (var action in actions)
                builder.AppendLine($"- {action}");
        }

        builder.AppendLine();
    }

    private static bool HasRecentCueContent(RecentCueContext cue)
    {
        return !string.IsNullOrWhiteSpace(cue.Id)
            || cue.Index.HasValue
            || !string.IsNullOrWhiteSpace(cue.Text)
            || cue.GroupPath?.Any(part => !string.IsNullOrWhiteSpace(part)) == true
            || cue.Refs?.Any(item => !string.IsNullOrWhiteSpace(item)) == true
            || cue.Actions?.Any(action => !string.IsNullOrWhiteSpace(action)) == true
            || cue.Start.HasValue
            || cue.Duration.HasValue;
    }

    private static List<string> CleanPath(List<string>? path)
    {
        return path?
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList() ?? new List<string>();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength) return value;
        return value[..maxLength] + "…";
    }
}
