using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

// No model or vector service: isolate backend keyword retrieval with real data.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
using var rules = new GameRulesService(Options.Create(new RulesOptions { BasePath = root }));
var queries = new[] { "贵族", "获得", "费用", "行动", "回合 结束", "卡牌", "资源", "ontology::token", "不存在的对象", "金币" };
var output = new List<object>();
foreach (var game in rules.GetGames().Order())
{
    foreach (var query in queries) await rules.SearchConceptsAsync(game, query);
    var times = new List<double>();
    var results = new StringBuilder();
    long allocated = GC.GetTotalAllocatedBytes(true);
    for (int repeat = 0; repeat < 5; repeat++)
    foreach (var query in queries)
    {
        var timer = Stopwatch.StartNew();
        var result = await rules.SearchConceptsAsync(game, query);
        times.Add(timer.Elapsed.TotalMilliseconds);
        results.Append(JsonSerializer.Serialize(result));
    }
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    times.Sort();
    output.Add(new { game, mean_ms = times.Average(), p50_ms = times[times.Count / 2], p95_ms = times[(int)(times.Count * .95)], bytes_per_search = allocated / times.Count,
        sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(results.ToString()))) });
}
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
