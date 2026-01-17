using System.Text.Json;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Promptly.Infrastructure.Services;

public class YamlService : IYamlService
{
    private readonly ILogger<YamlService> _logger;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public YamlService(ILogger<YamlService> logger)
    {
        _logger = logger;

        // Configure YamlDotNet with snake_case naming convention
        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public List<TestCase> DeserializeTests(string yamlContent, Guid suiteId)
    {
        try
        {
            // Deserialize YAML to intermediate format
            var yamlTests = _deserializer.Deserialize<List<YamlTestCase>>(yamlContent);

            // Convert to TestCase entities
            var testCases = new List<TestCase>();
            foreach (var yamlTest in yamlTests)
            {
                var testCase = new TestCase
                {
                    SuiteId = suiteId,
                    ExternalId = yamlTest.Id,
                    Name = yamlTest.Name,
                    Description = yamlTest.Description,
                    InputSpecJson = JsonSerializer.Serialize(yamlTest.Input),
                    ExpectationsJson = JsonSerializer.Serialize(yamlTest.Expectations)
                };

                testCases.Add(testCase);
            }

            _logger.LogInformation("Deserialized {Count} test cases from YAML", testCases.Count);

            return testCases;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize YAML tests");
            throw new InvalidOperationException($"Failed to parse YAML: {ex.Message}", ex);
        }
    }

    public string SerializeTests(List<TestCase> testCases)
    {
        try
        {
            // Convert TestCase entities to YAML-friendly format
            var yamlTests = new List<YamlTestCase>();
            foreach (var testCase in testCases)
            {
                var input = JsonSerializer.Deserialize<Dictionary<string, object>>(testCase.InputSpecJson);
                var expectations = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(testCase.ExpectationsJson);

                var yamlTest = new YamlTestCase
                {
                    Id = testCase.ExternalId,
                    Name = testCase.Name,
                    Description = testCase.Description,
                    Input = input ?? new Dictionary<string, object>(),
                    Expectations = expectations ?? new List<Dictionary<string, object>>()
                };

                yamlTests.Add(yamlTest);
            }

            var yaml = _serializer.Serialize(yamlTests);

            _logger.LogInformation("Serialized {Count} test cases to YAML", testCases.Count);

            return yaml;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize test cases to YAML");
            throw new InvalidOperationException($"Failed to generate YAML: {ex.Message}", ex);
        }
    }

    // Internal classes for YAML serialization
    private class YamlTestCase
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Dictionary<string, object> Input { get; set; } = new();
        public List<Dictionary<string, object>> Expectations { get; set; } = new();
    }
}
