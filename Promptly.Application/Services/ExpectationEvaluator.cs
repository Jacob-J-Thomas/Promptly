using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;
using ExpectationResult = Promptly.Domain.ValueObjects.ExpectationResult;

namespace Promptly.Application.Services;

public class ExpectationEvaluator : IExpectationEvaluator
{
    private const string UrlPattern = @"https?://[^\s]+";

    private readonly ILogger<ExpectationEvaluator> _logger;
    private readonly IBoundedRegexMatcher _regexMatcher;
    private readonly IExpectationValidator _expectationValidator;

    public ExpectationEvaluator(
        ILogger<ExpectationEvaluator> logger,
        IBoundedRegexMatcher regexMatcher,
        IExpectationValidator? expectationValidator = null)
    {
        _logger = logger;
        _regexMatcher = regexMatcher;
        _expectationValidator = expectationValidator ?? new ExpectationDslValidator(regexMatcher);
    }

    public Task<ExpectationResult> EvaluateAsync(
        object expectation,
        CanonicalTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Evaluate(expectation, trace, cancellationToken));
    }

    private ExpectationResult Evaluate(
        object expectation,
        CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    ErrorCode = "missing_expectation_type",
                    Reason = "Invalid expectation: missing 'type' field"
                };
            }

            var type = expDict["type"] is JsonElement { ValueKind: JsonValueKind.String } typeElement
                ? typeElement.GetString()
                : expDict["type"]?.ToString();
            type = string.IsNullOrWhiteSpace(type) ? null : type;
            var validation = _expectationValidator.ValidateExpectation(expDict);
            if (!validation.IsValid)
            {
                var issue = validation.Issues[0];
                return new ExpectationResult
                {
                    ExpectationType = type ?? "unknown",
                    Passed = false,
                    Score = 0.0,
                    ErrorCode = issue.Code,
                    Reason = $"{issue.Path}: {issue.Message}"
                };
            }

            return type switch
            {
                "contains_text" => EvaluateContainsText(expDict, trace),
                "banned_text" => EvaluateBannedText(expDict, trace),
                "regex_match" => EvaluateRegexMatch(expDict, trace, cancellationToken),
                "link_pattern" => EvaluateLinkPattern(expDict, trace, cancellationToken),
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
        catch (OperationCanceledException)
        {
            throw;
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

    private static ExpectationResult EvaluateContainsText(
        Dictionary<string, object> exp,
        CanonicalTrace trace)
    {
        var text = GetStringValue(exp, "text") ?? "";
        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? true;
        var combined = CombineAssistantMessages(trace);
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

    private static ExpectationResult EvaluateBannedText(
        Dictionary<string, object> exp,
        CanonicalTrace trace)
    {
        var text = GetStringValue(exp, "text") ?? "";
        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? true;
        var combined = CombineAssistantMessages(trace);
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

    private ExpectationResult EvaluateRegexMatch(
        Dictionary<string, object> exp,
        CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        var pattern = GetStringValue(exp, "pattern");
        if (pattern == null)
        {
            return MissingRegexPattern("regex_match");
        }

        var caseInsensitive = GetBoolValue(exp, "case_insensitive") ?? true;
        var matchResult = _regexMatcher.FindMatches(
            pattern,
            CombineAssistantMessagesForRegex(trace),
            caseInsensitive,
            cancellationToken);

        if (!matchResult.Succeeded)
        {
            return RegexError("regex_match", matchResult);
        }

        var match = matchResult.Matches.FirstOrDefault();
        return new ExpectationResult
        {
            ExpectationType = "regex_match",
            Passed = match != null,
            Score = match != null ? 1.0 : 0.0,
            Reason = match != null
                ? $"Regex pattern '{pattern}' matched: '{match.Value}'"
                : $"Regex pattern '{pattern}' did not match",
            Metrics = match == null
                ? null
                : new Dictionary<string, object>
                {
                    { "matched_text", match.Value },
                    { "match_index", match.Index }
                }
        };
    }

    private ExpectationResult EvaluateLinkPattern(
        Dictionary<string, object> exp,
        CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        var pattern = GetStringValue(exp, "pattern");
        if (pattern == null)
        {
            return MissingRegexPattern("link_pattern");
        }

        var urlResult = _regexMatcher.FindMatches(
            UrlPattern,
            CombineAssistantMessagesForRegex(trace),
            caseInsensitive: true,
            cancellationToken);

        if (!urlResult.Succeeded)
        {
            return RegexError("link_pattern", urlResult);
        }

        var urls = urlResult.Matches.Select(match => match.Value).ToList();
        var patternResult = _regexMatcher.FindMatchingCandidates(
            pattern,
            urls,
            caseInsensitive: true,
            cancellationToken);

        if (!patternResult.Succeeded)
        {
            return RegexError("link_pattern", patternResult.Status, patternResult.ErrorMessage);
        }

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

        var matchedUrls = patternResult.Matches;
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

    private static ExpectationResult RegexError(
        string expectationType,
        BoundedRegexMatchResult matchResult)
    {
        return RegexError(expectationType, matchResult.Status, matchResult.ErrorMessage);
    }

    private static ExpectationResult RegexError(
        string expectationType,
        BoundedRegexStatus status,
        string? errorMessage)
    {
        var errorCode = status switch
        {
            BoundedRegexStatus.InvalidPattern => "invalid_regex_pattern",
            BoundedRegexStatus.UnsupportedPattern => "unsupported_regex_construct",
            BoundedRegexStatus.PatternTooLong => "regex_pattern_too_long",
            BoundedRegexStatus.InputTooLong => "regex_input_too_long",
            BoundedRegexStatus.TimedOut => "regex_timeout",
            _ => "regex_evaluation_error"
        };

        return new ExpectationResult
        {
            ExpectationType = expectationType,
            Passed = false,
            Score = 0.0,
            ErrorCode = errorCode,
            Reason = errorMessage ?? "Regex evaluation failed"
        };
    }

    private static ExpectationResult MissingRegexPattern(string expectationType)
    {
        return new ExpectationResult
        {
            ExpectationType = expectationType,
            Passed = false,
            Score = 0.0,
            ErrorCode = "invalid_regex_pattern",
            Reason = "Regex pattern is required"
        };
    }

    private static ExpectationResult EvaluateToolCalled(
        Dictionary<string, object> exp,
        CanonicalTrace trace)
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

    private static ExpectationResult EvaluateToolSequence(
        Dictionary<string, object> exp,
        CanonicalTrace trace)
    {
        var expectedSequence = GetListValue(exp, "sequence") ?? [];
        var actualSequence = trace.ToolCalls.Select(tc => tc.Name).ToList();
        if (expectedSequence.Count == 0)
        {
            return new ExpectationResult
            {
                ExpectationType = "tool_sequence",
                Passed = false,
                Score = 0.0,
                ErrorCode = "invalid_tool_sequence",
                Reason = "Tool sequence must contain at least one non-empty tool name"
            };
        }

        var exactSequence = GetBoolValue(exp, "exact_sequence") ?? true;
        var matches = exactSequence
            ? actualSequence.SequenceEqual(expectedSequence, StringComparer.Ordinal)
            : IsOrderedSubsequence(expectedSequence, actualSequence);

        return new ExpectationResult
        {
            ExpectationType = "tool_sequence",
            Passed = matches,
            Score = matches ? 1.0 : 0.0,
            Reason = matches
                ? exactSequence
                    ? $"Tool sequence exactly matches expected order: [{string.Join(", ", expectedSequence)}]"
                    : $"Tool sequence appears in expected order: [{string.Join(", ", expectedSequence)}]"
                : $"Tool sequence mismatch. Expected: [{string.Join(", ", expectedSequence)}], Actual: [{string.Join(", ", actualSequence)}]",
            Metrics = new Dictionary<string, object>
            {
                { "expected_sequence", expectedSequence },
                { "actual_sequence", actualSequence }
            }
        };
    }

    private static bool IsOrderedSubsequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        var expectedIndex = 0;
        foreach (var actualTool in actual)
        {
            if (string.Equals(actualTool, expected[expectedIndex], StringComparison.Ordinal))
            {
                expectedIndex++;
                if (expectedIndex == expected.Count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string CombineAssistantMessages(CanonicalTrace trace)
    {
        return string.Join(
            "\n",
            trace.Messages
                .Where(message => message.Role == "assistant")
                .Select(message => message.Content));
    }

    private static string CombineAssistantMessagesForRegex(CanonicalTrace trace)
    {
        var builder = new StringBuilder(BoundedRegexMatcher.MaxInputLength + 1);
        foreach (var message in trace.Messages.Where(message => message.Role == "assistant"))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var remaining = BoundedRegexMatcher.MaxInputLength + 1 - builder.Length;
            if (remaining <= 0)
            {
                break;
            }

            if (message.Content.Length <= remaining)
            {
                builder.Append(message.Content);
                continue;
            }

            builder.Append(message.Content.AsSpan(0, remaining));
            break;
        }

        return builder.ToString();
    }

    private static string? GetStringValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return value?.ToString();
    }

    private static bool? GetBoolValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : null;
        }

        return value is bool boolValue ? boolValue : null;
    }

    private static List<string>? GetListValue(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            return element.EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToList();
        }

        return value as List<string>;
    }
}
