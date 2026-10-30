using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EduNexus.Infrastructure;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        var cs = Environment.GetEnvironmentVariable("EDUNEXUS_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=edunexus;Username=edunexus;Password=edunexus";
        // Same switch as the API (Tls__CaFile): design-time migrations talk to Postgres over verified TLS too.
        var ca = Environment.GetEnvironmentVariable("Tls__CaFile");
        if (!string.IsNullOrWhiteSpace(ca))
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
            if (!cs.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase) && !cs.Contains("SslMode", StringComparison.OrdinalIgnoreCase)) b.SslMode = Npgsql.SslMode.VerifyFull;
            if (string.IsNullOrEmpty(b.RootCertificate)) b.RootCertificate = ca;
            cs = b.ConnectionString;
        }
        options.UseNpgsql(cs);
        return new AppDbContext(options.Options);
    }
}
