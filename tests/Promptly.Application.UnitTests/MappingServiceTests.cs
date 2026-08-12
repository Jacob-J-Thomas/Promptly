using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Services;
using Promptly.Infrastructure.Services;

namespace Promptly.Application.UnitTests;

public sealed class MappingServiceTests
{
    [Fact]
    public async Task ApplyMappingAsync_maps_the_complete_camelCase_schema_1_contract()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "messages": {
                "itemsPath": "$.choices[*].message",
                "rolePath": "$.role",
                "contentPath": "$.content"
              },
              "toolCalls": {
                "itemsPath": "$.tool_calls[*]",
                "namePath": "$.function.name",
                "argumentsPath": "$.function.arguments"
              },
              "usage": {
                "objectPath": "$.usage",
                "promptTokensPath": "$.prompt_tokens",
                "completionTokensPath": "$.completion_tokens",
                "totalTokensPath": "$.total_tokens",
                "costPath": "$.cost",
                "latencyMsPath": "$.latency_ms"
              },
              "retrievedDocs": {
                "itemsPath": "$.documents[*]",
                "idPath": "$.id",
                "titlePath": "$.title",
                "contentPath": "$.content",
                "metadataPath": "$.metadata"
              },
              "fallback": { "singleAssistantContentPath": "$.answer" }
            }
            """,
            """
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": ["first", {"type":"text","text":"second"}]
                }
              }],
              "tool_calls": [{
                "function": {"name":"lookup","arguments":{"id":42}}
              }],
              "usage": {
                "prompt_tokens": 7,
                "completion_tokens": 11,
                "total_tokens": 18,
                "cost": 0.125,
                "latency_ms": 321
              },
              "documents": [{
                "id": "doc-1",
                "title": "Reference",
                "content": "Ground truth",
                "metadata": {"rank":1,"source":"catalog"}
              }],
              "answer": "unused fallback"
            }
            """);

        Assert.True(result.Success, result.ErrorMessage);
        var trace = Assert.IsType<Promptly.Domain.ValueObjects.CanonicalTrace>(result.Trace);
        var message = Assert.Single(trace.Messages);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("first\n{\"type\":\"text\",\"text\":\"second\"}", message.Content);
        var toolCall = Assert.Single(trace.ToolCalls);
        Assert.Equal("lookup", toolCall.Name);
        Assert.Equal("{\"id\":42}", toolCall.ArgumentsJson);
        Assert.NotNull(trace.Usage);
        Assert.Equal(7, trace.Usage.PromptTokens);
        Assert.Equal(11, trace.Usage.CompletionTokens);
        Assert.Equal(18, trace.Usage.TotalTokens);
        Assert.Equal(0.125m, trace.Usage.Cost);
        Assert.Equal(321, trace.Usage.LatencyMs);
        var document = Assert.Single(trace.RetrievedDocs);
        Assert.Equal("doc-1", document.Id);
        Assert.Equal("Reference", document.Title);
        Assert.Equal("Ground truth", document.Content);
        Assert.NotNull(document.Metadata);
        Assert.Equal(1, Assert.IsType<JsonElement>(document.Metadata["rank"]).GetInt32());
        Assert.Equal(
            "catalog",
            Assert.IsType<JsonElement>(document.Metadata["source"]).GetString());
        Assert.Contains("\"choices\"", trace.RawResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_uses_fallback_when_messages_mapping_is_omitted()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            """{"answer":"hello"}""");

        Assert.True(result.Success, result.ErrorMessage);
        var message = Assert.Single(Assert.IsType<Promptly.Domain.ValueObjects.CanonicalTrace>(
            result.Trace).Messages);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_uses_fallback_when_messages_items_do_not_match()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "messages": {"itemsPath":"$.missing[*]"},
              "fallback": {"singleAssistantContentPath":"$.answer"}
            }
            """,
            """{"answer":"fallback value"}""");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            "fallback value",
            Assert.Single(result.Trace!.Messages).Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_discards_partial_messages_before_fallback()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "messages": {"itemsPath":"$.messages[*]"},
              "fallback": {"singleAssistantContentPath":"$.answer"}
            }
            """,
            """
            {
              "messages": [
                {"role":"assistant","content":"first"},
                {"role":"assistant"}
              ],
              "answer": "fallback"
            }
            """);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("fallback", Assert.Single(result.Trace!.Messages).Content);
    }

    [Theory]
    [InlineData(
        "{\"version\":1,\"messages\":{\"itemsPath\":\"$.missing[*]\"}}",
        "messages.itemsPath")]
    [InlineData(
        "{\"version\":1,\"toolCalls\":{\"itemsPath\":\"$.missing[*]\"}}",
        "toolCalls.itemsPath")]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.missing\",\"totalTokensPath\":\"$.total\"}}",
        "usage.objectPath")]
    [InlineData(
        "{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.documents[*]\",\"contentPath\":\"$.content\"}}",
        "retrievedDocs.contentPath")]
    [InlineData(
        "{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.missing\"}}",
        "fallback.singleAssistantContentPath")]
    public async Task ApplyMappingAsync_reports_configured_required_paths_that_do_not_match(
        string mappingSpec,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            mappingSpec,
            """{"documents":[{"title":"missing content"}]}""");

        Assert.False(result.Success);
        Assert.Null(result.Trace);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains(expectedPath, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("did not match", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", "mappingSpec")]
    [InlineData(" ", "mappingSpec")]
    [InlineData("null", "mappingSpec")]
    [InlineData("{\"Version\":1,\"Fallback\":{\"SingleAssistantContentPath\":\"$.answer\"}}", "Version")]
    [InlineData("{\"version\":\"one\",\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":5}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "mappingSpec")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.\\ud800\"}}", "mappingSpec")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[\\\"a\\tb\\\"]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$['a\\u001fb']\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"messages\":{\"itemsPath\":\"$.items[9007199254740992]\"},\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "messages.itemsPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[999999999999999999999999999999999999999]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":null,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":2,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\" \"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"usage\":{\"objectPath\":\"$.usage\"}}", "usage")]
    [InlineData("{\"version\":1,\"messages\":{}}", "messages.itemsPath")]
    [InlineData("{\"version\":1,\"toolCalls\":{}}", "toolCalls.itemsPath")]
    [InlineData("{\"version\":1,\"usage\":{\"totalTokensPath\":\"$.total\"}}", "usage.objectPath")]
    [InlineData("{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.docs[*]\"}}", "retrievedDocs.contentPath")]
    [InlineData("{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.docs[*]\",\"contentPath\":\"$.content\",\"idPath\":\" \"}}", "retrievedDocs.idPath")]
    public async Task ApplyMappingAsync_rejects_invalid_schema_1_specs(
        string mappingSpec,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(mappingSpec, """{"answer":"hello"}""");

        Assert.False(result.Success);
        Assert.Null(result.Trace);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains(expectedPath, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_preserves_optional_sections_and_string_arguments()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "toolCalls": {"itemsPath":"$.tools[*]"},
              "usage": {"objectPath":"$.usage","totalTokensPath":"$.total"},
              "retrievedDocs": {
                "itemsPath":"$.docs[*]",
                "idPath":"$.id",
                "contentPath":"$.content"
              }
            }
            """,
            """
            {
              "tools": [{"name":"search","arguments":"{\"query\":\"test\"}"}],
              "usage": {"total":3},
              "docs": [
                {"content":"without id"},
                {"id":2,"content":"numeric id"}
              ]
            }
            """);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Trace!.Messages);
        var toolCall = Assert.Single(result.Trace.ToolCalls);
        Assert.Equal("{\"query\":\"test\"}", toolCall.ArgumentsJson);
        Assert.NotNull(result.Trace.Usage);
        Assert.Null(result.Trace.Usage.PromptTokens);
        Assert.Null(result.Trace.Usage.CompletionTokens);
        Assert.Equal(3, result.Trace.Usage.TotalTokens);
        Assert.Null(result.Trace.Usage.Cost);
        Assert.Null(result.Trace.Usage.LatencyMs);
        Assert.Collection(
            result.Trace.RetrievedDocs,
            document => Assert.Null(document.Id),
            document => Assert.Equal("2", document.Id));
    }

    [Theory]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.usage\",\"totalTokensPath\":\"$.total\"}}",
        "{\"usage\":{\"total\":\"three\"}}",
        "usage.totalTokensPath")]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.usage\",\"latencyMsPath\":\"$.latency\"}}",
        "{\"usage\":{\"latency\":1.5}}",
        "usage.latencyMsPath")]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.usage\",\"costPath\":\"$.cost\"}}",
        "{\"usage\":{\"cost\":\"free\"}}",
        "usage.costPath")]
    public async Task ApplyMappingAsync_rejects_configured_usage_values_with_wrong_types(
        string mappingSpec,
        string responseJson,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(mappingSpec, responseJson);

        Assert.False(result.Success);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains(expectedPath, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "{\"version\":1,\"messages\":{\"itemsPath\":\"$.items[*]\"}}",
        "{\"items\":[{\"role\":false,\"content\":\"hello\"}]}",
        "messages.rolePath")]
    [InlineData(
        "{\"version\":1,\"toolCalls\":{\"itemsPath\":\"$.items[*]\"}}",
        "{\"items\":[{\"name\":{\"value\":\"search\"},\"arguments\":{}}]}",
        "toolCalls.namePath")]
    [InlineData(
        "{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.items[*]\",\"contentPath\":\"$.content\"}}",
        "{\"items\":[{\"content\":42}]}",
        "retrievedDocs.contentPath")]
    [InlineData(
        "{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}",
        "{\"answer\":{\"text\":\"hello\"}}",
        "fallback.singleAssistantContentPath")]
    public async Task ApplyMappingAsync_rejects_non_string_canonical_fields(
        string mappingSpec,
        string responseJson,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(mappingSpec, responseJson);

        Assert.False(result.Success);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains("did not match a string", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("idPath", "true", "string or number")]
    [InlineData("idPath", "{}", "string or number")]
    [InlineData("titlePath", "42", "string")]
    [InlineData("titlePath", "[]", "string")]
    public async Task ApplyMappingAsync_rejects_invalid_optional_document_field_types(
        string pathName,
        string valueJson,
        string expectedShape)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var spec = $$"""
            {
              "version": 1,
              "retrievedDocs": {
                "itemsPath": "$.docs[*]",
                "contentPath": "$.content",
                "{{pathName}}": "$.value"
              }
            }
            """;

        var result = await service.ApplyMappingAsync(
            spec,
            $$"""{"docs":[{"content":"hello","value":{{valueJson}}}]}""");

        Assert.False(result.Success);
        Assert.Equal(
            pathName == "idPath" ? "retrievedDocs.idPath" : "retrievedDocs.titlePath",
            result.ErrorPath);
        Assert.Contains(expectedShape, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_requires_each_configured_optional_doc_path_to_match()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "retrievedDocs": {
                "itemsPath":"$.docs[*]",
                "contentPath":"$.content",
                "titlePath":"$.title"
              }
            }
            """,
            """{"docs":[{"content":"first"},{"content":"second"}]}""");

        Assert.False(result.Success);
        Assert.Equal("retrievedDocs.titlePath", result.ErrorPath);
    }

    [Theory]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.usage[*]\",\"totalTokensPath\":\"$.total\"}}",
        "{\"usage\":[{\"total\":1},{\"total\":2}]}",
        "usage.objectPath")]
    [InlineData(
        "{\"version\":1,\"usage\":{\"objectPath\":\"$.usage\",\"totalTokensPath\":\"$.values[*]\"}}",
        "{\"usage\":{\"values\":[1,2]}}",
        "usage.totalTokensPath")]
    [InlineData(
        "{\"version\":1,\"messages\":{\"itemsPath\":\"$.messages[*]\",\"rolePath\":\"$.roles[*]\"}}",
        "{\"messages\":[{\"roles\":[\"assistant\",\"tool\"],\"content\":\"hello\"}]}",
        "messages.rolePath")]
    [InlineData(
        "{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.docs[*]\",\"contentPath\":\"$.content\",\"titlePath\":\"$.titles[*]\"}}",
        "{\"docs\":[{\"content\":\"hello\",\"titles\":[\"one\",\"two\"]}]}",
        "retrievedDocs.titlePath")]
    public async Task ApplyMappingAsync_rejects_multiple_matches_for_singular_paths(
        string mappingSpec,
        string responseJson,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(mappingSpec, responseJson);

        Assert.False(result.Success);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains("more than one value", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_rejects_non_object_retrieved_doc_metadata()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "retrievedDocs": {
                "itemsPath":"$.docs[*]",
                "contentPath":"$.content",
                "metadataPath":"$.metadata"
              }
            }
            """,
            """{"docs":[{"content":"first","metadata":"not an object"}]}""");

        Assert.False(result.Success);
        Assert.Equal("retrievedDocs.metadataPath", result.ErrorPath);
        Assert.Contains("did not match an object", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_requires_usage_object_path_to_select_an_object()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"usage":{"objectPath":"$.total","totalTokensPath":"$"}}""",
            """{"total":7}""");

        Assert.False(result.Success);
        Assert.Equal("usage.objectPath", result.ErrorPath);
        Assert.Contains("did not match an object", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "{\"version\":1,\"messages\":{\"itemsPath\":\"$.items[*]\",\"rolePath\":\"$\",\"contentPath\":\"$\"}}",
        "messages.itemsPath")]
    [InlineData(
        "{\"version\":1,\"toolCalls\":{\"itemsPath\":\"$.items[*]\",\"namePath\":\"$\",\"argumentsPath\":\"$\"}}",
        "toolCalls.itemsPath")]
    [InlineData(
        "{\"version\":1,\"retrievedDocs\":{\"itemsPath\":\"$.items[*]\",\"contentPath\":\"$\"}}",
        "retrievedDocs.itemsPath")]
    public async Task ApplyMappingAsync_requires_collection_paths_to_select_objects(
        string mappingSpec,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            mappingSpec,
            """{"items":["scalar"]}""");

        Assert.False(result.Success);
        Assert.Equal(expectedPath, result.ErrorPath);
        Assert.Contains("only objects", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_reports_the_fallback_path_when_messages_and_fallback_fail()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "messages": {"itemsPath":"$.messages[*]"},
              "fallback": {"singleAssistantContentPath":"$.answer"}
            }
            """,
            "{}");

        Assert.False(result.Success);
        Assert.Equal("fallback.singleAssistantContentPath", result.ErrorPath);
    }

    [Fact]
    public async Task ApplyMappingAsync_rejects_unknown_schema_fields()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """
            {
              "version": 1,
              "fallback": {"singleAssistantContentPath":"$.answer"},
              "unexpected": true
            }
            """,
            """{"answer":"hello"}""");

        Assert.False(result.Success);
        Assert.Null(result.Trace);
        Assert.Equal("unexpected", result.ErrorPath);
        Assert.Contains("unexpected", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("schema 1", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateMappingAsync_uses_the_same_schema_and_extraction_rules()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var valid = await service.ValidateMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            """{"answer":"preview"}""");
        var invalid = await service.ValidateMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.missing"}}""",
            """{"answer":"preview"}""");

        Assert.True(valid.Success, valid.ErrorMessage);
        Assert.Equal("preview", Assert.Single(valid.Trace!.Messages).Content);
        Assert.False(invalid.Success);
        Assert.Equal("fallback.singleAssistantContentPath", invalid.ErrorPath);
        Assert.Contains(
            "fallback.singleAssistantContentPath",
            invalid.ErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_accepts_canonical_bracket_quoted_members()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$['answer-key']"}}""",
            """{"answer-key":"bracket member"}""");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("bracket member", Assert.Single(result.Trace!.Messages).Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_does_not_treat_bracket_text_in_a_quoted_member_as_an_index()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$[\"[9007199254740992]\"]"}}""",
            """{"[9007199254740992]":"quoted member"}""");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("quoted member", Assert.Single(result.Trace!.Messages).Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_accepts_the_maximum_compatible_array_index()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"messages":{"itemsPath":"$.items[9007199254740991]"},"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            """{"items":[],"answer":"fallback"}""");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("fallback", Assert.Single(result.Trace!.Messages).Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_applies_wildcards_to_object_property_values()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"messages":{"itemsPath":"$.messages[*]"}}""",
            """
            {
              "messages": {
                "first": {"role":"assistant","content":"one"},
                "second": {"role":"tool","content":"two"}
              }
            }
            """);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Collection(
            result.Trace!.Messages,
            message => Assert.Equal(("assistant", "one"), (message.Role, message.Content)),
            message => Assert.Equal(("tool", "two"), (message.Role, message.Content)));
    }

    [Theory]
    [InlineData("$.answer[*]", "{\"answer\":\"hello\"}")]
    [InlineData("$.answer[0]", "{\"answer\":\"hello\"}")]
    [InlineData("$.answer", "{\"answer\":null}")]
    public async Task ApplyMappingAsync_does_not_treat_scalar_indexes_wildcards_or_null_as_matches(
        string path,
        string responseJson)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var spec = JsonSerializer.Serialize(new
        {
            version = 1,
            fallback = new { singleAssistantContentPath = path }
        });

        var result = await service.ApplyMappingAsync(spec, responseJson);

        Assert.False(result.Success);
        Assert.Equal("fallback.singleAssistantContentPath", result.ErrorPath);
        Assert.Contains("did not match", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyMappingAsync_caps_the_retained_raw_response()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var response = JsonSerializer.Serialize(new { answer = new string('x', 205 * 1024) });

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            response);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Trace);
        Assert.Equal(200 * 1024, Encoding.UTF8.GetByteCount(result.Trace.RawResponse!));
        Assert.EndsWith("\n... (truncated)", result.Trace.RawResponse, StringComparison.Ordinal);
        Assert.Equal(205 * 1024, Assert.Single(result.Trace.Messages).Content.Length);
    }

    [Fact]
    public async Task ApplyMappingAsync_caps_multibyte_raw_response_on_a_rune_boundary()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var content = string.Concat(Enumerable.Repeat("😀", 60 * 1024));
        var response = $$"""{"answer":"{{content}}"}""";

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            response);

        Assert.True(result.Success, result.ErrorMessage);
        var trace = Assert.IsType<Promptly.Domain.ValueObjects.CanonicalTrace>(result.Trace);
        var rawResponse = Assert.IsType<string>(trace.RawResponse);
        Assert.True(Encoding.UTF8.GetByteCount(rawResponse) <= 200 * 1024);
        Assert.EndsWith("\n... (truncated)", rawResponse, StringComparison.Ordinal);
        Assert.DoesNotContain(
            rawResponse.EnumerateRunes(),
            rune => rune == Rune.ReplacementChar);
        Assert.NotEmpty(JsonSerializer.Serialize(trace));
        Assert.Equal(content, Assert.Single(trace.Messages).Content);
    }

    [Fact]
    public async Task ApplyMappingAsync_does_not_expose_unexpected_exception_details()
    {
        await using var dbContext = CreateDbContext();
        var service = new MappingService(
            new ThrowingJsonPathService(),
            NullLogger<MappingService>.Instance,
            dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            """{"answer":"hello"}""");

        Assert.False(result.Success);
        Assert.Null(result.Trace);
        Assert.Equal("fallback.singleAssistantContentPath", result.ErrorPath);
        Assert.Contains("could not be evaluated", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"answer\":")]
    [InlineData("{\"answer\":\"first\",\"answer\":\"second\"}")]
    [InlineData("{\"answer\":\"ok\",\"other\":1,\"other\":2}")]
    [InlineData("{\"answer\":\"\\ud800\"}")]
    public async Task ApplyMappingAsync_rejects_noncanonical_response_json(string responseJson)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var result = await service.ApplyMappingAsync(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""",
            responseJson);

        Assert.False(result.Success);
        Assert.Null(result.Trace);
        Assert.Equal("responseJson", result.ErrorPath);
        Assert.DoesNotContain("first", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("second", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-mapping-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static MappingService CreateService(PromptlyDbContext dbContext) =>
        new(
            new JsonPathService(NullLogger<JsonPathService>.Instance),
            NullLogger<MappingService>.Instance,
            dbContext);

    private sealed class ThrowingJsonPathService : IJsonPathService
    {
        public JsonElement? SelectToken(string jsonString, string jsonPath) =>
            jsonString == "{}"
                ? null
                : throw new UnexpectedMappingException("secret implementation detail");

        public IEnumerable<JsonElement> SelectTokens(string jsonString, string jsonPath) =>
            throw new UnexpectedMappingException("secret implementation detail");
    }

    private sealed class UnexpectedMappingException(string message) : Exception(message);
}
