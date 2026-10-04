using System.Security.Cryptography;
using System.Text;

namespace EpsilonGCS.Services;

/// <summary>
/// Encrypts secrets (the Google API key) for the current Windows user with DPAPI, so settings.json never holds
/// them in plain text and they cannot be decrypted by another account or on another PC.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EpsilonGCS/maps/v1");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Plain secret, or "" when nothing is stored or it cannot be decrypted (other user / PC).</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
