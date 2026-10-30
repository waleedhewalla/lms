using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace EduNexus.Api.Tls;

/// <summary>
/// Trust for the services inside the cluster (Postgres, RabbitMQ, MinIO, OpenSearch, Redis). They present certificates
/// from the site's own CA, which the operating system does not know, so connections are checked against that CA file
/// only, never against the system store and never with checking switched off. The host name is still checked.
/// Set <c>Tls:CaFile</c> (env <c>Tls__CaFile</c>) to turn this on; without it nothing changes.
/// </summary>
public sealed class PrivateCa
{
    public const string ConfigKey = "Tls:CaFile";

    private readonly X509Certificate2Collection roots;

    public PrivateCa(string caFile)
    {
        CaFile = caFile;
        roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caFile);
        if (roots.Count == 0) throw new InvalidOperationException($"{ConfigKey} '{caFile}' holds no certificate.");
    }

    public string CaFile { get; }

    /// <summary>The CA, or null when private-CA trust is not configured.</summary>
    public static PrivateCa? From(IConfiguration config)
    {
        var file = config[ConfigKey];
        return string.IsNullOrWhiteSpace(file) ? null : new PrivateCa(file);
    }

    /// <summary>Accepts a server certificate only if it chains to this CA and names the host being contacted.</summary>
    public bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string? expectedHost)
    {
        if (certificate is null) return false;
        // A name mismatch is never excused; chain errors are re-checked below against our CA instead of the system roots.
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        using var leaf = new X509Certificate2(certificate);
        using var own = new X509Chain();
        own.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        own.ChainPolicy.CustomTrustStore.AddRange(roots);
        own.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // in-cluster CA publishes no CRL/OCSP
        if (chain is not null)
            foreach (var element in chain.ChainElements.Skip(1))
                own.ChainPolicy.ExtraStore.Add(element.Certificate);
        return own.Build(leaf);
    }

    /// <summary>For <see cref="SslStream"/> and the clients that take a RemoteCertificateValidationCallback.</summary>
    public RemoteCertificateValidationCallback Callback(string? expectedHost = null) =>
        (_, cert, chain, errors) => Validate(cert, chain, errors, expectedHost);

    public SocketsHttpHandler CreateHandler(TimeSpan? connectionLifetime = null) => new()
    {
        PooledConnectionLifetime = connectionLifetime ?? TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Callback() },
    };

    /// <summary>Adds full certificate checking against this CA to a Postgres connection string.</summary>
    public string ApplyTo(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        // An explicit choice in the connection string wins: the operator may know better (e.g. a different CA file).
        if (!connectionString.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase) && !connectionString.Contains("SslMode", StringComparison.OrdinalIgnoreCase))
            b.SslMode = SslMode.VerifyFull;
        if (string.IsNullOrEmpty(b.RootCertificate)) b.RootCertificate = CaFile;
        return b.ConnectionString;
    }
}
