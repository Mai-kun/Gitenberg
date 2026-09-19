using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Activity;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Features.Export;
using Gitenberg.Web.Features.Graph;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Pins;
using Gitenberg.Web.Features.Repositories;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.Shares;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.Templates;
using Gitenberg.Web.Features.Tasks;
using Gitenberg.Web.Infrastructure;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);


Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .CreateBootstrapLogger();

builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

builder.Services.AddMemoryCache();
builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"))
);

var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? "temp-keys";
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("GitenbergApp");
builder.Services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();
builder.Services.AddScoped<IGitHubService, GitHubService>();
builder.Services.AddSingleton<IGitHubIdentityService, GitHubIdentityService>();
builder.Services.AddScoped<IRepositoryContextResolver, RepositoryContextResolver>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var webAuthConfig = builder.Configuration
                          .GetSection(WebAuthConfiguration.SectionName)
                          .Get<WebAuthConfiguration>()
                  ?? new WebAuthConfiguration();
builder.Services.AddSingleton(webAuthConfig);
builder.Services.AddScoped<WebAuthService>();

var searchConfig = builder.Configuration
                           .GetSection(SearchConfiguration.SectionName)
                           .Get<SearchConfiguration>()
                   ?? new SearchConfiguration();
builder.Services.AddSingleton(searchConfig);
builder.Services.AddScoped<NoteIndexer>();
builder.Services.AddHostedService<NoteIndexingService>();

var syncConfig = builder.Configuration
                        .GetSection(SyncConfiguration.SectionName)
                        .Get<SyncConfiguration>()
                ?? new SyncConfiguration();
builder.Services.AddSingleton(syncConfig);
builder.Services.AddScoped<PendingSyncService>();
builder.Services.AddHostedService<SyncFlushService>();

var shareConfig = builder.Configuration
                         .GetSection(ShareConfiguration.SectionName)
                         .Get<ShareConfiguration>()
                 ?? new ShareConfiguration();
builder.Services.AddSingleton(shareConfig);
builder.Services.AddScoped<ShareLinksService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<PinsService>();

// Public share endpoints are guarded only by the unguessable token, so the
// rate limiter blunts brute-force sweeps over /api/share/{token}; the auth
// policy does the same for repeated sign-in attempts.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("share", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));
});

// Configure forwarded headers for reverse proxy support
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Trust loopback addresses for development
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
app.UseExceptionHandler();
// API responses are authenticated by a cookie and carry per-user data, so
// they must never land in the browser HTTP cache (a cached /api/notes from
// one account could otherwise be shown to the next visitor on the same host).
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    return next();
});
// Redirection and request logging must precede the endpoints and static
// files: registered later they never run, because matched endpoints and
// UseStaticFiles short-circuit the pipeline.
app.UseHttpsRedirection();
app.UseSerilogRequestLogging();
app.UseRateLimiter();

// Security headers to protect against common web vulnerabilities
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    // Content-Security-Policy will be added based on environment
    if (app.Environment.IsDevelopment())
    {
        // More permissive in development for easier testing
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline';";
    }
    else
    {
        // Strict CSP in production
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self';";
    }
    await next();
});

// Support for reverse proxy scenarios
app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.EnsureFtsTableCreated();
    db.EnsureUserCaptureColumnsCreated();
    PendingSyncService.EnsureTableCreated(db);
    ActivityService.EnsureTableCreated(db);
    PinsService.EnsureTableCreated(db);
    ShareLinksService.EnsureTableCreated(db);
    // Requires the FTS and PendingNoteOps tables to exist already.
    db.EnsureRepositoriesTableCreated();
    WebAuthService.EnsureTableCreated(db);
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Keep dev iterations honest: never serve stale JS/CSS from the browser cache.
    OnPrepareResponse = ctx =>
    {
        if (app.Environment.IsDevelopment())
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});

app.MapAuthEndpoints();
app.MapNotesEndpoints();
app.MapHistoryEndpoints();
app.MapTemplatesEndpoints();
app.MapRepositoriesEndpoints();
app.MapSearchEndpoints();
app.MapSyncEndpoints();
app.MapExportEndpoints();
app.MapActivityEndpoints();
app.MapTasksEndpoints();
app.MapGraphEndpoints();
app.MapPinsEndpoints();
app.MapShareEndpoints();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
