using System.IO.Compression;
using System.Text;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;

namespace Gitenberg.Web.Features.TelegramBot;

public static class BotEndpoints
{
    public static void MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/bot/webhook", async (
            [FromBody] Update update,
            [FromHeader(Name = "X-Telegram-Bot-Api-Secret-Token")] string? secretToken,
            UpdateHandler updateHandler,
            BotConfiguration botConfig,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(botConfig.SecretToken) || botConfig.SecretToken != secretToken)
            {
                return Results.Unauthorized();
            }

            await updateHandler.HandleUpdateAsync(update, cancellationToken);
            return Results.Ok();
        })
        .WithName("TelegramWebhook")
        .WithSummary("Handles incoming Telegram bot updates")
        .WithDescription("Accepts webhook updates from Telegram Bot API and processes start command");

        app.MapGet(InlineFileLinkService.PathSegment, async (
            long uid,
            string path,
            long exp,
            string? sig,
            string? format,
            AppDbContext dbContext,
            IGitHubService gitHubService,
            ITokenEncryptionService encryptionService,
            InlineFileLinkService fileLinks,
            CancellationToken cancellationToken) =>
        {
            // Only "zip" and the default "md" are valid; anything else is
            // validated as markdown, so a tampered format cannot widen access.
            var fileFormat = format == InlineFileLinkService.ZipFormat
                ? InlineFileLinkService.ZipFormat
                : InlineFileLinkService.MarkdownFormat;

            if (!fileLinks.TryValidate(uid, path, exp, fileFormat, sig))
            {
                return Results.Unauthorized();
            }

            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == uid, cancellationToken);
            if (user == null || string.IsNullOrWhiteSpace(user.GitHubToken))
            {
                return Results.NotFound();
            }

            var context = new GitHubRepositoryContext(
                encryptionService.DecryptToken(user.GitHubToken),
                user.RepositoryOwner,
                user.RepositoryName
            );
            var content = await gitHubService.GetNoteContentAsync(context, path);

            if (fileFormat == InlineFileLinkService.ZipFormat)
            {
                var zipName = Path.GetFileNameWithoutExtension(path) + ".zip";
                return Results.File(
                    CreateNoteZip(path, content),
                    "application/zip",
                    zipName
                );
            }

            return Results.File(
                Encoding.UTF8.GetBytes(content),
                "text/markdown; charset=utf-8",
                Path.GetFileName(path)
            );
        })
        .WithName("InlineNoteFile")
        .WithSummary("Serves a note file (raw or zipped) through a signed short-lived link used by inline search results")
        .ExcludeFromDescription();
    }

    private static byte[] CreateNoteZip(string notePath, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(Path.GetFileName(notePath));
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return stream.ToArray();
    }
}
