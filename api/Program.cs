using api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.Configure<OpenAISettings>(context.Configuration.GetSection("OpenAI"));
        services.AddSingleton<MockGenerator>();
        services.AddHttpClient();
        services.AddHttpClient<MockGenerator>();
    })
    .Build();

host.Run();

public class OpenAISettings
{
    public string Key { get; set; }
}