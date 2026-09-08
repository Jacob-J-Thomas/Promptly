using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Promptly.Infrastructure.Services;

public class YamlService : IYamlService
{
    public const int MaxYamlBytes = 1_048_576;
    public const int MaxYamlNodes = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<YamlService> _logger;
    private readonly ITestSpecificationValidator _specificationValidator;

    public YamlService(
        ILogger<YamlService> logger,
        ITestSpecificationValidator? specificationValidator = null)
    {
        _logger = logger;
        var expectationValidator = new ExpectationDslValidator();
        _specificationValidator = specificationValidator ?? new TestSpecificationValidator(expectationValidator);
    }

    public List<TestCase> DeserializeTests(string yamlContent, Guid suiteId)
    {
        if (yamlContent is null || string.IsNullOrWhiteSpace(yamlContent))
        {
            throw Validation("invalid_yaml", "$", "YAML content is required");
        }

        if (Encoding.UTF8.GetByteCount(yamlContent) > MaxYamlBytes)
        {
            throw Validation("too_large", "$", "YAML content exceeds the maximum size");
        }

        try
        {
            var preflightIssues = PreflightYamlSyntax(yamlContent);
            if (preflightIssues.Count > 0)
            {
                throw new TestSpecificationValidationException(preflightIssues);
            }

            var stream = new YamlStream();
            using var reader = new StringReader(yamlContent);
            stream.Load(reader);
            if (stream.Documents.Count != 1)
            {
                throw Validation("invalid_yaml", "$", "Exactly one YAML document is required");
            }

            var root = stream.Documents[0].RootNode;
            var issues = new List<ExpectationValidationIssue>();
            ValidateYamlLimits(root, "$", 0, issues, new HashSet<YamlNode>());
            ValidateOptionalWrapperVersion(root, issues);
            if (root is not YamlSequenceNode rows)
            {
                issues.Add(new("invalid_shape", "$", "YAML root must be a test-row sequence"));
                throw new TestSpecificationValidationException(issues);
            }

            if (rows.Children.Count > TestSpecificationValidator.MaxMessageCount)
            {
                issues.Add(new(
                    "too_large",
                    "$",
                    $"YAML imports cannot exceed {TestSpecificationValidator.MaxMessageCount} rows"));
            }

            var testCases = new List<TestCase>(rows.Children.Count);
            for (var index = 0; index < rows.Children.Count; index++)
            {
                var rowPath = $"rows[{index}]";
                var testCase = ParseRow(rows.Children[index], rowPath, suiteId, issues);
                if (testCase is not null)
                {
                    testCases.Add(testCase);
                }
            }

            if (issues.Count > 0)
            {
                throw new TestSpecificationValidationException(issues);
            }

            _logger.LogInformation("Deserialized {Count} test cases from YAML", testCases.Count);
            return testCases;
        }
        catch (TestSpecificationValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException)
        {
            _logger.LogError(ex, "Failed to deserialize YAML tests");
            throw Validation("invalid_yaml", "$", "YAML document is invalid");
        }
    }

    public string SerializeTests(List<TestCase> testCases)
    {
        ArgumentNullException.ThrowIfNull(testCases);
        var root = new YamlSequenceNode();
        var issues = new List<ExpectationValidationIssue>();

        for (var index = 0; index < testCases.Count; index++)
        {
            var testCase = testCases[index];
            var rowPath = $"rows[{index}]";
            var validation = _specificationValidator.Validate(
                testCase.InputSpecJson,
                testCase.ExpectationsJson);
            AddRowIssues(issues, rowPath, validation.Issues);
            if (!validation.IsValid)
            {
                continue;
            }

            try
            {
                using var input = JsonDocument.Parse(testCase.InputSpecJson);
                using var expectations = JsonDocument.Parse(testCase.ExpectationsJson);
                var row = new YamlMappingNode();
                row.Add(new YamlScalarNode("id"), StringNode(testCase.ExternalId));
                row.Add(new YamlScalarNode("name"), StringNode(testCase.Name));
                row.Add(new YamlScalarNode("description"), testCase.Description is null
                    ? NullNode()
                    : StringNode(testCase.Description));
                row.Add(new YamlScalarNode("input"), JsonToYaml(input.RootElement));
                row.Add(new YamlScalarNode("expectations"), JsonToYaml(expectations.RootElement));
                root.Add(row);
            }
            catch (JsonException)
            {
                issues.Add(new("invalid_json", $"{rowPath}.input", "Persisted test JSON is invalid"));
            }
        }

        if (issues.Count > 0)
        {
            throw new TestSpecificationValidationException(issues);
        }

        var yaml = new StringBuilder();
        using (var writer = new StringWriter(yaml, CultureInfo.InvariantCulture))
        {
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        }

        _logger.LogInformation("Serialized {Count} test cases to YAML", testCases.Count);
        return yaml.ToString();
    }

    private TestCase? ParseRow(
        YamlNode node,
        string rowPath,
        Guid suiteId,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (node is not YamlMappingNode row)
        {
            issues.Add(new("invalid_type", rowPath, "Each YAML row must be an object"));
            return null;
        }

        var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var child in row.Children)
        {
            if (child.Key is not YamlScalarNode key
                || string.IsNullOrWhiteSpace(key.Value)
                || IsTypedPlainScalar(key))
            {
                issues.Add(new("invalid_type", rowPath, "YAML row keys must be strings"));
                continue;
            }

            var keyPath = $"{rowPath}.{key.Value}";
            if (!values.TryAdd(key.Value, child.Value))
            {
                continue;
            }

            if (key.Value is not ("id" or "name" or "description" or "input" or "expectations"))
            {
                issues.Add(new("unsupported_value", keyPath, "YAML row property is unsupported"));
            }
        }

        var valid = true;
        var externalId = ReadRequiredString(values, "id", $"{rowPath}.id", issues, ref valid);
        var name = ReadRequiredString(values, "name", $"{rowPath}.name", issues, ref valid);
        if (!values.TryGetValue("description", out var descriptionNode))
        {
            descriptionNode = NullNode();
        }
        else if (descriptionNode is not YamlScalarNode descriptionScalar)
        {
            issues.Add(new("invalid_type", $"{rowPath}.description", "Description must be a string or null"));
            valid = false;
        }
        else if (descriptionScalar.Value is not null
            && !IsYamlNull(descriptionScalar)
            && (IsTypedPlainScalar(descriptionScalar)
                || descriptionScalar.Value.Length > TestSpecificationValidator.MaxScalarLength))
        {
            issues.Add(new(
                IsTypedPlainScalar(descriptionScalar) ? "invalid_type" : "too_large",
                $"{rowPath}.description",
                IsTypedPlainScalar(descriptionScalar)
                    ? "Description must be a string or null"
                    : "Description is too long"));
            valid = false;
        }

        if (!values.TryGetValue("input", out var inputNode))
        {
            issues.Add(new("required", $"{rowPath}.input", "Input is required"));
            valid = false;
        }
        else if (inputNode is not YamlMappingNode)
        {
            issues.Add(new("invalid_type", $"{rowPath}.input", "Input must be an object"));
            valid = false;
        }

        if (!values.TryGetValue("expectations", out var expectationsNode))
        {
            issues.Add(new("required", $"{rowPath}.expectations", "Expectations are required"));
            valid = false;
        }
        else if (expectationsNode is not YamlSequenceNode)
        {
            issues.Add(new("invalid_type", $"{rowPath}.expectations", "Expectations must be an array"));
            valid = false;
        }

        if (inputNode is not null && expectationsNode is not null)
        {
            try
            {
                var inputJson = ToJsonNode(inputNode).ToJsonString(JsonOptions);
                var expectationsJson = ToJsonNode(expectationsNode).ToJsonString(JsonOptions);
                AddRowIssues(
                    issues,
                    rowPath,
                    _specificationValidator.Validate(inputJson, expectationsJson).Issues);
            }
            catch (InvalidOperationException)
            {
                issues.Add(new("invalid_type", rowPath, "YAML values cannot be represented as JSON"));
                valid = false;
            }
        }

        if (!valid)
        {
            return null;
        }

        try
        {
            var inputJson = ToJsonNode(inputNode!).ToJsonString(JsonOptions);
            var expectationsJson = ToJsonNode(expectationsNode!).ToJsonString(JsonOptions);
            return new TestCase
            {
                SuiteId = suiteId,
                ExternalId = externalId!,
                Name = name!,
                Description = descriptionNode is YamlScalarNode description && !IsYamlNull(description)
                    ? description.Value
                    : null,
                InputSpecJson = inputJson,
                ExpectationsJson = expectationsJson
            };
        }
        catch (InvalidOperationException)
        {
            issues.Add(new("invalid_type", rowPath, "YAML values cannot be represented as JSON"));
            return null;
        }
    }

    private static string? ReadRequiredString(
        IReadOnlyDictionary<string, YamlNode> values,
        string name,
        string path,
        ICollection<ExpectationValidationIssue> issues,
        ref bool valid)
    {
        if (!values.TryGetValue(name, out var node))
        {
            issues.Add(new("required", path, $"{name} is required"));
            valid = false;
            return null;
        }

        if (node is not YamlScalarNode scalar
            || IsYamlNull(scalar)
            || string.IsNullOrWhiteSpace(scalar.Value)
            || IsTypedPlainScalar(scalar))
        {
            issues.Add(new("invalid_type", path, $"{name} must be a non-empty string"));
            valid = false;
            return null;
        }

        if (scalar.Value!.Length > TestSpecificationValidator.MaxScalarLength)
        {
            issues.Add(new("too_large", path, $"{name} is too long"));
            valid = false;
        }

        return scalar.Value;
    }

    private static bool IsTypedPlainScalar(YamlScalarNode scalar) =>
        scalar.Style == ScalarStyle.Plain
        && (bool.TryParse(scalar.Value, out _)
            || long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || decimal.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static void ValidateOptionalWrapperVersion(
        YamlNode root,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (root is not YamlMappingNode mapping)
        {
            return;
        }

        foreach (var child in mapping.Children)
        {
            if (child.Key is not YamlScalarNode key || key.Value != "version")
            {
                continue;
            }

            if (child.Value is not YamlScalarNode version
                || version.Style != ScalarStyle.Plain
                || version.Value != "1")
            {
                issues.Add(new(
                    "unsupported_value",
                    "$.version",
                    "YAML schema version is unsupported"));
            }

            break;
        }
    }

    private static List<ExpectationValidationIssue> PreflightYamlSyntax(string yaml)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var eventCount = 0;
            while (parser.MoveNext())
            {
                if (++eventCount > MaxYamlNodes)
                {
                    return
                    [
                        new ExpectationValidationIssue(
                            "too_large",
                            "$",
                            "YAML document exceeds the maximum node count")
                    ];
                }
                if (parser.Current is AnchorAlias
                    || parser.Current is NodeEvent nodeEvent && !nodeEvent.Anchor.IsEmpty
                    || parser.Current is NodeEvent taggedNode && !taggedNode.Tag.IsEmpty)
                {
                    return
                    [
                        new ExpectationValidationIssue(
                            "unsupported_value",
                            "$",
                            "YAML anchors, aliases, and tags are not supported")
                    ];
                }
            }
        }
        catch (YamlException)
        {
            // The representation-model load below produces the stable invalid_yaml issue.
        }

        return [];
    }

    private static void ValidateYamlLimits(
        YamlNode node,
        string path,
        int depth,
        ICollection<ExpectationValidationIssue> issues,
        ISet<YamlNode> activeNodes)
    {
        if (depth > TestSpecificationValidator.MaxDepth)
        {
            issues.Add(new("too_large", path, "YAML nesting exceeds the maximum depth"));
            return;
        }

        if (!activeNodes.Add(node))
        {
            issues.Add(new("unsupported_value", path, "YAML aliases and recursive values are not supported"));
            return;
        }

        try
        {
            switch (node)
            {
                case YamlScalarNode scalar:
                    if (scalar.Value?.Length > TestSpecificationValidator.MaxScalarLength)
                    {
                        issues.Add(new("too_large", path, "YAML scalar exceeds the maximum length"));
                    }

                    break;
                case YamlMappingNode mapping:
                    {
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var child in mapping.Children)
                        {
                            var key = child.Key is YamlScalarNode scalarKey ? scalarKey.Value : null;
                            var childPath = key is null ? path : $"{path}.{key}";
                            if (key is null || !names.Add(key))
                            {
                                issues.Add(new("duplicate_property", childPath, "Duplicate YAML property is not allowed"));
                            }

                            ValidateYamlLimits(
                                child.Value,
                                childPath,
                                depth + 1,
                                issues,
                                activeNodes);
                        }

                        break;
                    }
                case YamlSequenceNode sequence:
                    for (var index = 0; index < sequence.Children.Count; index++)
                    {
                        ValidateYamlLimits(
                            sequence.Children[index],
                            $"{path}[{index}]",
                            depth + 1,
                            issues,
                            activeNodes);
                    }

                    break;
                default:
                    issues.Add(new("invalid_type", path, "YAML node type is unsupported"));
                    break;
            }
        }
        finally
        {
            activeNodes.Remove(node);
        }
    }

    private static JsonNode ToJsonNode(YamlNode node) => ToJsonNode(node, new HashSet<YamlNode>());

    private static JsonNode ToJsonNode(YamlNode node, ISet<YamlNode> activeNodes)
    {
        if (!activeNodes.Add(node))
        {
            throw new InvalidOperationException("YAML aliases and recursive values are not supported");
        }

        try
        {
            return node switch
            {
                YamlMappingNode mapping => ToJsonObject(mapping, activeNodes),
                YamlSequenceNode sequence => ToJsonArray(sequence, activeNodes),
                YamlScalarNode scalar when IsYamlNull(scalar) => null!,
                YamlScalarNode scalar => ParseScalarNode(scalar),
                _ => throw new InvalidOperationException("Unsupported YAML node")
            };
        }
        finally
        {
            activeNodes.Remove(node);
        }
    }

    private static JsonObject ToJsonObject(YamlMappingNode mapping, ISet<YamlNode> activeNodes)
    {
        var result = new JsonObject();
        foreach (var child in mapping.Children)
        {
            if (child.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                throw new InvalidOperationException("YAML object keys must be strings");
            }

            result[key.Value] = ToJsonNode(child.Value, activeNodes);
        }

        return result;
    }

    private static JsonArray ToJsonArray(YamlSequenceNode sequence, ISet<YamlNode> activeNodes)
    {
        var result = new JsonArray();
        foreach (var child in sequence.Children)
        {
            result.Add(ToJsonNode(child, activeNodes));
        }

        return result;
    }

    private static JsonNode ParseScalarNode(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style == ScalarStyle.Plain)
        {
            if (IsYamlNull(scalar))
            {
                return null!;
            }

            if (bool.TryParse(value, out var boolean))
            {
                return JsonValue.Create(boolean)!;
            }

            if (LooksLikeNumericScalar(value))
            {
                if (!TryParseJsonNumber(value, out var jsonNumber))
                {
                    throw new InvalidOperationException("YAML numeric scalar is not a supported JSON number");
                }

                return jsonNumber!;
            }
        }

        return JsonValue.Create(value)!;
    }

    private static bool LooksLikeNumericScalar(string value) =>
        value.Length > 0
        && (char.IsDigit(value[0]) || value[0] is '+' or '-' or '.')
        && value.Any(char.IsDigit)
        && value.All(character =>
            char.IsDigit(character) || character is '+' or '-' or '.' or 'e' or 'E');

    private static bool TryParseJsonNumber(string value, out JsonNode? number)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Number)
            {
                number = JsonValue.Create(document.RootElement.Clone());
                return number is not null;
            }
        }
        catch (JsonException)
        {
        }

        number = null;
        return false;
    }

    private static bool IsYamlNull(YamlScalarNode scalar) =>
        scalar.Style == ScalarStyle.Plain
        && scalar.Value is not null
        && scalar.Value is "" or "~" or "null" or "Null" or "NULL";

    private static YamlScalarNode StringNode(string value) =>
        new(value) { Style = ScalarStyle.DoubleQuoted };

    private static YamlScalarNode NullNode() =>
        new("null") { Style = ScalarStyle.Plain };

    private static YamlNode JsonToYaml(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectToYaml(element),
            JsonValueKind.Array => JsonArrayToYaml(element),
            JsonValueKind.String => StringNode(element.GetString() ?? string.Empty),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => new YamlScalarNode(element.GetRawText()),
            JsonValueKind.Null => NullNode(),
            _ => throw new InvalidOperationException("Unsupported JSON value")
        };

    private static YamlMappingNode JsonObjectToYaml(JsonElement element)
    {
        var result = new YamlMappingNode();
        foreach (var property in element.EnumerateObject())
        {
            result.Add(new YamlScalarNode(property.Name), JsonToYaml(property.Value));
        }

        return result;
    }

    private static YamlSequenceNode JsonArrayToYaml(JsonElement element)
    {
        var result = new YamlSequenceNode();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(JsonToYaml(item));
        }

        return result;
    }

    private static void AddRowIssues(
        ICollection<ExpectationValidationIssue> target,
        string rowPath,
        IEnumerable<ExpectationValidationIssue> source)
    {
        foreach (var issue in source)
        {
            var path = issue.Path.StartsWith("inputSpecJson", StringComparison.Ordinal)
                ? $"{rowPath}.input{issue.Path["inputSpecJson".Length..]}"
                : issue.Path.StartsWith("expectationsJson", StringComparison.Ordinal)
                    ? $"{rowPath}.expectations{issue.Path["expectationsJson".Length..]}"
                    : $"{rowPath}.{issue.Path}";
            target.Add(issue with { Path = path });
        }
    }

    private static TestSpecificationValidationException Validation(string code, string path, string message) =>
        new([new ExpectationValidationIssue(code, path, message)]);
}
