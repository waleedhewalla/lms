using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EduNexus.Api;
using EduNexus.Api.AI;
using EduNexus.Api.Auth;
using EduNexus.Api.Endpoints;
using EduNexus.Api.Events;
using EduNexus.Api.Integrations;
using EduNexus.Api.Storage;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Minio;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// R1 Foundation: OpenAPI + EF Core/Postgres (RLS) for all foundation entities.
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<FoundationStore>(); // stateless SoD helper; persistence is EF
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks();

var conn = Environment.GetEnvironmentVariable("EDUNEXUS_CONNECTION")
    ?? builder.Configuration.GetConnectionString("Foundation")
    ?? "Host=localhost;Port=5433;Database=edunexus;Username=edunexus;Password=edunexus";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// --- Auth: OIDC when Authority configured, else dev HS256 (R1 slice) ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
var authCfg = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton<DevTokenService>();
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.AddHostedService<EventRelay>();
builder.Services.AddHostedService<NotificationConsumer>();
builder.Services.AddHostedService<SlaMonitor>();

// --- S3-compatible object storage (MinIO reference) ---
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    return new MinioClient().WithEndpoint(o.Endpoint).WithCredentials(o.AccessKey, o.SecretKey).WithSSL(o.Secure).Build();
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    var c = sp.GetRequiredService<IMinioClient>();
    return new ObjectStorage(c, o);
});

// --- AI (R5): tenant retrieval index + copilot + webhook fan-out ---
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddHttpClient("ai").SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("integrations").SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai");
    return new TenantSearchIndex(http, o);
});
builder.Services.AddHostedService<IntegrationDispatcher>();

// --- Observability: traces (OTLP, collector optional) + Prometheus metrics on /metrics ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("edunexus-api", serviceVersion: "1.0.0-R1"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        if (!string.IsNullOrWhiteSpace(authCfg.Authority))
        {
            o.Authority = authCfg.Authority;
            o.Audience = authCfg.Audience;
            o.RequireHttpsMetadata = builder.Environment.IsProduction();
        }
        else
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "edunexus-dev",
                ValidateAudience = true,
                ValidAudience = authCfg.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authCfg.DevSigningKey)),
            };
        }
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// 0.5 security gate: never boot Production on dev auth defaults.
if (app.Environment.IsProduction())
{
    var ao = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
    if (string.IsNullOrWhiteSpace(ao.Authority))
        throw new InvalidOperationException("Production requires Auth:Authority (OIDC). Dev HS256 is Development-only.");
    if (ao.EnableDevToken)
        throw new InvalidOperationException("Production requires Auth:EnableDevToken=false.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();

app.MapGet("/", () => Results.Ok(new { name = "EduNexus OS V2 API", release = "R1-Foundation", docs = "/openapi/v1.json" }));

// Map Endpoint Modules (1-F Program.cs Vertical Slice Extraction)
app.MapFoundationEndpoints();
app.MapCorrespondenceEndpoints();
app.MapGovernanceEndpoints();
app.MapDocumentEndpoints();
app.MapIntelligenceEndpoints();
app.MapReadEndpoints();

if (args.Contains("--migrate-only"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("EF Core database migrations applied successfully.");
    return;
}

app.Run();
