using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker;
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
        services.AddHttpClient<OpenAiHttpClient>((sp, client) => {
            var settings = sp.GetRequiredService<IOptions<OpenAISettings>>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.Key);
            client.Timeout = TimeSpan.FromSeconds(60);
            client.BaseAddress = new Uri("https://api.openai.com");
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("servicemockgenerator-backend"))
            .WithTracing(tracerProvider =>
            {
                tracerProvider.AddSource("api");
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
        context.CancellationToken.Register(() => {
            activity?.Dispose();
            context.InstanceServices.GetRequiredService<TracerProvider>().ForceFlush();
        });

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            activity?.RecordException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            activity?.Dispose();
            context.InstanceServices.GetRequiredService<TracerProvider>().ForceFlush();
        }
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
