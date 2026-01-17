using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Application.Data;

namespace Promptly.Application.Services;

public class MappingService : IMappingService
{
    private readonly IJsonPathService _jsonPathService;
    private readonly ILogger<MappingService> _logger;
    private readonly PromptlyDbContext _dbContext;
    private const int MaxRawResponseSize = 200 * 1024; // 200KB

    public MappingService(IJsonPathService jsonPathService, ILogger<MappingService> logger, PromptlyDbContext dbContext)
    {
        _jsonPathService = jsonPathService;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson)
    {
        return await Task.Run(() => ApplyMapping(mappingSpecJson, responseJson));
    }

    public async Task<MappingResult> ValidateMappingAsync(string mappingSpecJson, string sampleResponseJson)
    {
        return await ApplyMappingAsync(mappingSpecJson, sampleResponseJson);
    }

    private MappingResult ApplyMapping(string mappingSpecJson, string responseJson)
    {
        try
        {
            var mappingSpec = JsonSerializer.Deserialize<MappingSpecDefinition>(mappingSpecJson);
            if (mappingSpec == null)
            {
                return new MappingResult
                {
                    Success = false,
                    ErrorMessage = "Invalid mapping spec JSON"
                };
            }

            var trace = new CanonicalTrace
            {
                Messages = ExtractMessages(mappingSpec, responseJson),
                ToolCalls = ExtractToolCalls(mappingSpec, responseJson),
                Usage = ExtractUsage(mappingSpec, responseJson),
                RetrievedDocs = ExtractRetrievedDocs(mappingSpec, responseJson),
                RawResponse = CapRawResponse(responseJson)
            };

            return new MappingResult
            {
                Success = true,
                Trace = trace
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply mapping spec");
            return new MappingResult
            {
                Success = false,
                ErrorMessage = $"Mapping failed: {ex.Message}"
            };
        }
    }

    private List<Message> ExtractMessages(MappingSpecDefinition spec, string responseJson)
    {
        var messages = new List<Message>();

        try
        {
            if (spec.Messages != null && !string.IsNullOrWhiteSpace(spec.Messages.ItemsPath))
            {
                var messageItems = _jsonPathService.SelectTokens(responseJson, spec.Messages.ItemsPath);

                foreach (var item in messageItems)
                {
                    var itemJson = item.GetRawText();
                    var role = ExtractStringValue(itemJson, spec.Messages.RolePath) ?? "assistant";
                    var content = ExtractContentValue(itemJson, spec.Messages.ContentPath) ?? "";

                    messages.Add(new Message { Role = role, Content = content });
                }

                return messages;
            }

            // Fallback: try to get single assistant content
            if (spec.Fallback != null && !string.IsNullOrWhiteSpace(spec.Fallback.SingleAssistantContentPath))
            {
                var content = ExtractStringValue(responseJson, spec.Fallback.SingleAssistantContentPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    messages.Add(new Message { Role = "assistant", Content = content });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract messages, using empty list");
        }

        return messages;
    }

    private List<ToolCall> ExtractToolCalls(MappingSpecDefinition spec, string responseJson)
    {
        var toolCalls = new List<ToolCall>();

        try
        {
            if (spec.ToolCalls != null && !string.IsNullOrWhiteSpace(spec.ToolCalls.ItemsPath))
            {
                var toolCallItems = _jsonPathService.SelectTokens(responseJson, spec.ToolCalls.ItemsPath);

                foreach (var item in toolCallItems)
                {
                    var itemJson = item.GetRawText();
                    var name = ExtractStringValue(itemJson, spec.ToolCalls.NamePath) ?? "";
                    var arguments = ExtractArgumentsValue(itemJson, spec.ToolCalls.ArgumentsPath) ?? "{}";

                    toolCalls.Add(new ToolCall { Name = name, ArgumentsJson = arguments });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract tool calls, using empty list");
        }

        return toolCalls;
    }

    private Usage? ExtractUsage(MappingSpecDefinition spec, string responseJson)
    {
        try
        {
            if (spec.Usage != null && !string.IsNullOrWhiteSpace(spec.Usage.ObjectPath))
            {
                var usageToken = _jsonPathService.SelectToken(responseJson, spec.Usage.ObjectPath);
                if (usageToken == null)
                {
                    return null;
                }

                var usageJson = usageToken.Value.GetRawText();

                return new Usage
                {
                    PromptTokens = ExtractIntValue(usageJson, spec.Usage.PromptTokensPath),
                    CompletionTokens = ExtractIntValue(usageJson, spec.Usage.CompletionTokensPath),
                    TotalTokens = ExtractIntValue(usageJson, spec.Usage.TotalTokensPath),
                    Cost = ExtractDecimalValue(usageJson, spec.Usage.CostPath),
                    LatencyMs = ExtractLongValue(usageJson, spec.Usage.LatencyMsPath)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract usage, returning null");
        }

        return null;
    }

    private List<RetrievedDoc> ExtractRetrievedDocs(MappingSpecDefinition spec, string responseJson)
    {
        var docs = new List<RetrievedDoc>();

        try
        {
            if (spec.RetrievedDocs != null && !string.IsNullOrWhiteSpace(spec.RetrievedDocs.ItemsPath))
            {
                var docItems = _jsonPathService.SelectTokens(responseJson, spec.RetrievedDocs.ItemsPath);

                foreach (var item in docItems)
                {
                    var itemJson = item.GetRawText();
                    var id = ExtractStringValue(itemJson, spec.RetrievedDocs.IdPath);
                    var title = ExtractStringValue(itemJson, spec.RetrievedDocs.TitlePath);
                    var content = ExtractStringValue(itemJson, spec.RetrievedDocs.ContentPath) ?? "";

                    Dictionary<string, object>? metadata = null;
                    if (!string.IsNullOrWhiteSpace(spec.RetrievedDocs.MetadataPath))
                    {
                        var metadataToken = _jsonPathService.SelectToken(itemJson, spec.RetrievedDocs.MetadataPath);
                        if (metadataToken != null)
                        {
                            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataToken.Value.GetRawText());
                        }
                    }

                    docs.Add(new RetrievedDoc
                    {
                        Id = id,
                        Title = title,
                        Content = content,
                        Metadata = metadata
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract retrieved docs, using empty list");
        }

        return docs;
    }

    private string? ExtractStringValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            return token.Value.ValueKind == JsonValueKind.String
                ? token.Value.GetString()
                : token.Value.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    private string? ExtractContentValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            // If content is a string, return it
            if (token.Value.ValueKind == JsonValueKind.String)
            {
                return token.Value.GetString();
            }

            // If content is an array or object, stringify it
            if (token.Value.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in token.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(item.GetString() ?? "");
                    }
                    else
                    {
                        parts.Add(item.GetRawText());
                    }
                }
                return string.Join("\n", parts);
            }

            return token.Value.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    private string? ExtractArgumentsValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            // If already a string, return it
            if (token.Value.ValueKind == JsonValueKind.String)
            {
                return token.Value.GetString();
            }

            // If object or array, serialize to JSON string
            return token.Value.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    private int? ExtractIntValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            if (token.Value.ValueKind == JsonValueKind.Number && token.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private long? ExtractLongValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            if (token.Value.ValueKind == JsonValueKind.Number && token.Value.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private decimal? ExtractDecimalValue(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var token = _jsonPathService.SelectToken(json, path);
            if (token == null)
            {
                return null;
            }

            if (token.Value.ValueKind == JsonValueKind.Number && token.Value.TryGetDecimal(out var value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private string? CapRawResponse(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        if (responseJson.Length > MaxRawResponseSize)
        {
            return responseJson.Substring(0, MaxRawResponseSize) + "\n... (truncated)";
        }

        return responseJson;
    }

    // MappingSpec CRUD operations
    public async Task<MappingSpec> SaveMappingSpecAsync(Guid endpointId, string name, string specJson)
    {
        var mappingSpec = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = endpointId,
            Name = name,
            SpecJson = specJson,
            IsDefault = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.MappingSpecs.Add(mappingSpec);
        await _dbContext.SaveChangesAsync();

        return mappingSpec;
    }

    public async Task<List<MappingSpec>> GetMappingSpecsByEndpointAsync(Guid endpointId)
    {
        return await _dbContext.MappingSpecs
            .Where(m => m.EndpointId == endpointId)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id)
    {
        return await _dbContext.MappingSpecs.FindAsync(id);
    }

    public async Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId)
    {
        return await _dbContext.MappingSpecs
            .Where(m => m.EndpointId == endpointId && m.IsDefault)
            .FirstOrDefaultAsync();
    }

    public async Task<MappingSpec> UpdateMappingSpecAsync(Guid id, string name, string specJson)
    {
        var mappingSpec = await _dbContext.MappingSpecs.FindAsync(id);
        if (mappingSpec == null)
        {
            throw new InvalidOperationException($"MappingSpec with ID {id} not found");
        }

        mappingSpec.Name = name;
        mappingSpec.SpecJson = specJson;
        mappingSpec.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return mappingSpec;
    }

    public async Task SetDefaultMappingAsync(Guid id)
    {
        var mappingSpec = await _dbContext.MappingSpecs.FindAsync(id);
        if (mappingSpec == null)
        {
            throw new InvalidOperationException($"MappingSpec with ID {id} not found");
        }

        // Clear any existing default for this endpoint
        var existingDefaults = await _dbContext.MappingSpecs
            .Where(m => m.EndpointId == mappingSpec.EndpointId && m.IsDefault)
            .ToListAsync();

        foreach (var existing in existingDefaults)
        {
            existing.IsDefault = false;
        }

        // Set this one as default
        mappingSpec.IsDefault = true;
        mappingSpec.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteMappingSpecAsync(Guid id)
    {
        var mappingSpec = await _dbContext.MappingSpecs.FindAsync(id);
        if (mappingSpec == null)
        {
            throw new InvalidOperationException($"MappingSpec with ID {id} not found");
        }

        _dbContext.MappingSpecs.Remove(mappingSpec);
        await _dbContext.SaveChangesAsync();
    }
}
