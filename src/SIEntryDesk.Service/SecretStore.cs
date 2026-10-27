using System.Security.Cryptography;
using System.Text.Json;

namespace SIEntryDesk.Service;

/// <param name="AccessToken">Access token for events and unlocking (device = view, sites = edit).</param>
/// <param name="ProtectKey">Optional API key from UniFi OS → Integrations for the live view.</param>
/// <remarks>A separate token only for unlocking was supported up to 0.5.2. Such a field in older secrets.dat is
/// ignored, it added no security because both tokens are on the same PC.</remarks>
internal sealed record Secrets(string AccessToken, string? ProtectKey = null);

/// <summary>
/// Tokens encrypted with DPAPI (machine key) in secrets.dat. The file permissions protect against standard users, local
/// administrators can read the tokens.
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
            return secrets with { ProtectKey = string.IsNullOrWhiteSpace(secrets.ProtectKey) ? null : secrets.ProtectKey };
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
