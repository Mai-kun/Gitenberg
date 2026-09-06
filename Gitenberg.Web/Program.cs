using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Registration;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Infrastructure;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

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
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var botConfig = builder.Configuration
                        .GetSection(BotConfiguration.SectionName)
                        .Get<BotConfiguration>()
                ?? new BotConfiguration();
builder.Services.AddSingleton(botConfig);

if (!string.IsNullOrWhiteSpace(botConfig.BotToken))
{
    builder.Services.AddHttpClient("tgwebhook")
            .AddTypedClient<ITelegramBotClient>((httpClient, sp) => new TelegramBotClient(botConfig.BotToken, httpClient));
    builder.Services.AddHostedService<ConfigureWebhook>();
}
builder.Services.AddScoped<UpdateHandler>();
builder.Services.AddSingleton<ITelegramAuthValidator>(_ => new TelegramAuthValidator(botConfig.BotToken));

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

var app = builder.Build();
app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.EnsureFtsTableCreated();
    PendingSyncService.EnsureTableCreated(db);
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

app.MapNotesEndpoints();
app.MapRegistrationEndpoints();
app.MapBotEndpoints();
app.MapSearchEndpoints();
app.MapSyncEndpoints();

app.UseHttpsRedirection();
app.Run();