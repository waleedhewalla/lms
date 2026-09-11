using System.Security.Cryptography;
using System.Text;

namespace EduNexus.Api.Integrations;

/// <summary>HMAC-SHA256 request signing for outbound webhooks (X-EduNexus-Signature = hex digest of body).</summary>
public static class WebhookSigner
{
    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
