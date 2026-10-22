namespace BoardAI.Api.Models;

public class RulesOptions
{
    public string BasePath { get; set; } = string.Empty;

    /// <summary>Maps to Rules:MemoryCache in appsettings.json.</summary>
    public RulesMemoryCacheOptions MemoryCache { get; set; } = new();

    /// <summary>Maps to Rules:DiskCache in appsettings.json.</summary>
    public RulesDiskCacheOptions DiskCache { get; set; } = new();

    public void Validate()
    {
        MemoryCache.Validate();
        DiskCache.Validate();
    }
}
