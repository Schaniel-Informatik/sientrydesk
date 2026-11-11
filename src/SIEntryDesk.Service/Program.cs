using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Texts;
using SIEntryDesk.Service;

// Command line in the language of Windows (German or English). The service itself uses the language of the
// installation, see below.
Tr.Use(Tr.FromCulture(System.Globalization.CultureInfo.CurrentUICulture));

if (args is [var helpArg] && helpArg.ToLowerInvariant() is "--help" or "-h" or "-?" or "/?" or "--hilfe")
{
    Console.WriteLine(Usage());
    return 0;
}

// For admins: show the certificate fingerprints of the console, to check them and take them into the configuration.
if (args is ["show-pins", var pinHost])
{
    if (Uri.CheckHostName(pinHost) == UriHostNameType.Unknown)
    {
        Console.Error.WriteLine(Tr.Admin("Ungültiger Host.", "Invalid host."));
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
            Console.WriteLine(Tr.Admin($"// {name}: Port {port} nicht erreichbar ({ex.GetType().Name})",
                $"// {name}: port {port} not reachable ({ex.GetType().Name})"));
            exit = 1;
        }
    }
    Console.WriteLine(Tr.Admin("// Nur übernehmen, wenn diese Verbindung sicher zur richtigen Konsole geht (Firmennetz oder VPN).",
        "// Only take these over if this connection surely goes to the right console (company network or VPN)."));
    Console.WriteLine(Tr.Admin("// StreamPin kann weggelassen werden, wenn er gleich wie ProtectPin ist.",
        "// StreamPin can be left out if it is the same as ProtectPin."));
    return exit;
}

// Setup (as administrator): read the credentials from standard input and store them encrypted.
//   Line 1: Access token, line 2: empty, line 3 optional: Protect API key for the live view.
//   Up to 0.5.2 line 2 was a separate token for unlocking. The format stays so that older credential scripts still fit.
if (args is ["set-secrets"])
{
    var accessToken = Console.In.ReadLine()?.Trim();
    var reserved = Console.In.ReadLine()?.Trim();
    var protectKey = Console.In.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(accessToken))
    {
        Console.Error.WriteLine(Tr.Admin("Kein Access-Token auf der Standardeingabe.", "No Access token on standard input."));
        return 2;
    }
    if (!string.IsNullOrEmpty(reserved))
    {
        Console.Error.WriteLine(Tr.Admin(
            "Zeile 2 muss leer sein: Ein eigener Token zum Öffnen wird nicht mehr unterstützt. " +
            "Der Access-Token braucht Gerät = Anzeigen und Standorte = Bearbeiten.",
            "Line 2 must be empty: a separate token for opening is no longer supported. " +
            "The Access token needs Device = View and Location = Edit."));
        return 2;
    }
    SecretStore.Save(new Secrets(accessToken, string.IsNullOrEmpty(protectKey) ? null : protectKey));
    Console.WriteLine(Tr.Admin($"Tokens gespeichert: {ServicePaths.SecretsFile}", $"Tokens stored: {ServicePaths.SecretsFile}"));
    return 0;
}

// As a service the program starts without arguments. Anything else is a typo and must not start the service.
if (args.Length > 0)
{
    Console.Error.WriteLine(Tr.Admin($"Unbekannter Aufruf: {string.Join(' ', args)}", $"Unknown call: {string.Join(' ', args)}"));
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

// Texts of the service (to the apps and in the log) in the language of the installation. An unknown value counts as
// German, the coordinator logs it.
Tr.TryParse(builder.Configuration["Language"], out var language);
Tr.Use(language);

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

static string Usage() => Tr.Admin($"""
    SI EntryDesk Dienst {ServiceState.Version}

    SIEntryDesk.Service.exe
        Der Dienst. install.ps1 richtet ihn als Windows-Dienst ein.

    SIEntryDesk.Service.exe show-pins <Konsole>
        Zertifikats-Fingerabdrücke der Konsole für die sientrydesk.json. Nur übernehmen,
        wenn die Verbindung sicher zur richtigen Konsole geht (Firmennetz oder VPN).

    SIEntryDesk.Service.exe set-secrets
        Zugänge von der Standardeingabe verschlüsselt speichern, als Administrator.
        Zeile 1 Access-Token, Zeile 2 leer, Zeile 3 Protect-Schlüssel.
        Einfacher: set-tokens.ps1 (von Hand) oder das Paket der Zugänge (Intune).

    SIEntryDesk.Service.exe --help
        Diese Hilfe.
    """, $"""
    SI EntryDesk service {ServiceState.Version}

    SIEntryDesk.Service.exe
        The service. install.ps1 sets it up as a Windows service.

    SIEntryDesk.Service.exe show-pins <console>
        Certificate fingerprints of the console for sientrydesk.json. Only take them over
        if the connection surely goes to the right console (company network or VPN).

    SIEntryDesk.Service.exe set-secrets
        Store the credentials from standard input encrypted, as administrator.
        Line 1 Access token, line 2 empty, line 3 Protect key.
        Easier: set-tokens.ps1 (by hand) or the credentials package (Intune).

    SIEntryDesk.Service.exe --help
        This help.
    """);
