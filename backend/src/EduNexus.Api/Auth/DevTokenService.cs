using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EduNexus.Api.Auth;

/// <summary>Dev-only token minter (HS256). Production uses an external OIDC IdP carrying the same claims.</summary>
public sealed class DevTokenService(IOptions<AuthOptions> options)
{
    public const string PermissionClaim = "permission";
    public const string TenantClaim = "tenant_id";

    public string Mint(string subject, Guid tenantId, IEnumerable<string> permissions, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.DevSigningKey));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(TenantClaim, tenantId.ToString()),
        };
        claims.AddRange(permissions.Distinct().Select(p => new Claim(PermissionClaim, p)));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "edunexus-dev",
            audience: options.Value.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(8)),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.Identity?.IsAuthenticated == true &&
        user.FindAll(DevTokenService.PermissionClaim).Any(c => c.Value == permission);

    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(DevTokenService.TenantClaim), out var id) ? id : null;
}
