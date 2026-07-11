using System.Security.Cryptography;
using System.Text;

namespace OpenWire.Service.Storage;

/// <summary>DPAPI protection for secrets embedded in the persisted settings document.</summary>
public static class SettingsSecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("OpenWire.Settings.VirusTotalApiKey.v1"));

    public static bool IsProtected(string value)
        => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Unprotect(string? persisted)
    {
        if (string.IsNullOrEmpty(persisted)) return string.Empty;
        if (!IsProtected(persisted)) return persisted;

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(persisted[Prefix.Length..]);
            byte[] plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
