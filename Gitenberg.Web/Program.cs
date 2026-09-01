using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Registration;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Infrastructure;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddMemoryCache();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"))
);

builder.Services.AddDataProtection();
builder.Services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();
builder.Services.AddScoped<IGitHubService, GitHubService>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var botConfig = builder.Configuration.GetSection(BotConfiguration.SectionName).Get<BotConfiguration>()
                ?? new BotConfiguration();
builder.Services.AddSingleton(botConfig);
builder.Services.AddHttpClient("tgwebhook")
       .AddTypedClient<ITelegramBotClient>((httpClient, sp) => new TelegramBotClient(botConfig.BotToken, httpClient));
builder.Services.AddScoped<UpdateHandler>();
builder.Services.AddHostedService<ConfigureWebhook>();

var searchConfig = builder.Configuration.GetSection(SearchConfiguration.SectionName).Get<SearchConfiguration>()
                ?? new SearchConfiguration();
builder.Services.AddSingleton(searchConfig);
builder.Services.AddScoped<NoteIndexer>();
builder.Services.AddHostedService<NoteIndexingService>();

var app = builder.Build();

app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.EnsureFtsTableCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/api");
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapNotesEndpoints();
app.MapRegistrationEndpoints();
app.MapBotEndpoints();
app.MapSearchEndpoints();

app.UseHttpsRedirection();
app.Run();