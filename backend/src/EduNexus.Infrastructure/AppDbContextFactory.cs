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
        options.UseNpgsql(cs);
        return new AppDbContext(options.Options);
    }
}
