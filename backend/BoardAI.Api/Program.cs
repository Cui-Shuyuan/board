using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace BoardAI.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        // ---- CLI 模式：重建索引（默认增量，--full 强制全量）----
        if (args.Length > 0 && args[0] == "--rebuild-all")
        {
            await RunRebuildAll(full: args.Contains("--full"));
            return;
        }
        if (args.Length > 1 && args[0] == "--rebuild-index")
        {
            await RunRebuildGame(args[1], full: args.Contains("--full"));
            return;
        }

        // ---- 正常模式：启动 Web 服务 ----
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<LLMOptions>(
            builder.Configuration.GetSection("LLM"));
        builder.Services.Configure<RulesOptions>(
            builder.Configuration.GetSection("Rules"));

        // 自定义 Console Formatter：每行日志带请求 ID
        builder.Logging.AddConsoleFormatter<RequestIdConsoleFormatter, SimpleConsoleFormatterOptions>();

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

        // 本地模型走 OpenAI 兼容接口（Ollama / llama.cpp / vLLM 等）；
        // DeepSeek 保持原实现，配置在 appsettings*.json 的 LLM:Provider。
        var llmProvider = builder.Configuration.GetValue<string>("LLM:Provider") ?? "DeepSeek";
        if (llmProvider.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || llmProvider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHttpClient<ILLMService, OpenAICompatibleLLMService>();
        }
        else
        {
            builder.Services.AddHttpClient<ILLMService, DeepSeekLLMService>();
        }

        builder.Services.AddControllers();

        var app = builder.Build();

        app.UseMiddleware<RequestIdMiddleware>();

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // 暴露 games/ 目录下的图片等媒体资源
        var gamesPath = Path.Combine(
            builder.Configuration.GetValue<string>("Rules:BasePath") ?? builder.Environment.ContentRootPath,
            "games");
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(gamesPath),
            RequestPath = "/games"
        });

        app.MapControllers();

        app.Run();
    }

    // ---- CLI 重建逻辑 ----

    private static async Task RunRebuildAll(bool full)
    {
        var (rulesService, _, _) = CreateRebuildServices();
        var games = rulesService.GetGames();
        var mode = full ? "full" : "incremental";
        Console.WriteLine($"Syncing index for {games.Count} game(s) [{mode}]...");
        foreach (var game in games)
        {
            if (full)
                await rulesService.BuildEmbeddingIndexAsync(game);
            else
                await rulesService.SyncEmbeddingIndexAsync(game);
            Console.WriteLine($"  ✓ {game}");
        }
        Console.WriteLine("Done.");
    }

    private static async Task RunRebuildGame(string gameId, bool full)
    {
        var (rulesService, _, _) = CreateRebuildServices();
        var mode = full ? "full" : "incremental";
        Console.WriteLine($"Syncing index for '{gameId}' [{mode}]...");
        if (full)
            await rulesService.BuildEmbeddingIndexAsync(gameId);
        else
            await rulesService.SyncEmbeddingIndexAsync(gameId);
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
