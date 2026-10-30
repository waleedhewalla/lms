using System.Security.Cryptography;
using System.Text;

namespace EduNexus.Api.Connectors;

/// <summary>
/// Seals the credentials the API holds for other systems (an LMS token, a password) so a database copy alone does not
/// reveal them. AES-256-GCM with a key the operator supplies (<c>Connectors:SecretKey</c>, 32 random bytes as base64):
/// the key is in the environment, never in the database, and is the same on every API replica. The context (tenant and
/// connector) is authenticated too, so a sealed value cannot be moved to another connector or tenant.
/// </summary>
public sealed class SecretBox(IConfiguration config)
{
    public const string KeyConfig = "Connectors:SecretKey";
    private const string Prefix = "v1.";

    private byte[]? Key()
    {
        var raw = config[KeyConfig];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { var key = Convert.FromBase64String(raw.Trim()); return key.Length == 32 ? key : null; }
        catch (FormatException) { return null; }
    }

    public bool IsConfigured => Key() is not null;

    public string Seal(string plain, string context)
    {
        var key = Key() ?? throw new InvalidOperationException($"{KeyConfig} is not set to 32 bytes of base64.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plain value, or throws <see cref="CryptographicException"/> when the key or the context is wrong or the value was changed.</summary>
    public string Open(string sealedValue, string context)
    {
        var key = Key() ?? throw new InvalidOperationException($"{KeyConfig} is not set to 32 bytes of base64.");
        if (!sealedValue.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unknown sealed format.");
        var all = Convert.FromBase64String(sealedValue[Prefix.Length..]);
        if (all.Length < 28) throw new CryptographicException("Sealed value is too short.");
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }
}
