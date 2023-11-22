using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;

namespace Smg.Functions;

/// <summary>
/// Typed Httpclient for openAI
/// </summary>
public class OpenAiHttpClient(HttpClient httpClient, IOptions<OpenAISettings> settings)
{
    public async Task<OpenApiCompletionResponse?> GetChatCompletionForApi(string title, string description)
    {
        HttpResponseMessage generateResponse;
        try
        {
            generateResponse = await httpClient.PostAsJsonAsync("v1/chat/completions", new OpenAIPostRequest
            {
                Messages = [
                new OpenAIPostRequest.Message {
                        Role = "system",
                        Content = settings.Value.SystemPrompt
                    },
                    new OpenAIPostRequest.Message {
                        Role = "user",
                        Content = $"I've created an API called {title}, here's the description {description}. Please make this API feel bad for existing in the world"
                    }
                ]
            });

            if (!generateResponse.IsSuccessStatusCode)
            {
                Activity.Current?.SetStatus(ActivityStatusCode.Error);
                Activity.Current?.SetTag("error.message", "Failed to generate mock");
                Activity.Current?.SetTag("error.type", "OpenAIError");
                Activity.Current?.SetTag("error.openai_response", await generateResponse.Content.ReadAsStringAsync());
                return null;
            }

            var completionResponse = await generateResponse.Content.ReadFromJsonAsync<OpenApiCompletionResponse>();
            Activity.Current?.SetOpenAICosts(completionResponse?.Usage);
            return completionResponse;
        }
        catch (Exception ex)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            Activity.Current?.RecordException(ex);
            return null;
        }
    }

}

public class OpenApiCompletionResponse
{
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string TargetObject { get; set; } = "";
    public int Created { get; set; }
    public string Model { get; set; } = "";
    public List<Choice> Choices { get; set; } = new List<Choice>();
    public Usage Usage { get; set; } = new Usage();
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class Choice
{
    public int Index { get; set; }
    public Message Message { get; set; } = new Message();
    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "";
}


public static class ActivityExtensions
{
    public static void SetOpenAICosts(this Activity activity, Usage? usage)
    {
        if (usage == null) return;
        activity.SetTag("openai.cost.prompt_tokens", usage.PromptTokens);
        activity.SetTag("openai.cost.completion_tokens", usage.CompletionTokens);
        activity.SetTag("openai.cost.total_tokens", usage.TotalTokens);
    }
}

public class OpenAIPostRequest
{
    public string Model { get; set; } = "gpt-3.5-turbo";
    public List<Message> Messages { get; set; } = new List<Message>();
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 300;
    public class Message
    {
        public string Role { get; set; } = "System";
        public string Content { get; set; } = "";
    }
}

public class Message
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}
