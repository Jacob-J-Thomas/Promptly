using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;

namespace Promptly.Infrastructure.Services;

public class JsonPathService : IJsonPathService
{
    private readonly ILogger<JsonPathService> _logger;

    public JsonPathService(ILogger<JsonPathService> logger)
    {
        _logger = logger;
    }

    public JsonElement? SelectToken(string jsonString, string jsonPath)
    {
        try
        {
            var jsonNode = JsonNode.Parse(jsonString);
            if (jsonNode == null)
            {
                return null;
            }

            var path = JsonPath.Parse(jsonPath);
            var result = path.Evaluate(jsonNode);

            if (result.Matches == null || !result.Matches.Any())
            {
                return null;
            }

            var firstMatch = result.Matches.First().Value;
            if (firstMatch == null)
            {
                return null;
            }

            // Convert JsonNode back to JsonElement
            var matchJson = firstMatch.ToJsonString();
            using var doc = JsonDocument.Parse(matchJson);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select token using JSONPath {JsonPath}", jsonPath);
            throw new InvalidOperationException($"Failed to evaluate JSONPath '{jsonPath}': {ex.Message}", ex);
        }
    }

    public IEnumerable<JsonElement> SelectTokens(string jsonString, string jsonPath)
    {
        try
        {
            var jsonNode = JsonNode.Parse(jsonString);
            if (jsonNode == null)
            {
                return Enumerable.Empty<JsonElement>();
            }

            var path = JsonPath.Parse(jsonPath);
            var result = path.Evaluate(jsonNode);

            if (result.Matches == null)
            {
                return Enumerable.Empty<JsonElement>();
            }

            // Convert each JsonNode match back to JsonElement
            return result.Matches
                .Where(m => m.Value != null)
                .Select(m =>
                {
                    var matchJson = m.Value!.ToJsonString();
                    using var doc = JsonDocument.Parse(matchJson);
                    return doc.RootElement.Clone();
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select tokens using JSONPath {JsonPath}", jsonPath);
            throw new InvalidOperationException($"Failed to evaluate JSONPath '{jsonPath}': {ex.Message}", ex);
        }
    }
}
