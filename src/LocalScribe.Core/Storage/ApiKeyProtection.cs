using System.Security.Cryptography;
using System.Text;

namespace LocalScribe.Core.Storage;

/// <summary>Remote-inference API keys at rest (design 2026-10-18). settings.json never holds a key
/// in plaintext: it holds a DPAPI blob scoped to the CURRENT Windows user, so the file is useless
/// if copied to another account or machine (a backup, a support paste-in, a synced folder). The
/// key is decrypted only at the moment a request is built, and then travels to the inference
/// helper over its private stdin pipe - never through an environment variable or a command line,
/// both of which other processes of the same user can read.
///
/// The entropy is not a secret; it only stops another application's DPAPI blob from being
/// mistaken for one of ours.</summary>
public static class ApiKeyProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalScribe.RemoteInference.ApiKey.v1");

    /// <summary>Null/blank in, null out - "no key" is stored as absence, never as an encrypted
    /// empty string.</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext.Trim()), Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Null when there is no key. Throws when a stored blob cannot be decrypted (a
    /// settings file carried over from another Windows account): the caller names the problem
    /// instead of silently sending an unauthenticated request.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "The saved API key could not be decrypted for this Windows user - enter it again in "
                + "Settings > Remote inference.", ex);
        }
    }
}
