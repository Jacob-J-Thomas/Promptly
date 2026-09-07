using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;

namespace Promptly.Application.Services;

public class MappingService : IMappingService
{
    private readonly IJsonPathService _jsonPathService;
    private readonly ILogger<MappingService> _logger;
    private readonly PromptlyDbContext _dbContext;
    private const int MaxRawResponseSize = 200 * 1024; // 200KB
    private const long MaxCompatibleJsonPathIndex = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions MappingSpecJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private const string CompatibleJsonPathPattern =
        """^\$(?:\.[A-Za-z_][A-Za-z0-9_]*|\[(?:0|[1-9][0-9]*|\*)\]|\["[^"\\\u0000-\u001F]*"\]|\['[^'\\\u0000-\u001F]*'\])*$""";
    private static readonly Regex CompatibleJsonPathRegex = new(
        CompatibleJsonPathPattern,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

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
            var mappingSpec = DeserializeAndValidateMappingSpec(mappingSpecJson);
            ValidateResponseJson(responseJson);

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
        catch (MappingSpecValidationException ex)
        {
            _logger.LogWarning(ex, "Invalid mapping specification at {MappingPath}", ex.Path);
            return new MappingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorPath = ex.Path
            };
        }
        catch (MappingException ex)
        {
            _logger.LogWarning(ex, "Mapping validation failed at {MappingPath}", ex.Path);
            return new MappingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ErrorPath = ex.Path
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply mapping spec");
            return new MappingResult
            {
                Success = false,
                ErrorMessage = "Mapping failed due to an unexpected error"
            };
        }
    }

    private MappingSpecDefinition DeserializeAndValidateMappingSpec(string mappingSpecJson)
    {
        if (string.IsNullOrWhiteSpace(mappingSpecJson))
        {
            throw new MappingSpecValidationException(
                "mappingSpec",
                "a schema-1 JSON object is required");
        }

        MappingSpecDefinition? spec;
        try
        {
            using var document = JsonDocument.Parse(mappingSpecJson);
            if (ContainsDuplicateObjectProperty(document.RootElement))
            {
                throw new MappingSpecValidationException(
                    "mappingSpec",
                    "the JSON contains duplicate property names");
            }

            spec = document.RootElement.Deserialize<MappingSpecDefinition>(MappingSpecJsonOptions);
        }
        catch (MappingSpecValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new MappingSpecValidationException(
                ex is JsonException jsonException
                    ? NormalizeJsonExceptionPath(jsonException.Path)
                    : "mappingSpec",
                "the JSON does not match schema 1",
                ex);
        }

        if (spec == null)
        {
            throw new MappingSpecValidationException(
                "mappingSpec",
                "a schema-1 JSON object is required");
        }

        ValidateMappingSpec(spec);
        return spec;
    }

    private static void ValidateResponseJson(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (ContainsDuplicateObjectProperty(document.RootElement))
            {
                throw new MappingException(
                    "responseJson",
                    "response JSON contains duplicate property names");
            }
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new MappingException("responseJson", "response is not valid JSON", ex);
        }
    }

    private static bool ContainsDuplicateObjectProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || ContainsDuplicateObjectProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.EnumerateArray().Any(ContainsDuplicateObjectProperty))
            {
                return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            _ = element.GetString();
        }

        return false;
    }

    private static string NormalizeJsonExceptionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return "mappingSpec";
        }

        return path.StartsWith("$.", StringComparison.Ordinal)
            ? path[2..]
            : path.TrimStart('$');
    }

    private void ValidateMappingSpec(MappingSpecDefinition spec)
    {
        if (spec.Messages == null
            && spec.ToolCalls == null
            && spec.Usage == null
            && spec.RetrievedDocs == null
            && spec.Fallback == null)
        {
            throw new MappingSpecValidationException(
                "mappingSpec",
                "at least one extraction section is required");
        }

        if (spec.Version != 1)
        {
            var versionDetail = spec.Version == null
                ? "the required schema version is missing"
                : $"unsupported mapping version {spec.Version}; only schema 1 is supported";
            throw new MappingSpecValidationException(
                "version",
                versionDetail);
        }

        if (spec.Messages != null)
        {
            RequirePaths(
                (spec.Messages.ItemsPath, "messages.itemsPath"),
                (spec.Messages.RolePath, "messages.rolePath"),
                (spec.Messages.ContentPath, "messages.contentPath"));
        }

        if (spec.ToolCalls != null)
        {
            RequirePaths(
                (spec.ToolCalls.ItemsPath, "toolCalls.itemsPath"),
                (spec.ToolCalls.NamePath, "toolCalls.namePath"),
                (spec.ToolCalls.ArgumentsPath, "toolCalls.argumentsPath"));
        }

        if (spec.Usage != null)
        {
            RequirePaths((spec.Usage.ObjectPath, "usage.objectPath"));
            (string? Path, string Label)[] metricPaths =
            [
                (spec.Usage.PromptTokensPath, "usage.promptTokensPath"),
                (spec.Usage.CompletionTokensPath, "usage.completionTokensPath"),
                (spec.Usage.TotalTokensPath, "usage.totalTokensPath"),
                (spec.Usage.CostPath, "usage.costPath"),
                (spec.Usage.LatencyMsPath, "usage.latencyMsPath")
            ];
            if (metricPaths.All(metric => metric.Path == null))
            {
                throw new MappingSpecValidationException(
                    "usage",
                    "at least one metric path is required");
            }

            ValidateOptionalPaths(metricPaths);
        }

        if (spec.RetrievedDocs != null)
        {
            RequirePaths(
                (spec.RetrievedDocs.ItemsPath, "retrievedDocs.itemsPath"),
                (spec.RetrievedDocs.ContentPath, "retrievedDocs.contentPath"));
            ValidateOptionalPaths(
                (spec.RetrievedDocs.IdPath, "retrievedDocs.idPath"),
                (spec.RetrievedDocs.TitlePath, "retrievedDocs.titlePath"),
                (spec.RetrievedDocs.MetadataPath, "retrievedDocs.metadataPath"));
        }

        if (spec.Fallback != null)
        {
            RequirePaths((
                spec.Fallback.SingleAssistantContentPath,
                "fallback.singleAssistantContentPath"));
        }

        ValidateJsonPathSyntax(GetConfiguredPaths(spec));
    }

    private static void RequirePaths(params (string? Path, string Label)[] paths)
    {
        foreach (var (path, label) in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new MappingSpecValidationException(
                    label,
                    "a non-empty JSONPath is required");
            }
        }
    }

    private static void ValidateOptionalPaths(params (string? Path, string Label)[] paths)
    {
        foreach (var (path, label) in paths)
        {
            if (path != null && string.IsNullOrWhiteSpace(path))
            {
                throw new MappingSpecValidationException(
                    label,
                    "the JSONPath cannot be empty");
            }
        }
    }

    private void ValidateJsonPathSyntax(
        IEnumerable<(string? Path, string Label)> configuredPaths)
    {
        foreach (var (path, label) in configuredPaths.Where(item => item.Path != null))
        {
            if (!CompatibleJsonPathRegex.IsMatch(path!) || UsesUnsupportedArrayIndex(path!))
            {
                throw new MappingSpecValidationException(
                    label,
                    $"'{path}' uses unsupported JSONPath syntax");
            }

            try
            {
                _jsonPathService.SelectToken("{}", path!);
            }
            catch (InvalidOperationException ex)
            {
                throw new MappingSpecValidationException(
                    label,
                    $"'{path}' is not a valid JSONPath",
                    ex);
            }
        }
    }

    private static bool UsesUnsupportedArrayIndex(string path)
    {
        var position = 1;
        while (position < path.Length)
        {
            if (path[position] == '.')
            {
                position++;
                while (position < path.Length && path[position] != '.' && path[position] != '[')
                {
                    position++;
                }

                continue;
            }

            var selectorStart = position + 1;
            if (path[selectorStart] is '"' or '\'')
            {
                var quote = path[selectorStart];
                position = path.IndexOf(quote, selectorStart + 1) + 2;
                continue;
            }

            var selectorEnd = path.IndexOf(']', selectorStart);
            var selector = path.AsSpan(selectorStart, selectorEnd - selectorStart);
            if (selector[0] != '*'
                && (!long.TryParse(selector, out var index)
                    || index > MaxCompatibleJsonPathIndex))
            {
                return true;
            }

            position = selectorEnd + 1;
        }

        return false;
    }

    private static IEnumerable<(string? Path, string Label)> GetConfiguredPaths(
        MappingSpecDefinition spec)
    {
        if (spec.Messages != null)
        {
            yield return (spec.Messages.ItemsPath, "messages.itemsPath");
            yield return (spec.Messages.RolePath, "messages.rolePath");
            yield return (spec.Messages.ContentPath, "messages.contentPath");
        }

        if (spec.ToolCalls != null)
        {
            yield return (spec.ToolCalls.ItemsPath, "toolCalls.itemsPath");
            yield return (spec.ToolCalls.NamePath, "toolCalls.namePath");
            yield return (spec.ToolCalls.ArgumentsPath, "toolCalls.argumentsPath");
        }

        if (spec.Usage != null)
        {
            yield return (spec.Usage.ObjectPath, "usage.objectPath");
            yield return (spec.Usage.PromptTokensPath, "usage.promptTokensPath");
            yield return (spec.Usage.CompletionTokensPath, "usage.completionTokensPath");
            yield return (spec.Usage.TotalTokensPath, "usage.totalTokensPath");
            yield return (spec.Usage.CostPath, "usage.costPath");
            yield return (spec.Usage.LatencyMsPath, "usage.latencyMsPath");
        }

        if (spec.RetrievedDocs != null)
        {
            yield return (spec.RetrievedDocs.ItemsPath, "retrievedDocs.itemsPath");
            yield return (spec.RetrievedDocs.IdPath, "retrievedDocs.idPath");
            yield return (spec.RetrievedDocs.TitlePath, "retrievedDocs.titlePath");
            yield return (spec.RetrievedDocs.ContentPath, "retrievedDocs.contentPath");
            yield return (spec.RetrievedDocs.MetadataPath, "retrievedDocs.metadataPath");
        }

        if (spec.Fallback != null)
        {
            yield return (
                spec.Fallback.SingleAssistantContentPath,
                "fallback.singleAssistantContentPath");
        }
    }

    private List<Message> ExtractMessages(MappingSpecDefinition spec, string responseJson)
    {
        var messages = new List<Message>();

        if (spec.Messages != null)
        {
            try
            {
                var messageItems = SelectRequiredTokens(
                    responseJson,
                    spec.Messages.ItemsPath!,
                    "messages.itemsPath");
                RequireObjectItems(messageItems, spec.Messages.ItemsPath!, "messages.itemsPath");

                foreach (var item in messageItems)
                {
                    var itemJson = item.GetRawText();
                    var role = ExtractRequiredStringValue(
                        itemJson,
                        spec.Messages.RolePath,
                        "messages.rolePath");
                    var content = ExtractRequiredContentValue(
                        itemJson,
                        spec.Messages.ContentPath,
                        "messages.contentPath");

                    messages.Add(new Message { Role = role, Content = content });
                }

                return messages;
            }
            catch (MappingException ex) when (spec.Fallback != null)
            {
                _logger.LogWarning(ex, "Messages mapping failed; applying configured fallback");
                messages.Clear();
            }
        }

        if (spec.Fallback != null)
        {
            var content = ExtractRequiredStringValue(
                responseJson,
                spec.Fallback.SingleAssistantContentPath!,
                "fallback.singleAssistantContentPath");
            messages.Add(new Message { Role = "assistant", Content = content });
        }

        return messages;
    }

    private List<ToolCall> ExtractToolCalls(MappingSpecDefinition spec, string responseJson)
    {
        if (spec.ToolCalls == null)
        {
            return [];
        }

        var toolCallItems = SelectRequiredTokens(
            responseJson,
            spec.ToolCalls.ItemsPath!,
            "toolCalls.itemsPath");
        RequireObjectItems(toolCallItems, spec.ToolCalls.ItemsPath!, "toolCalls.itemsPath");
        return toolCallItems.Select(item =>
        {
            var itemJson = item.GetRawText();
            return new ToolCall
            {
                Name = ExtractRequiredStringValue(
                    itemJson,
                    spec.ToolCalls.NamePath,
                    "toolCalls.namePath"),
                ArgumentsJson = ExtractRequiredArgumentsValue(
                    itemJson,
                    spec.ToolCalls.ArgumentsPath,
                    "toolCalls.argumentsPath")
            };
        }).ToList();
    }

    private Usage? ExtractUsage(MappingSpecDefinition spec, string responseJson)
    {
        if (spec.Usage == null)
        {
            return null;
        }

        var usageObject = SelectRequiredToken(
            responseJson,
            spec.Usage.ObjectPath!,
            "usage.objectPath");
        if (usageObject.ValueKind != JsonValueKind.Object)
        {
            throw new MappingException(
                "usage.objectPath",
                $"JSONPath '{spec.Usage.ObjectPath}' did not match an object");
        }

        var usageJson = usageObject.GetRawText();
        return new Usage
        {
            PromptTokens = ExtractConfiguredIntValue(
                usageJson,
                spec.Usage.PromptTokensPath,
                "usage.promptTokensPath"),
            CompletionTokens = ExtractConfiguredIntValue(
                usageJson,
                spec.Usage.CompletionTokensPath,
                "usage.completionTokensPath"),
            TotalTokens = ExtractConfiguredIntValue(
                usageJson,
                spec.Usage.TotalTokensPath,
                "usage.totalTokensPath"),
            Cost = ExtractConfiguredDecimalValue(
                usageJson,
                spec.Usage.CostPath,
                "usage.costPath"),
            LatencyMs = ExtractConfiguredLongValue(
                usageJson,
                spec.Usage.LatencyMsPath,
                "usage.latencyMsPath")
        };
    }

    private List<RetrievedDoc> ExtractRetrievedDocs(MappingSpecDefinition spec, string responseJson)
    {
        if (spec.RetrievedDocs == null)
        {
            return [];
        }

        var docItems = SelectRequiredTokens(
            responseJson,
            spec.RetrievedDocs.ItemsPath!,
            "retrievedDocs.itemsPath");
        RequireObjectItems(docItems, spec.RetrievedDocs.ItemsPath!, "retrievedDocs.itemsPath");
        RequireOptionalPathMatch(
            docItems,
            spec.RetrievedDocs.IdPath,
            "retrievedDocs.idPath");
        RequireOptionalPathMatch(
            docItems,
            spec.RetrievedDocs.TitlePath,
            "retrievedDocs.titlePath");
        RequireOptionalPathMatch(
            docItems,
            spec.RetrievedDocs.MetadataPath,
            "retrievedDocs.metadataPath");
        return docItems.Select(item =>
        {
            var itemJson = item.GetRawText();
            return new RetrievedDoc
            {
                Id = ExtractOptionalIdValue(
                    itemJson,
                    spec.RetrievedDocs.IdPath,
                    "retrievedDocs.idPath"),
                Title = ExtractOptionalStringValue(
                    itemJson,
                    spec.RetrievedDocs.TitlePath,
                    "retrievedDocs.titlePath"),
                Content = ExtractRequiredStringValue(
                    itemJson,
                    spec.RetrievedDocs.ContentPath!,
                    "retrievedDocs.contentPath"),
                Metadata = ExtractOptionalMetadataValue(
                    itemJson,
                    spec.RetrievedDocs.MetadataPath,
                    "retrievedDocs.metadataPath")
            };
        }).ToList();
    }

    private void RequireOptionalPathMatch(
        IReadOnlyList<JsonElement> items,
        string? path,
        string label)
    {
        if (path == null)
        {
            return;
        }

        if (!items.Any(item => SelectOptionalToken(item.GetRawText(), path, label) != null))
        {
            throw new MappingException(label, $"JSONPath '{path}' did not match a value");
        }
    }

    private static void RequireObjectItems(
        IReadOnlyList<JsonElement> items,
        string path,
        string label)
    {
        if (items.Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw new MappingException(label, $"JSONPath '{path}' did not match only objects");
        }
    }

    private IReadOnlyList<JsonElement> SelectRequiredTokens(
        string json,
        string path,
        string label)
    {
        try
        {
            var tokens = _jsonPathService.SelectTokens(json, path).ToList();
            if (tokens.Count == 0)
            {
                throw new MappingException(label, $"JSONPath '{path}' did not match any values");
            }

            return tokens;
        }
        catch (MappingException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new MappingException(label, $"JSONPath '{path}' could not be evaluated", ex);
        }
    }

    private JsonElement SelectRequiredToken(string json, string path, string label)
    {
        try
        {
            var tokens = _jsonPathService.SelectTokens(json, path).Take(2).ToList();
            return tokens.Count switch
            {
                1 => tokens[0],
                0 => throw new MappingException(
                    label,
                    $"JSONPath '{path}' did not match a value"),
                _ => throw new MappingException(
                    label,
                    $"JSONPath '{path}' matched more than one value")
            };
        }
        catch (MappingException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new MappingException(label, $"JSONPath '{path}' could not be evaluated", ex);
        }
    }

    private string ExtractRequiredStringValue(string json, string path, string label)
    {
        var value = SelectRequiredToken(json, path, label);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new MappingException(label, $"JSONPath '{path}' did not match a string");
        }

        return value.GetString()!;
    }

    private string ExtractRequiredContentValue(string json, string path, string label)
    {
        return FormatContentValue(SelectRequiredToken(json, path, label));
    }

    private string ExtractRequiredArgumentsValue(string json, string path, string label)
    {
        var value = SelectRequiredToken(json, path, label);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : value.GetRawText();
    }

    private string? ExtractOptionalStringValue(string json, string? path, string label)
    {
        if (path == null)
        {
            return null;
        }

        var value = SelectOptionalToken(json, path, label);
        if (value == null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.String)
        {
            throw new MappingException(label, $"JSONPath '{path}' did not match a string");
        }

        return value.Value.GetString();
    }

    private string? ExtractOptionalIdValue(string json, string? path, string label)
    {
        if (path == null)
        {
            return null;
        }

        var value = SelectOptionalToken(json, path, label);
        if (value == null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => throw new MappingException(
                label,
                $"JSONPath '{path}' did not match a string or number")
        };
    }

    private Dictionary<string, object>? ExtractOptionalMetadataValue(
        string json,
        string? path,
        string label)
    {
        if (path == null)
        {
            return null;
        }

        var value = SelectOptionalToken(json, path, label);
        if (value == null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Object)
        {
            throw new MappingException(label, $"JSONPath '{path}' did not match an object");
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(value.Value.GetRawText());
    }

    private JsonElement? SelectOptionalToken(string json, string path, string label)
    {
        try
        {
            var tokens = _jsonPathService.SelectTokens(json, path).Take(2).ToList();
            return tokens.Count switch
            {
                0 => null,
                1 => tokens[0],
                _ => throw new MappingException(
                    label,
                    $"JSONPath '{path}' matched more than one value")
            };
        }
        catch (MappingException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new MappingException(label, $"JSONPath '{path}' could not be evaluated", ex);
        }
    }

    private static string FormatContentValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return value.GetRawText();
        }

        return string.Join(
            "\n",
            value.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : item.GetRawText()));
    }

    private int? ExtractConfiguredIntValue(string json, string? path, string label)
    {
        if (path == null)
        {
            return null;
        }

        var token = SelectRequiredToken(json, path, label);
        if (token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out var value))
        {
            return value;
        }

        throw new MappingException(label, $"JSONPath '{path}' did not match a 32-bit integer");
    }

    private long? ExtractConfiguredLongValue(string json, string? path, string label)
    {
        if (path == null)
        {
            return null;
        }

        var token = SelectRequiredToken(json, path, label);
        if (token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out var value))
        {
            return value;
        }

        throw new MappingException(label, $"JSONPath '{path}' did not match a 64-bit integer");
    }

    private decimal? ExtractConfiguredDecimalValue(string json, string? path, string label)
    {
        if (path == null)
        {
            return null;
        }

        var token = SelectRequiredToken(json, path, label);
        if (token.ValueKind == JsonValueKind.Number && token.TryGetDecimal(out var value))
        {
            return value;
        }

        throw new MappingException(label, $"JSONPath '{path}' did not match a decimal number");
    }

    private string? CapRawResponse(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(responseJson) > MaxRawResponseSize)
        {
            const string suffix = "\n... (truncated)";
            var payloadLimit = MaxRawResponseSize - Encoding.UTF8.GetByteCount(suffix);
            var retainedBytes = 0;
            var builder = new StringBuilder();
            foreach (var rune in responseJson.EnumerateRunes())
            {
                if (retainedBytes + rune.Utf8SequenceLength > payloadLimit)
                {
                    break;
                }

                builder.Append(rune);
                retainedBytes += rune.Utf8SequenceLength;
            }

            return builder.Append(suffix).ToString();
        }

        return responseJson;
    }

    private sealed class MappingException(
        string path,
        string detail,
        Exception? innerException = null)
        : Exception($"Mapping failed at '{path}': {detail}", innerException)
    {
        public string Path { get; } = path;
    }

    // MappingSpec CRUD operations
    public async Task<MappingSpec?> SaveMappingSpecAsync(
        Guid endpointId,
        string name,
        string specJson,
        TenantAccessScope scope)
    {
        if (!await IsOwnedEndpointAsync(endpointId, scope))
        {
            return null;
        }

        DeserializeAndValidateMappingSpec(specJson);
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

    public async Task<List<MappingSpec>?> GetMappingSpecsByEndpointAsync(
        Guid endpointId,
        TenantAccessScope scope)
    {
        if (!await IsOwnedEndpointAsync(endpointId, scope))
        {
            return null;
        }

        return await _dbContext.MappingSpecs
            .ForTenant(scope)
            .Where(m => m.EndpointId == endpointId)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<MappingSpec?> GetMappingSpecByIdAsync(
        Guid id,
        TenantAccessScope scope)
    {
        return await _dbContext.MappingSpecs
            .ForTenant(scope)
            .FirstOrDefaultAsync(mappingSpec => mappingSpec.Id == id);
    }

    public async Task<MappingSpec?> GetDefaultMappingAsync(
        Guid endpointId,
        TenantAccessScope scope)
    {
        return await _dbContext.MappingSpecs
            .ForTenant(scope)
            .Where(m => m.EndpointId == endpointId && m.IsDefault)
            .FirstOrDefaultAsync();
    }

    public async Task<MappingSpec?> UpdateMappingSpecAsync(
        Guid id,
        string name,
        string specJson,
        TenantAccessScope scope)
    {
        var mappingSpec = await GetMappingSpecByIdAsync(id, scope);
        if (mappingSpec == null)
        {
            return null;
        }

        DeserializeAndValidateMappingSpec(specJson);

        mappingSpec.Name = name;
        mappingSpec.SpecJson = specJson;
        mappingSpec.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return mappingSpec;
    }

    public async Task<bool> SetDefaultMappingAsync(
        Guid id,
        TenantAccessScope scope)
    {
        var mappingSpec = await GetMappingSpecByIdAsync(id, scope);
        if (mappingSpec == null)
        {
            return false;
        }

        // Clear any existing default for this endpoint
        var existingDefaults = await _dbContext.MappingSpecs
            .ForTenant(scope)
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
        return true;
    }

    public async Task<bool> DeleteMappingSpecAsync(
        Guid id,
        TenantAccessScope scope)
    {
        var mappingSpec = await GetMappingSpecByIdAsync(id, scope);
        if (mappingSpec == null)
        {
            return false;
        }

        _dbContext.MappingSpecs.Remove(mappingSpec);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    private async Task<bool> IsOwnedEndpointAsync(
        Guid endpointId,
        TenantAccessScope scope) =>
        await _dbContext.Endpoints
            .ForTenant(scope)
            .AnyAsync(endpoint => endpoint.Id == endpointId);
}

public sealed class MappingSpecValidationException : Exception
{
    public MappingSpecValidationException(
        string path,
        string detail,
        Exception? innerException = null)
        : base($"Invalid mapping spec at '{path}': {detail}", innerException)
    {
        Path = path;
    }

    public string Path { get; }
}
