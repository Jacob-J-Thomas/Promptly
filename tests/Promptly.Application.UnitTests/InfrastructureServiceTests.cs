using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Infrastructure.Configuration;
using Promptly.Infrastructure.Services;

namespace Promptly.Application.UnitTests;

public sealed class DataProtectionEncryptionServiceTests
{
    [Fact]
    public void Encryption_round_trips_without_exposing_the_plaintext()
    {
        var service = CreateService();

        var cipherText = service.Encrypt("sensitive-value");

        Assert.NotEqual("sensitive-value", cipherText);
        Assert.Equal("sensitive-value", service.Decrypt(cipherText));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_values_are_preserved(string? value)
    {
        var service = CreateService();

        Assert.Equal(value, service.Encrypt(value!));
        Assert.Equal(value, service.Decrypt(value!));
    }

    [Fact]
    public void Decryption_wraps_data_protected_by_a_different_provider()
    {
        var cipherText = CreateService().Encrypt("sensitive-value");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateService().Decrypt(cipherText));

        Assert.Contains("encryption key may have changed", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    private static DataProtectionEncryptionService CreateService() =>
        new(new EphemeralDataProtectionProvider());
}

public sealed class JwtServiceTests
{
    [Fact]
    public void GenerateToken_emits_a_valid_signed_identity_token()
    {
        var now = DateTime.UtcNow;
        var settings = new JwtSettings
        {
            Issuer = "promptly-tests",
            Audience = "promptly-clients",
            Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ExpiryMinutes = 15
        };
        var service = new JwtService(Options.Create(settings));
        var user = new User
        {
            Id = "user-123",
            Email = "user@example.test",
            UserName = "test-user"
        };

        var encodedToken = service.GenerateToken(user);

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(
            encodedToken,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(settings.Key)),
                ClockSkew = TimeSpan.Zero
            },
            out var validatedToken);

        var jwt = Assert.IsType<JwtSecurityToken>(validatedToken);
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Header.Alg);
        Assert.Equal("user-123", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("user@example.test", principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal("test-user", principal.FindFirstValue(ClaimTypes.Name));
        Assert.InRange(jwt.ValidTo, now.AddMinutes(14), now.AddMinutes(16));
    }
}

public sealed class YamlServiceTests
{
    private readonly YamlService _service = new(NullLogger<YamlService>.Instance);

    [Fact]
    public void DeserializeTests_rejects_unknown_row_fields()
    {
        var suiteId = Guid.NewGuid();
        const string yaml = """
            - id: case-1
              name: Greeting
              description: Greets the caller
              ignored_field: ignored
              input:
                messages:
                  - role: user
                    content: Hello
              expectations:
                - type: contains_text
                  text: Hello
            """;

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.DeserializeTests(yaml, suiteId));

        Assert.Contains(exception.Issues, issue =>
            issue.Code == "unsupported_value" && issue.Path == "rows[0].ignored_field");
    }

    [Fact]
    public void DeserializeTests_rejects_typed_yaml_identifiers_and_descriptions()
    {
        const string yaml = """
            - id: 123
              name: true
              description: 5
              input:
                messages:
                  - role: user
                    content: Hello
              expectations:
                - type: contains_text
                  text: Hello
            """;

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.DeserializeTests(yaml, Guid.NewGuid()));

        Assert.Contains(exception.Issues, issue => issue.Path == "rows[0].id");
        Assert.Contains(exception.Issues, issue => issue.Path == "rows[0].name");
        Assert.Contains(exception.Issues, issue => issue.Path == "rows[0].description");
        Assert.All(
            exception.Issues.Where(issue => issue.Path.StartsWith("rows[0.", StringComparison.Ordinal)),
            issue => Assert.Equal("invalid_type", issue.Code));
    }

    [Fact]
    public void SerializeTests_round_trips_nested_metadata_without_value_kind_fields()
    {
        var original = new List<TestCase>
        {
            new()
            {
                ExternalId = "case-1",
                Name = "Greeting",
                Description = "Greets the caller",
                InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}],\"temperature\":0.5,\"enabled\":true,\"metadata\":{\"tags\":[\"demo\",null]}}",
                ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"Hello\"}]"
            },
            new()
            {
                ExternalId = "case-2",
                Name = "Defaults",
                InputSpecJson = "{\"messages\":[{\"role\":\"assistant\",\"content\":\"Done\"}],\"metadata\":null}",
                ExpectationsJson = "[{\"type\":\"groundedness\",\"min_score\":0.8}]"
            }
        };

        var yaml = _service.SerializeTests(original);
        var roundTripped = _service.DeserializeTests(yaml, Guid.NewGuid());

        Assert.DoesNotContain("value_kind", yaml, StringComparison.Ordinal);
        Assert.Collection(
            roundTripped,
            testCase =>
            {
                Assert.Equal("case-1", testCase.ExternalId);
                Assert.Equal("Greeting", testCase.Name);
                Assert.Equal("Greets the caller", testCase.Description);
                using var input = System.Text.Json.JsonDocument.Parse(testCase.InputSpecJson);
                Assert.Equal(0.5, input.RootElement.GetProperty("temperature").GetDouble());
                Assert.True(input.RootElement.GetProperty("enabled").GetBoolean());
                Assert.Null(input.RootElement.GetProperty("metadata").GetProperty("tags")[1].GetString());
                using var expectations = System.Text.Json.JsonDocument.Parse(testCase.ExpectationsJson);
                Assert.Equal("contains_text", expectations.RootElement[0].GetProperty("type").GetString());
            },
            testCase =>
            {
                Assert.Equal("case-2", testCase.ExternalId);
                Assert.Null(testCase.Description);
                using var input = System.Text.Json.JsonDocument.Parse(testCase.InputSpecJson);
                Assert.True(input.RootElement.GetProperty("metadata").ValueKind == System.Text.Json.JsonValueKind.Null);
                using var expectations = System.Text.Json.JsonDocument.Parse(testCase.ExpectationsJson);
                Assert.Equal(0.8, expectations.RootElement[0].GetProperty("min_score").GetDouble());
            });
    }

    [Fact]
    public void Empty_documents_round_trip_as_an_empty_test_collection()
    {
        var yaml = _service.SerializeTests([]);

        Assert.Empty(_service.DeserializeTests(yaml, Guid.NewGuid()));
    }

    [Fact]
    public void DeserializeTests_returns_safe_invalid_yaml_issue()
    {
        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.DeserializeTests("tests: [unterminated", Guid.NewGuid()));

        Assert.Contains(exception.Issues, issue => issue.Code == "invalid_yaml" && issue.Path == "$");
        Assert.DoesNotContain("unterminated", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&case")]
    [InlineData("*case")]
    [InlineData("!!str")]
    public void DeserializeTests_rejects_aliases_and_tags_before_loading_the_node_graph(string marker)
    {
        var yaml = marker switch
        {
            "&case" => "- &case\n  id: case-1\n  name: Case\n  input:\n    messages:\n      - role: user\n        content: hello\n  expectations:\n    - type: contains_text\n      text: hello\n",
            "*case" => "- id: case-1\n  name: Case\n  input:\n    messages:\n      - role: user\n        content: hello\n  expectations:\n    - type: contains_text\n      text: hello\n- *case\n",
            _ => "- id: !!str case-1\n  name: Case\n  input:\n    messages:\n      - role: user\n        content: hello\n  expectations:\n    - type: contains_text\n      text: hello\n"
        };

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.DeserializeTests(yaml, Guid.NewGuid()));

        Assert.Contains(
            exception.Issues,
            issue => issue.Code == "unsupported_value"
                && issue.Message.Contains("not supported", StringComparison.Ordinal));
        Assert.DoesNotContain("AnchorName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeTests_preserves_marker_characters_inside_quoted_scalars_and_comments()
    {
        const string yaml = """
            - id: quoted-case
              name: "A *quoted !name"
              description: 'A ''quoted'' value &anchor'
              input:
                messages:
                  - role: user
                    content: "Hello \"world\" *alias !tag" # marker-like comment
              expectations:
                - type: contains_text
                  text: hello
            """;

        var testCase = Assert.Single(_service.DeserializeTests(yaml, Guid.NewGuid()));

        Assert.Equal("A 'quoted' value &anchor", testCase.Description);
        Assert.Contains("*alias", testCase.InputSpecJson, StringComparison.Ordinal);
        Assert.Contains("!tag", testCase.InputSpecJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeTests_preserves_wide_json_numbers_as_numbers()
    {
        const string yaml = """
            - id: wide-number
              name: Wide number
              input:
                messages:
                  - role: user
                    content: hello
                score: 1e100
              expectations:
                - type: contains_text
                  text: hello
            """;

        var testCase = Assert.Single(_service.DeserializeTests(yaml, Guid.NewGuid()));

        using var input = System.Text.Json.JsonDocument.Parse(testCase.InputSpecJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, input.RootElement.GetProperty("score").ValueKind);
        Assert.Equal("1e100", input.RootElement.GetProperty("score").GetRawText());
    }

    [Fact]
    public void DeserializeTests_reports_bounded_document_shape_and_row_issues()
    {
        AssertIssue(null, "invalid_yaml", "$");
        AssertIssue("  \n", "invalid_yaml", "$");
        AssertIssue(new string('x', YamlService.MaxYamlBytes + 1), "too_large", "$");
        AssertIssue(ValidYamlRow("first") + "\n---\n" + ValidYamlRow("second"), "invalid_yaml", "$");
        AssertIssue("version: 1\n", "invalid_shape", "$");
        AssertIssue("version: 2\n", "unsupported_value", "$.version");
        AssertIssue("version: true\n", "unsupported_value", "$.version");
        AssertIssue("version: \"1\"\n", "unsupported_value", "$.version");
        AssertIssue("version:\n  - 1\n", "unsupported_value", "$.version");
        AssertIssue("- true\n", "invalid_type", "rows[0]");
        AssertIssue("- name: only-name\n", "required", "rows[0].id");
        AssertIssue("- id: only-id\n", "required", "rows[0].name");
        AssertIssue(
            "- id: missing-input\n"
            + "  name: Missing input\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n",
            "required",
            "rows[0].input");
        AssertIssue(
            "- id: missing-expectations\n"
            + "  name: Missing expectations\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n",
            "required",
            "rows[0].expectations");
        AssertIssue(
            "- id: shape\n"
            + "  name: Shape\n"
            + "  description:\n"
            + "    nested: true\n"
            + "  input: text\n"
            + "  expectations: text\n",
            "invalid_type",
            "rows[0].description");
        AssertIssue(
            "- id: input-shape\n"
            + "  name: Input shape\n"
            + "  input: text\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n",
            "invalid_type",
            "rows[0].input");
        AssertIssue(
            "- id: expectations-shape\n"
            + "  name: Expectations shape\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "  expectations: text\n",
            "invalid_type",
            "rows[0].expectations");
        AssertIssue(
            "- id: invalid-input\n"
            + "  name: Invalid input\n"
            + "  input:\n"
            + "    messages: []\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n",
            "required",
            "rows[0].input.messages");
        AssertIssue(
            "- id: invalid-expectations\n"
            + "  name: Invalid expectations\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "  expectations: []\n",
            "no_expectations",
            "rows[0].expectations");
        AssertIssue(
            "- id: duplicate\n"
            + "  id: duplicate-again\n"
            + "  name: Duplicate\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n",
            "invalid_yaml",
            "$");
        AssertIssue(
            "- id: [typed]\n"
            + "  name: {typed: true}\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n",
            "invalid_type",
            "rows[0].id");

        static void AssertIssue(string? yaml, string code, string path)
        {
            var service = new YamlService(NullLogger<YamlService>.Instance);
            var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            {
                service.DeserializeTests(yaml!, Guid.NewGuid());
            });
            Assert.Contains(exception.Issues, issue => issue.Code == code && issue.Path == path);
        }
    }

    [Fact]
    public void DeserializeTests_enforces_row_and_scalar_limits_at_the_boundary()
    {
        var exactRows = string.Join(
            "\n",
            Enumerable.Range(0, TestSpecificationValidator.MaxMessageCount)
                .Select(index => ValidYamlRow($"row-{index}")));
        var overRows = exactRows + "\n" + ValidYamlRow("row-over");
        var exactTests = new YamlService(NullLogger<YamlService>.Instance)
            .DeserializeTests(exactRows, Guid.NewGuid());
        Assert.Equal(TestSpecificationValidator.MaxMessageCount, exactTests.Count);
        Assert.Throws<TestSpecificationValidationException>(() =>
        {
            new YamlService(NullLogger<YamlService>.Instance)
                .DeserializeTests(overRows, Guid.NewGuid());
        });

        var longId = "- id: " + new string('x', TestSpecificationValidator.MaxScalarLength + 1) + "\n"
            + "  name: Name\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n";
        var longIdException = Assert.Throws<TestSpecificationValidationException>(() =>
        {
            new YamlService(NullLogger<YamlService>.Instance)
                .DeserializeTests(longId, Guid.NewGuid());
        });
        Assert.Contains(longIdException.Issues, issue => issue.Code == "too_large" && issue.Path == "rows[0].id");

        var longName = ValidYamlRow("long-name").Replace(
            "name: Test long-name",
            "name: " + new string('x', TestSpecificationValidator.MaxScalarLength + 1),
            StringComparison.Ordinal);
        var longNameException = Assert.Throws<TestSpecificationValidationException>(() =>
        {
            new YamlService(NullLogger<YamlService>.Instance)
                .DeserializeTests(longName, Guid.NewGuid());
        });
        Assert.Contains(longNameException.Issues, issue => issue.Code == "too_large" && issue.Path == "rows[0].name");

        var invalidNumber = ValidYamlRow("invalid-number").Replace(
            "content: hello",
            "content: 1e+",
            StringComparison.Ordinal);
        var invalidNumberException = Assert.Throws<TestSpecificationValidationException>(() =>
        {
            new YamlService(NullLogger<YamlService>.Instance)
                .DeserializeTests(invalidNumber, Guid.NewGuid());
        });
        Assert.Contains(invalidNumberException.Issues, issue => issue.Code == "invalid_type");

        var longDescription = ValidYamlRow("long-description").Replace(
            "description: description",
            "description: " + new string('x', TestSpecificationValidator.MaxScalarLength + 1),
            StringComparison.Ordinal);
        var longDescriptionException = Assert.Throws<TestSpecificationValidationException>(() =>
        {
            new YamlService(NullLogger<YamlService>.Instance)
                .DeserializeTests(longDescription, Guid.NewGuid());
        });
        Assert.Contains(
            longDescriptionException.Issues,
            issue => issue.Code == "too_large" && issue.Path == "rows[0].description");
    }

    [Fact]
    public void DeserializeTests_rejects_deep_yaml_before_json_conversion()
    {
        var nested = new StringBuilder("{");
        for (var index = 0; index < TestSpecificationValidator.MaxDepth + 1; index++)
        {
            nested.Append("\"next\":{");
        }

        nested.Append("\"value\":true");
        nested.Append('}', TestSpecificationValidator.MaxDepth + 2);
        var yaml = "- id: deep\n"
            + "  name: Deep\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "    metadata: "
            + nested
            + "\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n";

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
        {
            _service.DeserializeTests(yaml, Guid.NewGuid());
        });

        Assert.Contains(exception.Issues, issue => issue.Code == "too_large");
    }

    [Fact]
    public void DeserializeTests_rejects_a_large_yaml_event_stream_before_materializing_it()
    {
        var values = string.Join(",", Enumerable.Repeat("x", YamlService.MaxYamlNodes));
        var yaml = "- id: node-budget\n"
            + "  name: Node budget\n"
            + "  input:\n"
            + "    messages:\n"
            + "      - role: user\n"
            + "        content: hello\n"
            + "    metadata: ["
            + values
            + "]\n"
            + "  expectations:\n"
            + "    - type: contains_text\n"
            + "      text: hello\n";

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.DeserializeTests(yaml, Guid.NewGuid()));

        Assert.Contains(exception.Issues, issue =>
            issue.Code == "too_large"
            && issue.Message.Contains("node count", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-json", "[]")]
    [InlineData("{}", "not-json")]
    public void SerializeTests_returns_safe_invalid_json_issue(
        string inputSpecJson,
        string expectationsJson)
    {
        var testCase = new TestCase
        {
            ExternalId = "invalid",
            Name = "Invalid",
            InputSpecJson = inputSpecJson,
            ExpectationsJson = expectationsJson
        };

        var exception = Assert.Throws<TestSpecificationValidationException>(() =>
            _service.SerializeTests([testCase]));

        Assert.Contains(exception.Issues, issue => issue.Code is "invalid_json" or "invalid_expectations_json");
        Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
    }

    private static string ValidYamlRow(string externalId) => $$"""
        - id: {{externalId}}
          name: Test {{externalId}}
          description: description
          input:
            messages:
              - role: user
                content: hello
          expectations:
            - type: contains_text
              text: hello
        """;
}
