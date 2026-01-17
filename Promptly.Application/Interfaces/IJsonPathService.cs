using System.Text.Json;

namespace Promptly.Application.Interfaces;

public interface IJsonPathService
{
    JsonElement? SelectToken(string jsonString, string jsonPath);
    IEnumerable<JsonElement> SelectTokens(string jsonString, string jsonPath);
}
