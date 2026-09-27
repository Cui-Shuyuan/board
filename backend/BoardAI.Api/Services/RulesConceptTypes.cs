using System.Text.Json;

namespace BoardAI.Api.Services;

internal static class RulesConceptTypes
{
    public static readonly string[] ConceptArrayTypes =
        { "objects", "actions", "triggers", "conditions" };

    public static readonly string[] InstanceArrayTypes =
        { "effects", "modules", "cards", "continent_tiles", "sites", "chips" };

    /// <summary>
    /// 独立流程概念节点判据：带 specifies/extends/instance_of 的节点才是可被搜索的概念；
    /// 仅 id+name 的节点是 pipeline 局部步骤（do_after 引用名），不入搜索索引与目录。
    /// </summary>
    public static bool IsStandaloneFlowNode(JsonElement node) =>
        node.TryGetProperty("specifies", out _)
        || node.TryGetProperty("extends", out _)
        || node.TryGetProperty("instance_of", out _);
}
