namespace BoardAI.Api.Models;

public class RulesOptions
{
    public string BasePath { get; set; } = string.Empty;

    public RulesCacheOptions Cache { get; set; } = new();
}
