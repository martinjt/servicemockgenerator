using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Smg.Functions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.UseMiddleware<OpenTelemetryMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        var honeycombapikey = context.Configuration["HoneycombApiKey"];
        services.Configure<OpenAISettings>(context.Configuration.GetSection("OpenAI"));
        services.AddHttpClient<OpenAiHttpClient>((sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<OpenAISettings>>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.Key);
            client.Timeout = TimeSpan.FromSeconds(60);
            client.BaseAddress = new Uri("https://api.openai.com");
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("servicemockgenerator-backend"))
            .WithTracing(tracerProvider =>
            {
                tracerProvider.AddSource(DiagnosticConfig.Source.Name);
                tracerProvider.AddSource(OpenAiHttpClient.OpenAiSource.Name);
                tracerProvider.AddHttpClientInstrumentation();
                tracerProvider.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri("https://api.eu1.honeycomb.io:443");
                    o.Headers = $"x-honeycomb-team={honeycombapikey}";
                });
            });
        services.AddSingleton(_ => new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build());
    })
    .Build();

host.Run();

internal class OpenTelemetryMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        using var activity = DiagnosticConfig.Source.StartActivity(context.FunctionDefinition.Name);

        try
        {
            //add function RequestData to activity
            await SetFunctionInfo(activity, context);

            await next(context);
        }
        catch (Exception ex)
        {
            activity?.AddTag("http.response.status_code", HttpStatusCode.InternalServerError);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.RecordException(ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
            context.InstanceServices.GetRequiredService<TracerProvider>().ForceFlush();
        }
    }

    private static async Task SetFunctionInfo(Activity? activity, FunctionContext context)
    {
        if (activity == null)
            return;

        activity?.AddTag("faas.trigger", "http");
        activity?.AddTag("faas.id", context.FunctionDefinition.Id);
        activity?.AddTag("faas.name", context.FunctionDefinition.Name);
        activity?.AddTag("faas.entry_point", context.FunctionDefinition.EntryPoint);
        activity?.AddTag("faas.execution", context.InvocationId.ToString());

        var requestData = await context.GetHttpRequestDataAsync();
        if (requestData == null)
            return;

        SetHttpInfo(activity, requestData);
    }

    private static void SetHttpInfo(Activity? activity, HttpRequestData requestData)
    {
        if (activity == null || requestData == null)
            return;

        activity?.SetTag("http.request.method", requestData.Method);
        activity?.SetTag("http.request.content_length", requestData.Body.Length);
        activity?.SetTag("url.path", requestData.Url);
        activity?.SetTag("url.full", requestData.Url.AbsolutePath);
        activity?.SetTag("url.query", requestData.Url.Query);
        activity?.SetTag("server.address", requestData.Url.Host);
        activity?.SetTag("server.port", requestData.Url.Port);
        activity?.SetTag("http.user_agent", requestData.Headers.GetValues("User-Agent").FirstOrDefault());
        activity?.SetTag("client.address", requestData.Headers.GetValues("X-Forwarded-For").FirstOrDefault());
    }
}

internal static class DiagnosticConfig
{
    public static ActivitySource Source = new ActivitySource("api");
}

public class OpenAISettings
{
    public string SystemPrompt { get; set; } = "You're a sarcastic assistant that mocks an API for just existing in the world using some of it's information in your response";
    public string Key { get; set; }
    public string BaseAddress { get; set; } = null!;
}
