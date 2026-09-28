using SIEntryDesk.Core;
using SIEntryDesk.Service;

// Einrichtung (als Administrator): Tokens von der Standardeingabe lesen und verschlüsselt ablegen.
//   Zeile 1: Access-Token (view:device), Zeile 2 optional: eigener Token zum Öffnen (edit:space),
//   Zeile 3 optional: Protect-API-Schlüssel für das Livebild
if (args is ["set-secrets"])
{
    var accessToken = Console.In.ReadLine()?.Trim();
    var unlockToken = Console.In.ReadLine()?.Trim();
    var protectKey = Console.In.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(accessToken))
    {
        Console.Error.WriteLine("Kein Access-Token auf der Standardeingabe.");
        return 2;
    }
    SecretStore.Save(new Secrets(
        accessToken,
        string.IsNullOrEmpty(unlockToken) ? null : unlockToken,
        string.IsNullOrEmpty(protectKey) ? null : protectKey));
    Console.WriteLine($"Tokens gespeichert: {ServicePaths.SecretsFile}");
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = true,
});
builder.Services.AddWindowsService(o => o.ServiceName = ServicePaths.ServiceName);
builder.Configuration.AddJsonFile(ServicePaths.ConfigFile, optional: true, reloadOnChange: false);
builder.Services.Configure<EntryDeskOptions>(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(ServicePaths.LogDirectory));
if (Environment.UserInteractive)
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServiceState>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipeServer>());
builder.Services.AddHostedService<CallCoordinator>();

await builder.Build().RunAsync();
return 0;
