using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Service;

if (args is [var helpArg] && helpArg.ToLowerInvariant() is "--help" or "-h" or "-?" or "/?" or "--hilfe")
{
    Console.WriteLine(Usage());
    return 0;
}

// Für Admins: Zertifikats-Fingerabdrücke der Konsole anzeigen, zum Prüfen und Übernehmen in die Konfiguration.
if (args is ["show-pins", var pinHost])
{
    if (Uri.CheckHostName(pinHost) == UriHostNameType.Unknown)
    {
        Console.Error.WriteLine("Ungültiger Host.");
        return 2;
    }
    var exit = 0;
    foreach (var (name, port) in new[] { ("AccessPin", AccessApiClient.Port), ("ProtectPin", 443), ("StreamPin", 7441) })
    {
        try
        {
            var fingerprint = await CertificateProbe.FetchFingerprintAsync(pinHost, port, TimeSpan.FromSeconds(8), CancellationToken.None);
            Console.WriteLine($"// Port {port}");
            Console.WriteLine($"\"{name}\": \"{fingerprint}\",");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException
                                       or System.Security.Authentication.AuthenticationException or InvalidOperationException)
        {
            Console.WriteLine($"// {name}: Port {port} nicht erreichbar ({ex.GetType().Name})");
            exit = 1;
        }
    }
    Console.WriteLine("// Nur übernehmen, wenn diese Verbindung sicher zur richtigen Konsole geht (Firmennetz oder VPN).");
    Console.WriteLine("// StreamPin kann weggelassen werden, wenn er gleich wie ProtectPin ist.");
    return exit;
}

// Einrichtung (als Administrator): Zugänge von der Standardeingabe lesen und verschlüsselt ablegen.
//   Zeile 1: Access-Token, Zeile 2: leer, Zeile 3 optional: Protect-API-Schlüssel für das Livebild.
//   Zeile 2 war bis 0.5.2 ein eigener Token zum Öffnen. Das Format bleibt, damit ältere Skripte für die Zugänge passen.
if (args is ["set-secrets"])
{
    var accessToken = Console.In.ReadLine()?.Trim();
    var reserved = Console.In.ReadLine()?.Trim();
    var protectKey = Console.In.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(accessToken))
    {
        Console.Error.WriteLine("Kein Access-Token auf der Standardeingabe.");
        return 2;
    }
    if (!string.IsNullOrEmpty(reserved))
    {
        Console.Error.WriteLine("Zeile 2 muss leer sein: Ein eigener Token zum Öffnen wird nicht mehr unterstützt. " +
                                "Der Access-Token braucht Gerät = Anzeigen und Standorte = Bearbeiten.");
        return 2;
    }
    SecretStore.Save(new Secrets(accessToken, string.IsNullOrEmpty(protectKey) ? null : protectKey));
    Console.WriteLine($"Tokens gespeichert: {ServicePaths.SecretsFile}");
    return 0;
}

// Als Dienst startet das Programm ohne Argumente. Alles andere ist ein Tippfehler und soll nicht den Dienst starten.
if (args.Length > 0)
{
    Console.Error.WriteLine($"Unbekannter Aufruf: {string.Join(' ', args)}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage());
    return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = true,
});
builder.Services.AddWindowsService(o => o.ServiceName = ServicePaths.ServiceName);
builder.Configuration
    .AddJsonFile(ServicePaths.LegacyConfigFile, optional: true, reloadOnChange: false)
    .AddJsonFile(ServicePaths.ConfigFile, optional: true, reloadOnChange: false)
    .AddJsonFile(ServicePaths.LocalConfigFile, optional: true, reloadOnChange: false);
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

static string Usage() => $"""
    SI EntryDesk Dienst {ServiceState.Version}

    SIEntryDesk.Service.exe
        Der Dienst. install.ps1 richtet ihn als Windows-Dienst ein.

    SIEntryDesk.Service.exe show-pins <Konsole>
        Zertifikats-Fingerabdrücke der Konsole für die sientrydesk.json. Nur übernehmen,
        wenn die Verbindung sicher zur richtigen Konsole geht (Firmennetz oder VPN).

    SIEntryDesk.Service.exe set-secrets
        Zugänge von der Standardeingabe verschlüsselt speichern, als Administrator.
        Zeile 1 Access-Token, Zeile 2 leer, Zeile 3 Protect-Schlüssel.
        Einfacher: set-tokens.ps1 (von Hand) oder das Paket SI EntryDesk Zugänge (Intune).

    SIEntryDesk.Service.exe --help
        Diese Hilfe.
    """;
