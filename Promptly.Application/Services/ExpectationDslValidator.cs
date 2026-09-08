using System.Text.Json;
using Promptly.Application.Interfaces;

namespace Promptly.Application.Services;

/// <summary>
/// Shared validation boundary for the eight v1 expectation types.
/// </summary>
public sealed class ExpectationDslValidator : IExpectationValidator
{
    private static readonly HashSet<string> SupportedTypes =
    [
        "contains_text",
        "banned_text",
        "regex_match",
        "link_pattern",
        "tool_called",
        "tool_sequence",
        "llm_judge",
        "groundedness"
    ];

    private static readonly Dictionary<string, HashSet<string>> AllowedFields = new()
    {
        ["contains_text"] = ["type", "text", "case_insensitive"],
        ["banned_text"] = ["type", "text", "case_insensitive"],
        ["regex_match"] = ["type", "pattern", "case_insensitive"],
        ["link_pattern"] = ["type", "pattern"],
        ["tool_called"] = ["type", "tool_name"],
        ["tool_sequence"] = ["type", "sequence", "exact_sequence"],
        ["llm_judge"] = ["type", "rubric", "min_score", "model", "provider"],
        ["groundedness"] = ["type", "min_score", "model", "provider"]
    };

    private readonly IBoundedRegexMatcher _regexMatcher;

    public ExpectationDslValidator(IBoundedRegexMatcher? regexMatcher = null)
    {
        _regexMatcher = regexMatcher ?? new BoundedRegexMatcher();
    }

    public ExpectationValidationResult ValidateExpectationsJson(string expectationsJson)
    {
        if (string.IsNullOrWhiteSpace(expectationsJson))
        {
            return Invalid("invalid_expectations_json", "$", "Expectations JSON is required");
        }

        try
        {
            using var document = JsonDocument.Parse(expectationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid(
                    "invalid_expectations_json",
                    "$",
                    "Expectations JSON must be an array");
            }

            var issues = new List<ExpectationValidationIssue>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new(
                        "invalid_expectation",
                        $"$[{index}]",
                        "Each expectation must be an object"));
                }
                else
                {
                    var dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        element.GetRawText());
                    if (dictionary is null)
                    {
                        issues.Add(new(
                            "invalid_expectation",
                            $"$[{index}]",
                            "Each expectation must be an object"));
                    }
                    else
                    {
                        issues.AddRange(ValidateExpectation(dictionary).Issues.Select(issue =>
                            issue with { Path = $"$[{index}].{issue.Path.TrimStart('.')}" }));
                    }
                }

                index++;
            }

            if (index == 0)
            {
                issues.Add(new(
                    "no_expectations",
                    "$",
                    "At least one expectation is required"));
            }

            return new ExpectationValidationResult(issues);
        }
        catch (JsonException)
        {
            return Invalid(
                "invalid_expectations_json",
                "$",
                "Expectations JSON is invalid");
        }
    }

    public ExpectationValidationResult ValidateExpectation(
        IReadOnlyDictionary<string, object> expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        var issues = new List<ExpectationValidationIssue>();

        if (!expectation.TryGetValue("type", out var typeValue)
            || !TryGetString(typeValue, out var type)
            || string.IsNullOrWhiteSpace(type))
        {
            return Invalid("missing_expectation_type", "type", "Expectation type is required");
        }

        if (!SupportedTypes.Contains(type))
        {
            return Invalid(
                "unsupported_expectation_type",
                "type",
                $"Unsupported expectation type '{type}'");
        }

        foreach (var field in expectation.Keys)
        {
            if (!AllowedFields[type].Contains(field))
            {
                issues.Add(new(
                    "unknown_expectation_field",
                    field,
                    $"Field '{field}' is not supported for expectation type '{type}'"));
            }
        }

        switch (type)
        {
            case "contains_text":
            case "banned_text":
                RequireNonEmptyString(expectation, "text", issues);
                RequireOptionalBoolean(expectation, "case_insensitive", issues);
                break;
            case "regex_match":
                ValidatePattern(expectation, "pattern", issues);
                RequireOptionalBoolean(expectation, "case_insensitive", issues);
                break;
            case "link_pattern":
                ValidatePattern(expectation, "pattern", issues);
                break;
            case "tool_called":
                RequireNonEmptyString(expectation, "tool_name", issues);
                break;
            case "tool_sequence":
                ValidateSequence(expectation, issues);
                RequireOptionalBoolean(expectation, "exact_sequence", issues);
                break;
            case "llm_judge":
                RequireNonEmptyString(expectation, "rubric", issues);
                ValidateScore(expectation, issues);
                ValidateOptionalText(expectation, "model", issues);
                ValidateOptionalText(expectation, "provider", issues);
                break;
            case "groundedness":
                ValidateScore(expectation, issues);
                ValidateOptionalText(expectation, "model", issues);
                ValidateOptionalText(expectation, "provider", issues);
                break;
        }

        return new ExpectationValidationResult(issues);
    }

    private void ValidatePattern(
        IReadOnlyDictionary<string, object> expectation,
        string field,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (!TryGetString(expectation.GetValueOrDefault(field), out var pattern)
            || string.IsNullOrWhiteSpace(pattern))
        {
            issues.Add(new(
                "required_expectation_field",
                field,
                $"Expectation field '{field}' is required and must be non-empty"));
            return;
        }

        var result = _regexMatcher.FindMatches(pattern, string.Empty);
        if (!result.Succeeded)
        {
            issues.Add(new(
                RegexErrorCode(result.Status),
                field,
                result.ErrorMessage ?? (result.Status is
                    BoundedRegexStatus.InvalidPattern or
                    BoundedRegexStatus.UnsupportedPattern or
                    BoundedRegexStatus.PatternTooLong or
                    BoundedRegexStatus.InputTooLong
                    ? "Regex pattern is invalid"
                    : "Regex evaluation failed")));
        }
    }

    private static void ValidateSequence(
        IReadOnlyDictionary<string, object> expectation,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (!expectation.TryGetValue("sequence", out var value))
        {
            issues.Add(new("required_expectation_field", "sequence", "Tool sequence is required"));
            return;
        }

        if (!TryGetArray(value, out var sequence)
            || sequence.Count == 0
            || sequence.Any(item => string.IsNullOrWhiteSpace(item)))
        {
            issues.Add(new(
                "invalid_tool_sequence",
                "sequence",
                "Tool sequence must contain at least one non-empty tool name"));
        }
    }

    private static void ValidateScore(
        IReadOnlyDictionary<string, object> expectation,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (!expectation.TryGetValue("min_score", out var value))
        {
            return;
        }

        if (!TryGetDouble(value, out var score) || score is < 0 or > 1)
        {
            issues.Add(new(
                "invalid_score",
                "min_score",
                "min_score must be a number between 0 and 1"));
        }
    }

    private static void RequireNonEmptyString(
        IReadOnlyDictionary<string, object> expectation,
        string field,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (!expectation.TryGetValue(field, out var value)
            || !TryGetString(value, out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            issues.Add(new(
                "required_expectation_field",
                field,
                $"Expectation field '{field}' is required and must be non-empty"));
        }
    }

    private static void ValidateOptionalText(
        IReadOnlyDictionary<string, object> expectation,
        string field,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (expectation.TryGetValue(field, out var value)
            && value is not null
            && (!TryGetString(value, out var text) || string.IsNullOrWhiteSpace(text)))
        {
            issues.Add(new(
                "invalid_expectation_field",
                field,
                $"Expectation field '{field}' must be non-empty text when provided"));
        }
    }

    private static void RequireOptionalBoolean(
        IReadOnlyDictionary<string, object> expectation,
        string field,
        ICollection<ExpectationValidationIssue> issues)
    {
        if (expectation.TryGetValue(field, out var value)
            && !TryGetBoolean(value, out _))
        {
            issues.Add(new(
                "invalid_expectation_field",
                field,
                $"Expectation field '{field}' must be a boolean when provided"));
        }
    }

    private static bool TryGetString(object? value, out string? result)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.String } element)
        {
            result = element.GetString();
            return result is not null;
        }

        if (value is string text)
        {
            result = text;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryGetBoolean(object? value, out bool result)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } element)
        {
            result = element.GetBoolean();
            return true;
        }

        if (value is bool boolean)
        {
            result = boolean;
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        if (value is JsonElement element
            && element.ValueKind is JsonValueKind.Number
            && element.TryGetDouble(out result))
        {
            return true;
        }

        if (value is double doubleValue)
        {
            result = doubleValue;
            return true;
        }

        if (value is float floatValue)
        {
            result = floatValue;
            return true;
        }

        if (value is decimal decimalValue)
        {
            result = (double)decimalValue;
            return true;
        }

        if (value is int intValue)
        {
            result = intValue;
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetArray(object? value, out List<string> result)
    {
        result = [];
        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                result.Add(item.GetString() ?? string.Empty);
            }

            return true;
        }

        if (value is IEnumerable<string> values)
        {
            result.AddRange(values);
            return true;
        }

        return false;
    }

    private static string RegexErrorCode(BoundedRegexStatus status) => status switch
    {
        BoundedRegexStatus.InvalidPattern => "invalid_regex_pattern",
        BoundedRegexStatus.UnsupportedPattern => "unsupported_regex_construct",
        BoundedRegexStatus.PatternTooLong => "regex_pattern_too_long",
        BoundedRegexStatus.InputTooLong => "regex_input_too_long",
        BoundedRegexStatus.TimedOut => "regex_timeout",
        _ => "regex_evaluation_error"
    };

    private static ExpectationValidationResult Invalid(string code, string path, string message) =>
        new([new ExpectationValidationIssue(code, path, message)]);
}
