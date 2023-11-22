using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using YamlDotNet.Serialization;

namespace Smg.Functions;

public class MockGenerator(
    IHttpClientFactory httpClientFactory,
    IDeserializer deserializer,
    OpenAiHttpClient openAiHttpClient)
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();
    private readonly IDeserializer _deserializer = deserializer;
    private readonly OpenAiHttpClient _openAiHttpClient = openAiHttpClient;

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

        var openAIResponse = await _openAiHttpClient.GetChatCompletionForApi(parsedOpenApiDocument!.Info.Title, parsedOpenApiDocument!.Info.Description);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            response = openAIResponse?.Choices.FirstOrDefault()?.Message.Content ?? "Failed to generate mock"
        });

        return response;
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

