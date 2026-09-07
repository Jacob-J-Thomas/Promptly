using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Promptly.Domain.Entities;
using Promptly.Application.Models;
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

        Assert.Contains("id: case-1", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("value_kind", yaml, StringComparison.Ordinal);
        Assert.Collection(
            roundTripped,
            testCase =>
            {
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
}
