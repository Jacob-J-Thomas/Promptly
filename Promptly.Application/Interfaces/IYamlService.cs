using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IYamlService
{
    /// <summary>
    /// Deserialize YAML string into TestCase objects
    /// </summary>
    List<TestCase> DeserializeTests(string yamlContent, Guid suiteId);

    /// <summary>
    /// Serialize TestCase objects into YAML string
    /// </summary>
    string SerializeTests(List<TestCase> testCases);
}
