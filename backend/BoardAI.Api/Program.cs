using BoardAI.Api.Models;
using BoardAI.Api.Services;

namespace BoardAI.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<LLMOptions>(
            builder.Configuration.GetSection("LLM"));
        builder.Services.Configure<RulesOptions>(
            builder.Configuration.GetSection("Rules"));

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
}
