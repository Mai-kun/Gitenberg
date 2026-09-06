using Gitenberg.Web.Database;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Export;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/export")
                       .WithTags("Export")
                       .RequireTelegramAuth();

        group.MapGet("/archive", DownloadArchive)
             .WithName("ExportArchive")
             .WithSummary("Download a ZIP archive of the entire notes repository");
    }

    public static async Task<IResult> DownloadArchive(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        [FromQuery(Name = "reference")] string? reference,
        AppDbContext dbContext,
        IGitHubService gitHubService,
        ITokenEncryptionService encryptionService,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        if (string.IsNullOrWhiteSpace(user.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is not configured for this user." });
        }

        var context = new GitHubRepositoryContext(
            encryptionService.DecryptToken(user.GitHubToken), user.RepositoryOwner, user.RepositoryName);

        var archive = await gitHubService.GetRepositoryArchiveAsync(context, reference);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        var fileName = $"{user.RepositoryName}-{stamp}.zip";

        return Results.File(archive, "application/zip", fileName);
    }
}
