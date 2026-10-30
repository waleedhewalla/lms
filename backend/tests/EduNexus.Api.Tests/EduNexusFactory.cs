using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using EduNexus.Api.Connectors;
using EduNexus.Api.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EduNexus.Api.Tests;

/// <summary>
/// Boots the real API against a dedicated Postgres test database (edunexus_test).
/// Requires the edunexus-postgres container on localhost:5433.
/// </summary>
public sealed class EduNexusFactory : WebApplicationFactory<Program>
{
    public const string TestConnection =
        "Host=localhost;Port=5433;Database=edunexus_test;Username=edunexus;Password=edunexus";

    /// <summary>The storage every test talks to: an in-memory fake, so no test depends on a MinIO server.</summary>
    public FakeObjectStorage Storage { get; } = new();

    /// <summary>The other systems every connector test talks to: scripted, so no test needs a Moodle or a Canvas.</summary>
    public FakeCatalogClients Catalogs { get; } = new();

    /// <summary>A fixed key for sealing connector credentials in tests (32 bytes, base64).</summary>
    public const string TestSecretKey = "dGVzdC1rZXktdGVzdC1rZXktdGVzdC1rZXktMTIzNDU=";

    public EduNexusFactory()
    {
        Environment.SetEnvironmentVariable("EDUNEXUS_CONNECTION", TestConnection);
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IObjectStorage>();
            s.AddSingleton<IObjectStorage>(Storage);
            s.RemoveAll<ICatalogClients>();
            s.AddSingleton<ICatalogClients>(Catalogs);
        });
        builder.UseSetting("Connectors:Enabled", "true");
        builder.UseSetting("Connectors:SecretKey", TestSecretKey);
        // The suite fires bursts of calls as one test identity; dedicated tests cover the limiter with low limits.
        builder.UseSetting("RateLimiting:PermitsPerMinute", "100000");
        builder.UseSetting("RateLimiting:ExpensivePermitsPerMinute", "100000");
    }
}
