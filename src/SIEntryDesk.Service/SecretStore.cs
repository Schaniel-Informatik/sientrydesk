using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
        EnsureProtectedDirectory();
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

    /// <summary>
    /// Defense in depth (review 2026-10-23): install.ps1 restricts the data folder before credentials are stored. If
    /// set-secrets ever runs first, the folder would inherit the permissions of ProgramData, where standard users may
    /// read, and the machine key of DPAPI would let them decrypt secrets.dat. So a folder that still inherits gets the
    /// same protected permissions as from install.ps1: SYSTEM and administrators full control, the service read.
    /// </summary>
    private static void EnsureProtectedDirectory()
    {
        var folder = new DirectoryInfo(ServicePaths.DataDirectory);
        if (folder.Exists && folder.GetAccessControl().AreAccessRulesProtected)
            return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        try
        {
            var service = new NTAccount("NT SERVICE", ServicePaths.ServiceName).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new FileSystemAccessRule(service,
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        catch (IdentityNotMappedException)
        {
            // Service not installed yet: install.ps1 grants its access later.
        }
        if (folder.Exists)
            folder.SetAccessControl(security);
        else
            folder.Create(security);
    }
}
