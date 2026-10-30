using EduNexus.Api.Hosting;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EduNexus.Api;
using EduNexus.Api.AI;
using EduNexus.Api.Auth;
using EduNexus.Api.Connectors;
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
// CORS: explicit origins from Cors:AllowedOrigins (comma-separated or array). With none configured,
// Development allows any origin; every other environment allows no cross-origin calls (same-origin only).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? (builder.Configuration["Cors:AllowedOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
    else if (builder.Environment.IsDevelopment()) p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
}));
// Trusted-proxy forwarded headers, per-caller rate limiting, internal-only /metrics (Hosting/EdgeProtection.cs).
builder.Services.AddEdgeProtection(builder.Configuration);
builder.Services.AddHealthChecks();

// In-cluster TLS: with Tls:CaFile set, every service connection below is verified against the site's own CA.
var privateCa = EduNexus.Api.Tls.PrivateCa.From(builder.Configuration);
if (privateCa is not null) builder.Services.AddSingleton(privateCa);

var conn = Environment.GetEnvironmentVariable("EDUNEXUS_CONNECTION")
    ?? builder.Configuration.GetConnectionString("Foundation")
    ?? "Host=localhost;Port=5433;Database=edunexus;Username=edunexus;Password=edunexus";
if (privateCa is not null) conn = privateCa.ApplyTo(conn);
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
    var minio = new MinioClient().WithEndpoint(o.Endpoint).WithCredentials(o.AccessKey, o.SecretKey).WithSSL(o.Secure);
    // The SDK would otherwise check MinIO's certificate against the system roots, which do not include the site CA.
    if (o.Secure && sp.GetService<EduNexus.Api.Tls.PrivateCa>() is { } ca) minio = minio.WithHttpClient(new HttpClient(ca.CreateHandler()));
    return minio.Build();
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    var c = sp.GetRequiredService<IMinioClient>();
    // Independent signed requests prove what the SDK claims (see ObjectStorage). Pooled connections are recycled so a
    // moved service address is picked up.
    var ca = o.Secure ? sp.GetService<EduNexus.Api.Tls.PrivateCa>() : null;
    var probe = new HttpClient(ca?.CreateHandler() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(15) };
    return new ObjectStorage(c, o, probe);
});
builder.Services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<ObjectStorage>());

// --- AI (R5): tenant retrieval index + copilot + webhook fan-out ---
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddHttpClient("ai").SetHandlerLifetime(TimeSpan.FromMinutes(5));
// OpenSearch has its own client: the site CA must apply to it and not to the (public) LLM endpoint behind "ai".
builder.Services.AddHttpClient("opensearch")
    .ConfigurePrimaryHttpMessageHandler(sp => (HttpMessageHandler?)sp.GetService<EduNexus.Api.Tls.PrivateCa>()?.CreateHandler() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    .ConfigureHttpClient((sp, c) =>
    {
        var ai = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
        if (!string.IsNullOrEmpty(ai.OpenSearchUser))
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{ai.OpenSearchUser}:{ai.OpenSearchPassword}")));
    });
var allowPrivateTargets = builder.Configuration.GetValue<bool>(EduNexus.Api.Integrations.OutboundUrlGuard.AllowPrivateKey);
builder.Services.AddHttpClient("integrations")
    .ConfigurePrimaryHttpMessageHandler(() => EduNexus.Api.Integrations.OutboundUrlGuard.CreateHandler(allowPrivateTargets))
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("opensearch");
    return new TenantSearchIndex(http, o);
});
builder.Services.AddSingleton(sp => new TextGenerator(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value,
    sp.GetRequiredService<ILogger<TextGenerator>>()));
builder.Services.AddHostedService<IntegrationDispatcher>();
// LMS / student-information connectors (off unless Connectors:Enabled): sealed credentials and a pluggable client per system.
builder.Services.AddSingleton<EduNexus.Api.Connectors.SecretBox>();
builder.Services.AddSingleton<EduNexus.Api.Connectors.ICatalogClients, EduNexus.Api.Connectors.CatalogClients>();

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

app.UseForwardedHeaders();
app.UseCors();
app.UseAuthentication();
app.UseEdgeProtection();
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
app.MapCorrespondenceTypeEndpoints();
app.MapCorrespondenceFlowEndpoints();
app.MapCorrespondenceEndpoints();
app.MapGovernanceEndpoints();
app.MapDocumentEndpoints();
app.MapIntelligenceEndpoints();
app.MapReadEndpoints();
app.MapWorkEndpoints();
app.MapReportEndpoints();
app.MapGovernanceDepthEndpoints();
app.MapCalendarFeedEndpoints();
app.MapConnectorEndpoints();
app.MapAiGovernanceEndpoints();
app.MapRecordsEndpoints();
app.MapPrivacyEndpoints();
app.MapAccreditationEndpoints();
app.MapBoardEndpoints();
app.MapPolicyAttestationEndpoints();

if (args.Contains("--migrate-only"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("EF Core database migrations applied successfully.");
    return;
}

app.Run();
