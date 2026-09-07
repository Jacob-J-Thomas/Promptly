using System.Text.Json;
using System.Text.Json.Serialization;

namespace Promptly.Sdk.DotNet;

/// <summary>
/// Reads the numeric enum representation emitted by the API while retaining
/// the SDK's established string status properties for callers.
/// </summary>
public sealed class TestRunStatusStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? throw new JsonException("Run status cannot be null.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            return value switch
            {
                0 => "Queued",
                1 => "Running",
                2 => "Completed",
                3 => "Failed",
                _ => throw new JsonException($"Unknown run status value: {value}.")
            };
        }

        throw new JsonException("Run status must be a string or numeric enum value.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// Reads numeric test-result enum values emitted by the API as the SDK's
/// established string status properties.
/// </summary>
public sealed class TestResultStatusStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? throw new JsonException("Result status cannot be null.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            return value switch
            {
                0 => "Pass",
                1 => "Fail",
                2 => "Error",
                _ => throw new JsonException($"Unknown result status value: {value}.")
            };
        }

        throw new JsonException("Result status must be a string or numeric enum value.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
