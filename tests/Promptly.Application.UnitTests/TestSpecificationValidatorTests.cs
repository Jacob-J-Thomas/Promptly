using System.Text;
using Promptly.Application.Interfaces;
using Promptly.Application.Services;

namespace Promptly.Application.UnitTests;

public sealed class TestSpecificationValidatorTests
{
    private static readonly string ValidInput =
        "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";

    [Fact]
    public void Valid_input_retains_unknown_metadata_and_all_eight_expectation_types_are_accepted()
    {
        var input = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}],\"temperature\":0.5,\"enabled\":true,\"metadata\":{\"tags\":[\"demo\",null]}}";
        var expectations = "["
            + "{\"type\":\"contains_text\",\"text\":\"Hello\"},"
            + "{\"type\":\"banned_text\",\"text\":\"bad\"},"
            + "{\"type\":\"regex_match\",\"pattern\":\"Hello\"},"
            + "{\"type\":\"link_pattern\",\"pattern\":\"https?://\"},"
            + "{\"type\":\"tool_called\",\"tool_name\":\"search\"},"
            + "{\"type\":\"tool_sequence\",\"sequence\":[\"search\"]},"
            + "{\"type\":\"llm_judge\",\"rubric\":\"Be helpful\"},"
            + "{\"type\":\"groundedness\"}]";

        var result = Validator().Validate(input, expectations);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
    }

    [Theory]
    [InlineData("", "invalid_json", "inputSpecJson")]
    [InlineData("null", "invalid_shape", "inputSpecJson")]
    [InlineData("[]", "invalid_shape", "inputSpecJson")]
    [InlineData("{}", "required", "inputSpecJson.messages")]
    [InlineData("{\"messages\":[]}", "required", "inputSpecJson.messages")]
    [InlineData("{\"messages\":[{\"role\":\"root\",\"content\":\"x\"}]}", "unsupported_value", "inputSpecJson.messages[0].role")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":1}]}", "invalid_type", "inputSpecJson.messages[0].content")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"x\",\"extra\":true}]}", "unsupported_value", "inputSpecJson.messages[0].extra")]
    public void Invalid_input_reports_stable_issue(string input, string code, string path)
    {
        var result = Validator().ValidateInput(input);

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }

    [Fact]
    public void Duplicate_json_keys_are_rejected_without_exposing_parser_details()
    {
        var result = Validator().ValidateInput(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"enabled\":true,\"enabled\":false}");

        var issue = Assert.Single(result.Issues, issue => issue.Code == "duplicate_property");
        Assert.Equal("inputSpecJson.enabled", issue.Path);
        Assert.DoesNotContain("JsonException", issue.Message);
    }

    [Fact]
    public void Exact_message_limit_is_accepted_and_next_message_is_rejected()
    {
        var exact = "{\"messages\":[" + string.Join(",", Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", TestSpecificationValidator.MaxMessageCount)) + "]}";
        var over = "{\"messages\":[" + string.Join(",", Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", TestSpecificationValidator.MaxMessageCount + 1)) + "]}";

        Assert.True(Validator().ValidateInput(exact).IsValid);
        Assert.Contains(Validator().ValidateInput(over).Issues, issue => issue.Code == "too_large");
    }

    [Fact]
    public void Exact_scalar_limit_is_accepted_and_next_character_is_rejected()
    {
        var exact = "{\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', TestSpecificationValidator.MaxScalarLength) + "\"}]}";
        var over = "{\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', TestSpecificationValidator.MaxScalarLength + 1) + "\"}]}";

        Assert.True(Validator().ValidateInput(exact).IsValid);
        Assert.Contains(Validator().ValidateInput(over).Issues, issue => issue.Code == "too_large");
    }

    [Fact]
    public void Oversized_utf8_input_reports_size_without_parsing()
    {
        var input = "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"metadata\":\""
            + new string('é', TestSpecificationValidator.MaxInputJsonBytes)
            + "\"}";

        var result = Validator().ValidateInput(input);

        Assert.Contains(result.Issues, issue => issue.Code == "too_large" && issue.Path == "inputSpecJson");
        Assert.True(Encoding.UTF8.GetByteCount(input) > TestSpecificationValidator.MaxInputJsonBytes);
    }

    [Fact]
    public void Exact_input_byte_limit_is_accepted_and_one_more_byte_is_rejected()
    {
        var exact = BuildExactSizedInput(TestSpecificationValidator.MaxInputJsonBytes);
        var over = exact + " ";

        Assert.Equal(TestSpecificationValidator.MaxInputJsonBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.True(Validator().ValidateInput(exact).IsValid);
        Assert.Contains(
            Validator().ValidateInput(over).Issues,
            issue => issue.Code == "too_large" && issue.Path == "inputSpecJson");
    }

    [Fact]
    public void Expectation_issues_are_delegated_and_prefixed()
    {
        var result = Validator().Validate(ValidInput, "[{\"type\":\"contains_text\"}]");

        Assert.Contains(
            result.Issues,
            issue => issue.Code == "required_expectation_field"
                && issue.Path == "expectationsJson[0].text");
    }

    [Fact]
    public void Empty_expectations_are_rejected_with_a_root_path()
    {
        var result = Validator().Validate(ValidInput, "[]");

        Assert.Contains(result.Issues, issue => issue.Code == "no_expectations" && issue.Path == "expectationsJson");
    }

    [Fact]
    public void Expectation_json_applies_structural_limits_before_dsl_validation()
    {
        var spy = new SpyExpectationValidator();
        var validator = new TestSpecificationValidator(spy);
        var duplicate = validator.Validate(
            ValidInput,
            "[{\"type\":\"contains_text\",\"text\":\"x\",\"text\":\"y\"}]");

        Assert.Contains(
            duplicate.Issues,
            issue => issue.Code == "duplicate_property" && issue.Path == "expectationsJson[0].text");
        Assert.Equal(0, spy.JsonCalls);

        var scalar = validator.Validate(
            ValidInput,
            "[{\"type\":\"contains_text\",\"text\":\""
                + new string('x', TestSpecificationValidator.MaxScalarLength + 1)
                + "\"}]");

        Assert.Contains(
            scalar.Issues,
            issue => issue.Code == "too_large" && issue.Path == "expectationsJson[0].text");
        Assert.Equal(0, spy.JsonCalls);

        var nested = "true";
        for (var index = 0; index < TestSpecificationValidator.MaxDepth + 1; index++)
        {
            nested = "{\"next\":" + nested + "}";
        }

        var deep = validator.Validate(
            ValidInput,
            "[{\"type\":\"contains_text\",\"text\":\"x\",\"metadata\":" + nested + "}]");

        Assert.Contains(deep.Issues, issue => issue.Code == "too_large");
        Assert.Equal(0, spy.JsonCalls);

        static string RepeatedExpectations(int count) =>
            "[" + string.Join(",", Enumerable.Repeat("{\"type\":\"contains_text\",\"text\":\"x\"}", count)) + "]";

        var exact = validator.Validate(
            ValidInput,
            RepeatedExpectations(TestSpecificationValidator.MaxExpectationCount));
        var over = validator.Validate(
            ValidInput,
            RepeatedExpectations(TestSpecificationValidator.MaxExpectationCount + 1));

        Assert.DoesNotContain(exact.Issues, issue => issue.Code == "too_large");
        Assert.Equal(1, spy.JsonCalls);
        Assert.Contains(
            over.Issues,
            issue => issue.Code == "too_large" && issue.Path == "expectationsJson");
        Assert.Equal(1, spy.JsonCalls);
    }

    [Fact]
    public void Unknown_input_schema_version_is_rejected_without_requiring_a_version_marker()
    {
        var accepted = Validator().ValidateInput(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"version\":1}");
        var rejected = Validator().ValidateInput(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"version\":2}");

        Assert.True(accepted.IsValid);
        Assert.Contains(
            rejected.Issues,
            issue => issue.Code == "unsupported_value" && issue.Path == "inputSpecJson.version");
    }

    [Fact]
    public void Exact_depth_limit_is_accepted_and_next_nested_object_is_rejected()
    {
        static string NestedMetadata(int objectCount)
        {
            var nested = "true";
            for (var index = 0; index < objectCount; index++)
            {
                nested = "{\"next\":" + nested + "}";
            }

            return "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"metadata\":"
                + nested
                + "}";
        }

        var exact = Validator().ValidateInput(NestedMetadata(TestSpecificationValidator.MaxDepth - 1));
        var over = Validator().ValidateInput(NestedMetadata(TestSpecificationValidator.MaxDepth));

        Assert.DoesNotContain(exact.Issues, issue => issue.Code == "too_large");
        Assert.Contains(over.Issues, issue => issue.Code == "too_large");
    }

    private static ITestSpecificationValidator Validator() =>
        new TestSpecificationValidator(new ExpectationDslValidator());

    private static string BuildExactSizedInput(int byteLength)
    {
        const string prefix = "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"metadata\":[";
        const string item = "\"x\"";
        const string suffix = "]}";
        var fixedLength = Encoding.UTF8.GetByteCount(prefix + item + suffix);
        var additionalItems = (byteLength - fixedLength) / 4;
        var remainder = (byteLength - fixedLength) % 4;
        return prefix
            + item
            + string.Concat(Enumerable.Repeat(",\"x\"", additionalItems))
            + new string(' ', remainder)
            + suffix;
    }

    private sealed class SpyExpectationValidator : IExpectationValidator
    {
        public int JsonCalls { get; private set; }

        public ExpectationValidationResult ValidateExpectationsJson(string expectationsJson)
        {
            JsonCalls++;
            return ExpectationValidationResult.Valid;
        }

        public ExpectationValidationResult ValidateExpectation(
            IReadOnlyDictionary<string, object> expectation) =>
            ExpectationValidationResult.Valid;
    }
}
