using System.Security.Cryptography;
using System.Text.Json;

namespace SIEntryDesk.Service;

/// <param name="AccessToken">Access-Token für die Ereignisse (view:device).</param>
/// <param name="AccessUnlockToken">Optional eigener Token zum Öffnen (edit:space), sonst wird AccessToken verwendet.</param>
internal sealed record Secrets(string AccessToken, string? AccessUnlockToken);

/// <summary>
/// Tokens verschlüsselt mit DPAPI (Maschinenschlüssel) in secrets.dat. Schutz vor Standardbenutzern
/// bieten die Dateirechte, lokale Administratoren können die Tokens lesen.
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = "SIEntryDesk/secrets/v1"u8.ToArray();

    public static Secrets Load()
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(ServicePaths.SecretsFile), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            var secrets = JsonSerializer.Deserialize<Secrets>(plain);
            if (secrets is null || string.IsNullOrWhiteSpace(secrets.AccessToken))
                throw new InvalidDataException("secrets.dat enthält keinen Access-Token");
            return secrets with { AccessUnlockToken = string.IsNullOrWhiteSpace(secrets.AccessUnlockToken) ? null : secrets.AccessUnlockToken };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Save(Secrets secrets)
    {
        Directory.CreateDirectory(ServicePaths.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            var temp = ServicePaths.SecretsFile + ".neu";
            File.WriteAllBytes(temp, blob);
            File.Move(temp, ServicePaths.SecretsFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
