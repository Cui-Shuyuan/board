using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

namespace BoardAI.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        // ---- CLI 模式：重建索引 ----
        if (args.Length > 0 && args[0] == "--rebuild-all")
        {
            await RunRebuildAll();
            return;
        }
        if (args.Length > 1 && args[0] == "--rebuild-index")
        {
            await RunRebuildGame(args[1]);
            return;
        }

        // ---- 正常模式：启动 Web 服务 ----
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<LLMOptions>(
            builder.Configuration.GetSection("LLM"));
        builder.Services.Configure<RulesOptions>(
            builder.Configuration.GetSection("Rules"));

        var modelDir = builder.Configuration.GetValue<string>("Embedding:ModelDir")
            ?? Path.Combine(builder.Environment.ContentRootPath, "ml_models", "bge-small-zh");
        var embedder = new EmbeddingService(modelDir);
        builder.Services.AddSingleton(embedder);

        var qdrantHost = builder.Configuration.GetValue<string>("Qdrant:Host") ?? "localhost";
        var qdrantPort = builder.Configuration.GetValue<int?>("Qdrant:Port") ?? 6334;
        builder.Services.AddSingleton<VectorSearchService>(sp =>
            new VectorSearchService(qdrantHost, qdrantPort,
                sp.GetRequiredService<EmbeddingService>(),
                sp.GetRequiredService<ILogger<VectorSearchService>>()));

        builder.Services.AddSingleton<GameRulesService>();
        builder.Services.AddScoped<ChatOrchestratorService>();
        builder.Services.AddHttpClient<ILLMService, DeepSeekLLMService>();
        builder.Services.AddControllers();

        var app = builder.Build();

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapControllers();

        app.Run();
    }

    // ---- CLI 重建逻辑 ----

    private static async Task RunRebuildAll()
    {
        var (rulesService, _, _) = CreateRebuildServices();
        var games = rulesService.GetGames();
        Console.WriteLine($"Rebuilding index for {games.Count} game(s)...");
        foreach (var game in games)
        {
            await rulesService.BuildEmbeddingIndexAsync(game);
            Console.WriteLine($"  ✓ {game}");
        }
        Console.WriteLine("Done.");
    }

    private static async Task RunRebuildGame(string gameId)
    {
        var (rulesService, _, _) = CreateRebuildServices();
        Console.WriteLine($"Rebuilding index for '{gameId}'...");
        await rulesService.BuildEmbeddingIndexAsync(gameId);
        Console.WriteLine("Done.");
    }

    private static (GameRulesService, EmbeddingService, VectorSearchService) CreateRebuildServices()
    {
        // 手动读取配置，不走 WebApplication 那套
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var rulesOptions = Options.Create(new RulesOptions
        {
            BasePath = config.GetValue<string>("Rules:BasePath") ?? string.Empty
        });

        var modelDir = config.GetValue<string>("Embedding:ModelDir")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "ml_models", "bge-small-zh");
        var embedder = new EmbeddingService(modelDir);

        var qdrantHost = config.GetValue<string>("Qdrant:Host") ?? "localhost";
        var qdrantPort = config.GetValue<int?>("Qdrant:Port") ?? 6334;
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var vectorSearch = new VectorSearchService(qdrantHost, qdrantPort, embedder,
            loggerFactory.CreateLogger<VectorSearchService>());

        var rulesService = new GameRulesService(rulesOptions, vectorSearch);

        return (rulesService, embedder, vectorSearch);
    }
}
