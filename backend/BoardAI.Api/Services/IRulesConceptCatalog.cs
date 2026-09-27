using System.Text.Json;

namespace BoardAI.Api.Services;

public interface IRulesConceptCatalog
{
    IReadOnlyList<string> GetConceptTypes(string game);

    IReadOnlyList<ConceptSummary> ListConcepts(string game, string type);

    JsonElement? GetConcept(string game, string id);
}
