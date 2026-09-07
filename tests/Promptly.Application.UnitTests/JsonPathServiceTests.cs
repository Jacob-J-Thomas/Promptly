using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Infrastructure.Services;

namespace Promptly.Application.UnitTests;

public sealed class JsonPathServiceTests
{
    private readonly JsonPathService _service = new(NullLogger<JsonPathService>.Instance);

    [Fact]
    public void SelectToken_returns_a_scalar_match()
    {
        var result = _service.SelectToken(
            """
            { "answer": { "value": 42 } }
            """,
            "$.answer.value");

        var value = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(42, value.GetInt32());
    }

    [Fact]
    public void SelectTokens_returns_all_matches_in_document_order()
    {
        var results = _service.SelectTokens(
            """
            { "items": [{ "name": "first" }, { "name": "second" }] }
            """,
            "$.items[*].name").ToArray();

        Assert.Collection(
            results,
            result => Assert.Equal("first", result.GetString()),
            result => Assert.Equal("second", result.GetString()));
    }

    [Fact]
    public void Selection_reports_no_match_without_throwing()
    {
        const string json = """{ "present": true }""";

        Assert.Null(_service.SelectToken(json, "$.missing"));
        Assert.Empty(_service.SelectTokens(json, "$.missing"));
    }

    [Fact]
    public void SelectToken_wraps_an_invalid_path_with_context()
    {
        const string invalidPath = "$[";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _service.SelectToken("{}", invalidPath));

        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }
}
