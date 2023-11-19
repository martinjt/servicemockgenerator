using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace api
{
    public class MockGenerator
    {
        private readonly HttpClient _httpClient;
        private readonly IOptions<OpenAISettings> _openAISettings;
        private readonly ILogger<MockGenerator> _logger;

        public MockGenerator(
            IHttpClientFactory httpClientFactory,
            IOptions<OpenAISettings> openAISettings,
            ILogger<MockGenerator> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _openAISettings = openAISettings;
            _logger = logger;
        }

        [Function("generate")]
        public async Task<HttpResponseData> Generate([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {
            _logger.LogInformation("OpenAI Key: {Key}", _openAISettings.Value.Key);

            var serviceName = req.Query["service-name"];
            _httpClient.BaseAddress = new Uri("https://api.openai.com/");
            _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + _openAISettings.Value.Key);
            var generateResponse = await _httpClient.PostAsJsonAsync("v1/chat/completions", new OpenAIPostRequest {
                Messages = [
                    new OpenAIPostRequest.Message {
                        Role = "system",
                        Content = "You're a comedian who mocks web services based purely on their name to make the service feel bad"
                    },
                    new OpenAIPostRequest.Message {
                        Role = "user",
                        Content = $"Mock my {serviceName}"
                    }
                ]
            });

            var openAIResponse = await generateResponse.Content.ReadFromJsonAsync<OpenApiCompletionResponse>();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new {
                response = openAIResponse?.Choices.FirstOrDefault()?.Message.Content ?? "Failed to generate mock"
            });

            return response;
        }
    }

    public class OpenAIPostRequest
    {
        public string Model { get; set; } = "gpt-3.5-turbo";
        public List<Message> Messages { get; set; } = new List<Message>();
        public class Message { 
            public string Role { get; set;}
            public string Content { get; set; }
        }
    }

    public class Message
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }

    public class OpenApiCompletionResponse
    {
        public string Id { get; set; }

        [JsonPropertyName("object")]
        public string TargetObject { get; set; }
        public int Created { get; set; }
        public string Model { get; set; }
        public List<Choice> Choices { get; set; }
        public Usage Usage { get; set; }
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
        public Message Message { get; set; }
        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; }
    }


}
