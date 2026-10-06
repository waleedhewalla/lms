using System.Net;
using System.Net.Sockets;

namespace EduNexus.Api.Integrations;

/// <summary>
/// SSRF guard for tenant-supplied webhook targets. Rejects loopback, private, link-local (cloud metadata),
/// carrier-grade NAT, multicast and unspecified addresses both when an endpoint is registered and again
/// at connect time (so DNS rebinding cannot slip an internal address in). Sites that deliberately post
/// to internal systems set Integrations:AllowPrivateTargets=true.
/// </summary>
public static class OutboundUrlGuard
{
    public const string AllowPrivateKey = "Integrations:AllowPrivateTargets";

    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10                                   // 10.0.0.0/8
            || b[0] == 0                                    // 0.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)                 // link-local / cloud metadata
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 CGNAT
            || b[0] >= 224;                                 // multicast + reserved
    }

    /// <summary>Registration-time check: scheme, no credentials, and no literal internal host.</summary>
    public static string? Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "TargetUrl must be a valid HTTP/HTTPS URL.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "TargetUrl must not contain credentials.";
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsBlocked(ip)))
            return "TargetUrl must not point to a loopback, private or link-local address.";
        return null;
    }

    /// <summary>Handler for the "integrations" HttpClient: no redirects, and every connection's resolved IP is checked.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(a => allowPrivate || !IsBlocked(a))
                ?? throw new HttpRequestException($"Blocked outbound connection to internal address for host '{ctx.DnsEndPoint.Host}'.");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
