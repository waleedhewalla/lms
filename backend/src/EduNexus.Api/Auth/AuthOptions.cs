namespace EduNexus.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>OIDC authority (e.g. https://idp.example.com/realms/edunexus). Empty = dev symmetric-key mode.</summary>
    public string? Authority { get; set; }
    public string Audience { get; set; } = "edunexus-api";
    /// <summary>HS256 key for dev token minting/validation. Never use the default in production.</summary>
    public string DevSigningKey { get; set; } = "dev-only-key-change-me-min-32-chars-long!!";
    /// <summary>Exposes POST /api/auth/dev-token. Forced off outside Development.</summary>
    public bool EnableDevToken { get; set; } = true;
}
