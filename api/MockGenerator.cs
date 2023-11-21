using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using YamlDotNet.Serialization;

namespace Smg.Functions;

public class MockGenerator
{
    private const string SystemPrompt = "You're a sarcastic assistant that mocks an API for just existing in the world using some of it's information in your response";
    private readonly HttpClient _httpClient;
    private readonly IDeserializer _deserializer;
    private readonly IOptions<OpenAISettings> _openAISettings;

    public MockGenerator(
        IHttpClientFactory httpClientFactory,
        IDeserializer deserializer,
        IOptions<OpenAISettings> openAISettings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _deserializer = deserializer;
        _openAISettings = openAISettings;
    }

    private class OpenApiParseStatus
    {
        public bool IsSuccess { get; set; } = true;
        public string ErrorMessage { get; set; } = "";

        public static OpenApiParseStatus Success => new();
    }

    private async Task<(OpenApiParseStatus, OpenApiDoc?)> GetApiDescription(string openApiSpecUrl)
    {
        var openApiDocumentResponse = await _httpClient.GetAsync(openApiSpecUrl);
        if (!openApiDocumentResponse.IsSuccessStatusCode)
            return (new OpenApiParseStatus{ 
                ErrorMessage = "Unable to download URL"
                }, null);

        var doc = _deserializer.Deserialize<OpenApiDoc>(await openApiDocumentResponse.Content.ReadAsStringAsync());
        return (OpenApiParseStatus.Success, doc);
    }

    [Function("generate")]
    public async Task<HttpResponseData> Generate([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
    {
        var openApiUrl = req.Query["open-api-url"];
        Activity.Current?.SetTag("smg.openapi_url", openApiUrl);

        if (string.IsNullOrEmpty(openApiUrl) ||
            !(openApiUrl.StartsWith("https://") || openApiUrl.StartsWith("http://")))
        {
            var apiResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await apiResponse.WriteAsJsonAsync(new
            {
                error = "OpenAPI Url is required"
            });
            return apiResponse;
        }

        var (status, parsedOpenApiDocument) = await GetApiDescription(openApiUrl);
        if (!status.IsSuccess)
        {
            var apiResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await apiResponse.WriteAsJsonAsync(new
            {
                error = status.ErrorMessage
            });
            return apiResponse;            
        }

        _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + _openAISettings.Value.Key);
        _httpClient.Timeout = TimeSpan.FromSeconds(50);

        HttpResponseMessage generateResponse;
        try
        {
            generateResponse = await _httpClient.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", new OpenAIPostRequest
            {
                Messages = [
                    new OpenAIPostRequest.Message {
                        Role = "system",
                        Content = SystemPrompt
                    },
                    new OpenAIPostRequest.Message {
                        Role = "user",
                        Content = $"I've created an API called {parsedOpenApiDocument!.Info.Title}, here's the description {parsedOpenApiDocument!.Info.Description}. Please make this API feel bad for existing in the world"
                    }
                ]
            });
        }
        catch (Exception ex)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            Activity.Current?.RecordException(ex);

            var apiResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await apiResponse.WriteAsJsonAsync(new
            {
                error = "Failed to generate mock"
            });
            return apiResponse;
        }

        if (!generateResponse.IsSuccessStatusCode)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            Activity.Current?.SetTag("error.message", "Failed to generate mock");
            Activity.Current?.SetTag("error.type", "OpenAIError");
            Activity.Current?.SetTag("error.openai_response", await generateResponse.Content.ReadAsStringAsync());
            var apiResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await apiResponse.WriteAsJsonAsync(new
            {
                error = "Failed to generate mock"
            });
            return apiResponse;
        }

        var openAIResponse = await generateResponse.Content.ReadFromJsonAsync<OpenApiCompletionResponse>();

        Activity.Current?.SetOpenAICosts(openAIResponse?.Usage);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            response = openAIResponse?.Choices.FirstOrDefault()?.Message.Content ?? "Failed to generate mock"
        });

        return response;
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

public partial class OpenApiDoc
{
    public string Openapi { get; set; } = "";

    public Info Info { get; set; } = new Info();
}

public partial class Info
{
    public string Title { get; set; } = "";

    public string Description { get; set; } = "";
}