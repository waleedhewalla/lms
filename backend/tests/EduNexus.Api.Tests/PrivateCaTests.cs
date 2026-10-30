using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduNexus.Api.Events;
using EduNexus.Api.Tls;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EduNexus.Api.Tests;

/// <summary>A throwaway CA and server certificates, a real TLS listener, and the real client code paths.</summary>
public sealed class PrivateCaTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("edunexus-tls").FullName;
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { /* temp files; the OS cleans up */ } }

    private static (X509Certificate2 Ca, string Pem) NewCa(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (ca, ca.ExportCertificatePem());
    }

    private static X509Certificate2 NewServer(X509Certificate2 ca, string dnsName)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), serial);
        // Re-import with the private key so SslStream can use it (an ephemeral key cannot be used directly on all platforms).
        return X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    private PrivateCa CaFile(string pem, string file = "ca.crt")
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, pem);
        return new PrivateCa(path);
    }

    /// <summary>Accepts connections forever, completes the TLS handshake with <paramref name="server"/> and replies "ok".</summary>
    private static (TcpListener Listener, int Port) Serve(X509Certificate2 server)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var ssl = new SslStream(client.GetStream());
                            await ssl.AuthenticateAsServerAsync(server);
                            var buf = new byte[4096];
                            _ = await ssl.ReadAsync(buf);
                            await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                            await ssl.FlushAsync();
                        }
                        catch (Exception) { /* a client that rejected us: expected in the negative tests */ }
                    }
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static async Task<string> Get(HttpMessageHandler handler, string host, int port)
    {
        using var http = new HttpClient(handler);
        // "localhost" resolves to the loopback listener; the certificate must name exactly this host.
        return await http.GetStringAsync($"https://{host}:{port}/");
    }

    [Fact]
    public async Task Accepts_A_Server_Signed_By_The_Site_Ca()
    {
        var (ca, pem) = NewCa("EduNexus test CA");
        var (listener, port) = Serve(NewServer(ca, "localhost"));
        try { Assert.Equal("ok", await Get(CaFile(pem).CreateHandler(), "localhost", port)); }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Rejects_A_Server_Signed_By_Another_Ca()
    {
        var (ca, _) = NewCa("EduNexus test CA");
        var (_, otherPem) = NewCa("Some other CA");
        var (listener, port) = Serve(NewServer(ca, "localhost"));
        try { await Assert.ThrowsAsync<HttpRequestException>(() => Get(CaFile(otherPem).CreateHandler(), "localhost", port)); }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Rejects_A_Certificate_That_Names_A_Different_Host()
    {
        var (ca, pem) = NewCa("EduNexus test CA");
        var (listener, port) = Serve(NewServer(ca, "edunexus-minio"));   // right CA, wrong name for "localhost"
        try { await Assert.ThrowsAsync<HttpRequestException>(() => Get(CaFile(pem).CreateHandler(), "localhost", port)); }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Rejects_A_Self_Signed_Certificate_Even_If_Valid_For_The_Name()
    {
        var (_, pem) = NewCa("EduNexus test CA");
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var server = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
        var (listener, port) = Serve(server);
        try { await Assert.ThrowsAsync<HttpRequestException>(() => Get(CaFile(pem).CreateHandler(), "localhost", port)); }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Without_A_Configured_File_There_Is_No_Private_Ca_And_A_Bad_File_Fails_Loudly()
    {
        Assert.Null(PrivateCa.From(new ConfigurationBuilder().Build()));
        var empty = Path.Combine(dir, "empty.crt");
        File.WriteAllText(empty, "not a certificate");
        Assert.ThrowsAny<Exception>(() => new PrivateCa(empty));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PrivateCa.ConfigKey] = Path.Combine(dir, "missing.crt") }).Build();
        Assert.ThrowsAny<Exception>(() => PrivateCa.From(cfg));
    }

    [Fact]
    public void Postgres_Connection_String_Gets_Full_Verification_Unless_The_Operator_Chose()
    {
        var (_, pem) = NewCa("EduNexus test CA");
        var ca = CaFile(pem);
        var applied = ca.ApplyTo("Host=edunexus-postgres;Database=edunexus;Username=u;Password=p");
        Assert.Contains("SSL Mode=VerifyFull", applied);
        Assert.Contains(ca.CaFile, applied);
        // An explicit mode and root certificate in the string are left alone.
        var explicitCs = ca.ApplyTo("Host=h;Username=u;Password=p;SSL Mode=Require;Root Certificate=/etc/other.crt");
        Assert.Contains("SSL Mode=Require", explicitCs);
        Assert.Contains("/etc/other.crt", explicitCs);
    }

    [Fact]
    public void RabbitMq_Factory_Uses_Tls_With_The_Site_Ca_Only_When_Asked()
    {
        var (_, pem) = NewCa("EduNexus test CA");
        var ca = CaFile(pem);
        var plain = RabbitMqConnection.Factory(new RabbitMqOptions { Host = "edunexus-rabbitmq" }, ca);
        Assert.False(plain.Ssl.Enabled);

        var secure = RabbitMqConnection.Factory(new RabbitMqOptions { Host = "edunexus-rabbitmq", Port = 5671, Tls = true }, ca);
        Assert.True(secure.Ssl.Enabled);
        Assert.Equal("edunexus-rabbitmq", secure.Ssl.ServerName);
        Assert.NotNull(secure.Ssl.CertificateValidationCallback);

        var named = RabbitMqConnection.Factory(new RabbitMqOptions { Host = "10.0.0.5", Tls = true, TlsServerName = "edunexus-rabbitmq" }, null);
        Assert.Equal("edunexus-rabbitmq", named.Ssl.ServerName);
    }
}
