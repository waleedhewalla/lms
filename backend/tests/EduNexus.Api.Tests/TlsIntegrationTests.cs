using EduNexus.Api.Hosting;
using EduNexus.Api.Tls;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace EduNexus.Api.Tests;

/// <summary>
/// The real clients against real servers that speak TLS with a private CA. They run in the `tls-integration` CI job
/// (which starts the servers and sets the variables below) and locally when the variables are set; elsewhere they are
/// no-ops. EDUNEXUS_REQUIRE_TLS_TESTS=true (set only by that job) turns "not configured" into a failure.
///   EDUNEXUS_TEST_TLS_CA     path of the CA certificate (PEM)
///   EDUNEXUS_TEST_TLS_PG     Npgsql connection string of a Postgres that only accepts TLS
///   EDUNEXUS_TEST_TLS_REDIS  host:port of a Redis that only has a TLS port
/// </summary>
[Trait("Category", "Tls")]
public sealed class TlsIntegrationTests
{
    private static string? Setting(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v) && Environment.GetEnvironmentVariable("EDUNEXUS_REQUIRE_TLS_TESTS") == "true")
            Assert.Fail($"{name} must be set in the tls-integration job so in-cluster TLS is really tested.");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>Another CA's certificate file, so "wrong CA" is a real certificate that simply is not the server's issuer.</summary>
    private static string WrongCaFile()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=not-the-site-ca", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"wrong-ca-{Guid.NewGuid():N}.crt");
        File.WriteAllText(path, cert.ExportCertificatePem());
        return path;
    }

    [Fact]
    public async Task Postgres_Is_Reached_Over_Verified_Tls_And_Refuses_Plaintext_And_The_Wrong_Ca()
    {
        if (Setting("EDUNEXUS_TEST_TLS_CA") is not { } caFile || Setting("EDUNEXUS_TEST_TLS_PG") is not { } pg) return;

        await using (var ok = new NpgsqlConnection(new PrivateCa(caFile).ApplyTo(pg)))
        {
            await ok.OpenAsync();
            await using var cmd = new NpgsqlCommand("select ssl from pg_stat_ssl where pid = pg_backend_pid()", ok);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!);   // ExecuteScalar returns the single boolean column
        }

        var wrong = WrongCaFile();
        try
        {
            await using var bad = new NpgsqlConnection(new PrivateCa(wrong).ApplyTo(pg));
            await Assert.ThrowsAnyAsync<Exception>(() => bad.OpenAsync());
        }
        finally { File.Delete(wrong); }

        // The server only accepts TLS (hostssl rules), so a connection that asks for none must be refused.
        var plain = new NpgsqlConnectionStringBuilder(pg) { SslMode = SslMode.Disable }.ConnectionString;
        await using var clear = new NpgsqlConnection(plain);
        await Assert.ThrowsAnyAsync<Exception>(() => clear.OpenAsync());
    }

    [Fact]
    public async Task Redis_Is_Reached_Over_Verified_Tls_And_The_Rate_Limiter_Works_On_It()
    {
        if (Setting("EDUNEXUS_TEST_TLS_CA") is not { } caFile || Setting("EDUNEXUS_TEST_TLS_REDIS") is not { } address) return;

        var ca = new PrivateCa(caFile);
        await using (var redis = await ConnectionMultiplexer.ConnectAsync(EdgeProtection.RedisOptions($"{address},ssl=true,connectTimeout=3000", ca)))
        {
            Assert.True(redis.IsConnected);
            var store = new RedisRateLimitStore(redis, TimeProvider.System, $"test:{Guid.NewGuid():N}");
            Assert.True((await store.HitAsync("k", 1, default)).Allowed);
            Assert.False((await store.HitAsync("k", 1, default)).Allowed);
        }

        // Same server, but the client trusts a different CA: the handshake must fail, not fall back to trusting it.
        var wrong = WrongCaFile();
        try
        {
            var opts = EdgeProtection.RedisOptions($"{address},ssl=true,connectTimeout=3000", new PrivateCa(wrong));
            opts.AbortOnConnectFail = true;
            await Assert.ThrowsAnyAsync<RedisConnectionException>(() => ConnectionMultiplexer.ConnectAsync(opts));
        }
        finally { File.Delete(wrong); }

        // A client that does not use TLS cannot talk to a TLS-only port.
        var clear = EdgeProtection.RedisOptions($"{address},connectTimeout=2000", null);
        clear.AbortOnConnectFail = true;
        await Assert.ThrowsAnyAsync<RedisConnectionException>(() => ConnectionMultiplexer.ConnectAsync(clear));
    }
}
