using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("demo")]
public class DemoController : ControllerBase
{
    private readonly ILogger<DemoController> _logger;

    public DemoController(ILogger<DemoController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Demo chat endpoint that returns mock LLM-style responses
    /// </summary>
    [HttpPost("chat")]
    public IActionResult Chat([FromBody] DemoChatRequest request)
    {
        try
        {
            _logger.LogInformation("Demo chat endpoint called with {MessageCount} messages", request.Messages?.Count ?? 0);

            if (request.Messages == null || request.Messages.Count == 0)
            {
                return BadRequest(new { error = "No messages provided" });
            }

            // Get the last user message
            var lastUserMessage = request.Messages
                .LastOrDefault(m => m.Role == "user");

            var userContent = lastUserMessage?.Content ?? "Hello";

            // Generate canned responses based on keywords
            string responseContent;
            if (userContent.ToLower().Contains("hello") || userContent.ToLower().Contains("hi"))
            {
                responseContent = "Hello! I'm the Promptly demo assistant. I can help you test your LLM application monitoring. Try asking me about the weather, or anything else!";
            }
            else if (userContent.ToLower().Contains("weather"))
            {
                responseContent = "The weather today is sunny with a high of 72°F. It's a perfect day for testing your monitoring setup! For more details, visit https://example.com/weather.";
            }
            else if (userContent.ToLower().Contains("search") || userContent.ToLower().Contains("find"))
            {
                responseContent = "I'll search for that information for you. Based on my knowledge base, I found some relevant results.";
            }
            else
            {
                responseContent = $"You said: '{userContent}'. I'm a demo assistant, so my responses are pre-programmed. But in a real application, I'd provide a helpful response to your query!";
            }

            // Build response with OpenAI-like structure
            var response = new
            {
                id = $"demo-{Guid.NewGuid()}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = "demo-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = responseContent
                        },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = userContent.Split(' ').Length * 2,
                    completion_tokens = responseContent.Split(' ').Length * 2,
                    total_tokens = (userContent.Split(' ').Length + responseContent.Split(' ').Length) * 2
                },
                // Include demo tool calls if user asked to search
                tool_calls = userContent.ToLower().Contains("search") || userContent.ToLower().Contains("find")
                    ? new[]
                    {
                        new
                        {
                            id = "call_demo_1",
                            type = "function",
                            function = new
                            {
                                name = "search_knowledge_base",
                                arguments = JsonSerializer.Serialize(new
                                {
                                    query = userContent,
                                    limit = 5
                                })
                            }
                        }
                    }
                    : null,
                // Include demo retrieved docs for RAG testing
                retrieved_docs = userContent.ToLower().Contains("weather") || userContent.ToLower().Contains("search")
                    ? new[]
                    {
                        new
                        {
                            id = "doc1",
                            title = "Weather Information",
                            content = "Current weather conditions show sunny skies with temperatures around 72°F."
                        },
                        new
                        {
                            id = "doc2",
                            title = "Weather Forecast",
                            content = "The forecast for the next 3 days shows continued sunny weather with highs between 70-75°F."
                        }
                    }
                    : null
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in demo chat endpoint");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

public class DemoChatRequest
{
    public List<DemoMessage>? Messages { get; set; }
}

public class DemoMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
