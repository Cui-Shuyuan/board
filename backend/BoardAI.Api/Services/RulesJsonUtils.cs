using System.Text.RegularExpressions;

namespace BoardAI.Api.Services;

internal static class RulesJsonUtils
{
    public static readonly Regex ConceptRefRegex = new(
        @"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>",
        RegexOptions.Compiled);
}
