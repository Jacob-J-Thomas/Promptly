using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;
using ExpectationResult = Promptly.Domain.ValueObjects.ExpectationResult;

namespace Promptly.Application.Services;

public class ExpectationEvaluator : IExpectationEvaluator
{
    private readonly ILogger<ExpectationEvaluator> _logger;

    public ExpectationEvaluator(ILogger<ExpectationEvaluator> logger)
    {
        _logger = logger;
    }

    public async Task<ExpectationResult> EvaluateAsync(object expectation, CanonicalTrace trace)
    {
        return await Task.Run(() => Evaluate(expectation, trace));
    }

    private ExpectationResult Evaluate(object expectation, CanonicalTrace trace)
    {
        try
        {
            // Parse expectation as dictionary
            var expDict = expectation is JsonElement jsonElement
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText())
                : expectation as Dictionary<string, object>;

            if (expDict == null || !expDict.ContainsKey("type"))
            {
                return new ExpectationResult
                {
                    ExpectationType = "unknown",
                    Passed = false,
                    Score = 0.0,
                    Reason = "Invalid expectation: missing 'type' field"
                };
            }

            var type = expDict["type"].ToString();

            return type switch
            {
                "contains_text" => EvaluateContainsText(expDict, trace),
                "banned_text" => EvaluateBannedText(expDict, trace),
                "regex_match" => EvaluateRegexMatch(expDict, trace),
                "link_pattern" => EvaluateLinkPattern(expDict, trace),
                "tool_called" => EvaluateToolCalled(expDict, trace),
                "tool_sequence" => EvaluateToolSequence(expDict, trace),
                _ => new ExpectationResult
                {
                    ExpectationType = type ?? "unknown",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"Unknown expectation type: {type}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate expectation");
            return new ExpectationResult
            {
                ExpectationType = "error",
                Passed = false,
                Score = 0.0,
                Reason = $"Evaluation error: {ex.Message}"
            };
        }
    }

    private ExpectationResult EvaluateContainsText(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var text = GetStringValue(exp, "text") ?? "";
        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? false;

        var messages = trace.Messages.Where(m => m.Role == "assistant").ToList();
        var combined = string.Join("\n", messages.Select(m => m.Content));

        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var found = combined.Contains(text, comparison);

        return new ExpectationResult
        {
            ExpectationType = "contains_text",
            Passed = found,
            Score = found ? 1.0 : 0.0,
            Reason = found
                ? $"Text '{text}' found in assistant messages"
                : $"Text '{text}' not found in assistant messages"
        };
    }

    private ExpectationResult EvaluateBannedText(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var text = GetStringValue(exp, "text") ?? "";
        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? false;

        var messages = trace.Messages.Where(m => m.Role == "assistant").ToList();
        var combined = string.Join("\n", messages.Select(m => m.Content));

        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var found = combined.Contains(text, comparison);

        return new ExpectationResult
        {
            ExpectationType = "banned_text",
            Passed = !found,
            Score = !found ? 1.0 : 0.0,
            Reason = !found
                ? $"Banned text '{text}' correctly absent from assistant messages"
                : $"Banned text '{text}' found in assistant messages (violation)"
        };
    }

    private ExpectationResult EvaluateRegexMatch(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var pattern = GetStringValue(exp, "pattern") ?? "";
        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? false;

        var messages = trace.Messages.Where(m => m.Role == "assistant").ToList();
        var combined = string.Join("\n", messages.Select(m => m.Content));

        try
        {
            var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
            var regex = new Regex(pattern, options);
            var match = regex.Match(combined);

            return new ExpectationResult
            {
                ExpectationType = "regex_match",
                Passed = match.Success,
                Score = match.Success ? 1.0 : 0.0,
                Reason = match.Success
                    ? $"Regex pattern '{pattern}' matched: '{match.Value}'"
                    : $"Regex pattern '{pattern}' did not match",
                Metrics = match.Success ? new Dictionary<string, object>
                {
                    { "matched_text", match.Value },
                    { "match_index", match.Index }
                } : null
            };
        }
        catch (Exception ex)
        {
            return new ExpectationResult
            {
                ExpectationType = "regex_match",
                Passed = false,
                Score = 0.0,
                Reason = $"Invalid regex pattern: {ex.Message}"
            };
        }
    }

    private ExpectationResult EvaluateLinkPattern(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var pattern = GetStringValue(exp, "pattern") ?? "";

        var messages = trace.Messages.Where(m => m.Role == "assistant").ToList();
        var combined = string.Join("\n", messages.Select(m => m.Content));

        // Extract URLs using regex
        var urlRegex = new Regex(@"https?://[^\s]+", RegexOptions.IgnoreCase);
        var urls = urlRegex.Matches(combined).Select(m => m.Value).ToList();

        if (urls.Count == 0)
        {
            return new ExpectationResult
            {
                ExpectationType = "link_pattern",
                Passed = false,
                Score = 0.0,
                Reason = "No URLs found in assistant messages"
            };
        }

        // Check if any URL matches the pattern
        try
        {
            var patternRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            var matchedUrls = urls.Where(url => patternRegex.IsMatch(url)).ToList();

            if (matchedUrls.Count > 0)
            {
                return new ExpectationResult
                {
                    ExpectationType = "link_pattern",
                    Passed = true,
                    Score = 1.0,
                    Reason = $"Found {matchedUrls.Count} URL(s) matching pattern '{pattern}'",
                    Metrics = new Dictionary<string, object>
                    {
                        { "matched_urls", matchedUrls },
                        { "total_urls", urls.Count }
                    }
                };
            }
            else
            {
                return new ExpectationResult
                {
                    ExpectationType = "link_pattern",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"Found {urls.Count} URL(s) but none matched pattern '{pattern}'",
                    Metrics = new Dictionary<string, object>
                    {
                        { "found_urls", urls }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            return new ExpectationResult
            {
                ExpectationType = "link_pattern",
                Passed = false,
                Score = 0.0,
                Reason = $"Invalid pattern: {ex.Message}"
            };
        }
    }

    private ExpectationResult EvaluateToolCalled(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var toolName = GetStringValue(exp, "tool_name") ?? "";

        var toolCalls = trace.ToolCalls;
        var found = toolCalls.Any(tc => tc.Name == toolName);

        return new ExpectationResult
        {
            ExpectationType = "tool_called",
            Passed = found,
            Score = found ? 1.0 : 0.0,
            Reason = found
                ? $"Tool '{toolName}' was called"
                : $"Tool '{toolName}' was not called",
            Metrics = new Dictionary<string, object>
            {
                { "called_tools", toolCalls.Select(tc => tc.Name).ToList() }
            }
        };
    }

    private ExpectationResult EvaluateToolSequence(Dictionary<string, object> exp, CanonicalTrace trace)
    {
        var expectedSequence = GetListValue(exp, "sequence") ?? new List<string>();

        var toolCalls = trace.ToolCalls;
        var actualSequence = toolCalls.Select(tc => tc.Name).ToList();

        // Check if actual sequence matches expected sequence
        bool matches = true;
        if (actualSequence.Count < expectedSequence.Count)
        {
            matches = false;
        }
        else
        {
            for (int i = 0; i < expectedSequence.Count; i++)
            {
                if (actualSequence[i] != expectedSequence[i])
                {
                    matches = false;
                    break;
                }
            }
        }

        return new ExpectationResult
        {
            ExpectationType = "tool_sequence",
            Passed = matches,
            Score = matches ? 1.0 : 0.0,
            Reason = matches
                ? $"Tool sequence matches expected order: [{string.Join(", ", expectedSequence)}]"
                : $"Tool sequence mismatch. Expected: [{string.Join(", ", expectedSequence)}], Actual: [{string.Join(", ", actualSequence)}]",
            Metrics = new Dictionary<string, object>
            {
                { "expected_sequence", expectedSequence },
                { "actual_sequence", actualSequence }
            }
        };
    }

    // Helper methods to extract values from expectation dictionary
    private string? GetStringValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.ContainsKey(key))
            return null;

        var value = dict[key];
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
            return jsonElement.GetString();

        return value?.ToString();
    }

    private bool? GetBoolValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.ContainsKey(key))
            return null;

        var value = dict[key];
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.True)
                return true;
            if (jsonElement.ValueKind == JsonValueKind.False)
                return false;
        }

        if (value is bool boolValue)
            return boolValue;

        return null;
    }

    private List<string>? GetListValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.ContainsKey(key))
            return null;

        var value = dict[key];
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .ToList();
        }

        if (value is List<string> list)
            return list;

        return null;
    }
}
