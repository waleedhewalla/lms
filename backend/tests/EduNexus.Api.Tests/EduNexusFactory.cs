using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EduNexus.Api.Tests;

/// <summary>
/// Boots the real API against a dedicated Postgres test database (edunexus_test).
/// Requires the edunexus-postgres container on localhost:5433.
/// </summary>
public sealed class EduNexusFactory : WebApplicationFactory<Program>
{
    public const string TestConnection =
        "Host=localhost;Port=5433;Database=edunexus_test;Username=edunexus;Password=edunexus";

    public EduNexusFactory()
    {
        Environment.SetEnvironmentVariable("EDUNEXUS_CONNECTION", TestConnection);
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
