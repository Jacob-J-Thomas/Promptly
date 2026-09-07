using System.Text;
using System.Text.Json;
using Promptly.Application.Interfaces;

namespace Promptly.Application.Services;

/// <summary>
/// Shared bounded structural validator for the persisted test specification.
/// </summary>
public sealed class TestSpecificationValidator : ITestSpecificationValidator
{
    public const int MaxInputJsonBytes = 262_144;
    public const int MaxDepth = 32;
    public const int MaxMessageCount = 100;
    public const int MaxScalarLength = 16_384;

    private readonly IExpectationValidator _expectationValidator;

    public TestSpecificationValidator(IExpectationValidator expectationValidator)
    {
        _expectationValidator = expectationValidator ?? throw new ArgumentNullException(nameof(expectationValidator));
    }

    public ExpectationValidationResult Validate(string inputSpecJson, string expectationsJson)
    {
        var issues = new List<ExpectationValidationIssue>();
        issues.AddRange(ValidateInput(inputSpecJson).Issues);

        if (string.IsNullOrWhiteSpace(expectationsJson))
        {
            issues.Add(new("invalid_json", "expectationsJson", "Expectations JSON is required"));
        }
        else if (Encoding.UTF8.GetByteCount(expectationsJson) > MaxInputJsonBytes)
        {
            issues.Add(new("too_large", "expectationsJson", "Expectations JSON exceeds the maximum size"));
        }
        else
        {
            var expectationResult = _expectationValidator.ValidateExpectationsJson(expectationsJson);
            issues.AddRange(expectationResult.Issues.Select(issue =>
                issue with { Path = PrefixExpectationPath(issue.Path) }));
        }

        return new ExpectationValidationResult(issues);
    }

    public ExpectationValidationResult ValidateInput(string inputSpecJson)
    {
        if (string.IsNullOrWhiteSpace(inputSpecJson))
        {
            return Invalid("invalid_json", "inputSpecJson", "Input JSON is required");
        }

        if (Encoding.UTF8.GetByteCount(inputSpecJson) > MaxInputJsonBytes)
        {
            return Invalid("too_large", "inputSpecJson", "Input JSON exceeds the maximum size");
        }

        try
        {
            using var document = JsonDocument.Parse(
                inputSpecJson,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                    MaxDepth = 64
                });

            var issues = new List<ExpectationValidationIssue>();
            ValidateLimits(document.RootElement, "inputSpecJson", 0, issues);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(
                    "invalid_shape",
                    "inputSpecJson",
                    "Input JSON must be an object"));
                return new ExpectationValidationResult(issues);
            }

            var root = document.RootElement;
            if (root.TryGetProperty("version", out var version)
                && (version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != 1))
            {
                issues.Add(new(
                    "unsupported_value",
                    "inputSpecJson.version",
                    "Input schema version is unsupported"));
            }

            if (!root.TryGetProperty("messages", out var messages))
            {
                issues.Add(new("required", "inputSpecJson.messages", "Input messages are required"));
                return new ExpectationValidationResult(issues);
            }

            if (messages.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new(
                    "invalid_type",
                    "inputSpecJson.messages",
                    "Input messages must be an array"));
                return new ExpectationValidationResult(issues);
            }

            var messageCount = messages.GetArrayLength();
            if (messageCount == 0)
            {
                issues.Add(new(
                    "required",
                    "inputSpecJson.messages",
                    "At least one input message is required"));
            }
            else if (messageCount > MaxMessageCount)
            {
                issues.Add(new(
                    "too_large",
                    "inputSpecJson.messages",
                    $"Input messages cannot exceed {MaxMessageCount} items"));
            }

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                var path = $"inputSpecJson.messages[{index}]";
                if (message.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new("invalid_type", path, "Each input message must be an object"));
                    index++;
                    continue;
                }

                foreach (var property in message.EnumerateObject())
                {
                    if (property.Name is not ("role" or "content"))
                    {
                        issues.Add(new(
                            "unsupported_value",
                            $"{path}.{property.Name}",
                            "Message properties are limited to role and content"));
                    }
                }

                if (!message.TryGetProperty("role", out var role))
                {
                    issues.Add(new("required", $"{path}.role", "Message role is required"));
                }
                else if (role.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new("invalid_type", $"{path}.role", "Message role must be a string"));
                }
                else if (role.GetString() is not ("system" or "user" or "assistant"))
                {
                    issues.Add(new("unsupported_value", $"{path}.role", "Message role is unsupported"));
                }

                if (!message.TryGetProperty("content", out var content))
                {
                    issues.Add(new("required", $"{path}.content", "Message content is required"));
                }
                else if (content.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new("invalid_type", $"{path}.content", "Message content must be a string"));
                }
                else if (string.IsNullOrWhiteSpace(content.GetString()))
                {
                    issues.Add(new("required", $"{path}.content", "Message content must be non-empty"));
                }

                index++;
            }

            return new ExpectationValidationResult(issues);
        }
        catch (JsonException)
        {
            return Invalid("invalid_json", "inputSpecJson", "Input JSON is invalid");
        }
        catch (ArgumentException)
        {
            return Invalid("invalid_json", "inputSpecJson", "Input JSON is invalid");
        }
    }

    private static void ValidateLimits(
        JsonElement element,
        string path,
        int depth,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (depth > MaxDepth)
        {
            issues.Add(new("too_large", path, $"Input JSON nesting cannot exceed {MaxDepth} levels"));
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString()?.Length > MaxScalarLength:
                issues.Add(new("too_large", path, $"User-authored scalar cannot exceed {MaxScalarLength} characters"));
                break;
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = path == "inputSpecJson"
                        ? $"{path}.{property.Name}"
                        : $"{path}.{property.Name}";
                    if (!names.Add(property.Name))
                    {
                        issues.Add(new("duplicate_property", propertyPath, "Duplicate JSON property is not allowed"));
                    }

                    ValidateLimits(property.Value, propertyPath, depth + 1, issues);
                }

                break;
            }
            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ValidateLimits(item, $"{path}[{index}]", depth + 1, issues);
                    index++;
                }

                break;
            }
        }
    }

    private static string PrefixExpectationPath(string path) =>
        path == "$" ? "expectationsJson" : $"expectationsJson{path[1..]}";

    private static ExpectationValidationResult Invalid(string code, string path, string message) =>
        new([new ExpectationValidationIssue(code, path, message)]);
}
