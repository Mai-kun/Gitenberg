using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace Gitenberg.Web.Features.Notes;

public static class NotesEndpoints
{
    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Notes");

        group.MapGet("/", GetNotes)
             .WithName("GetNotes")
             .WithSummary("List all notes (files) in the repository");

        group.MapGet("/content", GetNoteContent)
             .WithName("GetNoteContent")
             .WithSummary("Get the text content of a note");

        group.MapPost("/", CreateOrUpdateNote)
             .WithName("CreateOrUpdateNote")
             .WithSummary("Create or update a note");

        group.MapDelete("/", DeleteNote)
             .WithName("DeleteNote")
             .WithSummary("Delete a note");
    }

    public static async Task<IResult> GetNotes(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService
    )
    {
        var telegramId = headerTelegramId ?? queryTelegramId;
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
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

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        var contents = await gitHubService.GetNotesAsync(context);
        var notes = contents.Select(c => new
            {
                c.Name,
                c.Path,
                c.Sha,
                c.Size,
                Type = c.Type.ToString(),
                c.DownloadUrl,
                c.HtmlUrl,
            }
        ).ToList();

        return Results.Ok(notes);
    }

    public static async Task<IResult> GetNoteContent(
        [FromQuery] string path,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService
    )
    {
        var telegramId = headerTelegramId ?? queryTelegramId;
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
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

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        var content = await gitHubService.GetNoteContentAsync(context, path);
        return Results.Ok(new { Path = path, Content = content });
    }

    public static async Task<IResult> CreateOrUpdateNote(
        [FromBody] CreateOrUpdateNoteRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService
    )
    {
        var telegramId = headerTelegramId ?? queryTelegramId;
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.BadRequest(new { Error = "Invalid request body or missing 'Path'." });
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

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        var commitMessage = string.IsNullOrWhiteSpace(request.CommitMessage)
            ? $"Update note: {request.Path}"
            : request.CommitMessage;

        await gitHubService.CreateOrUpdateNoteAsync(
            context,
            request.Path,
            request.Content ?? string.Empty,
            commitMessage
        );
        return Results.Ok(new { Message = $"Note at '{request.Path}' successfully created or updated." });
    }

    public static async Task<IResult> DeleteNote(
        [FromQuery] string path,
        [FromQuery] string? commitMessage,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService
    )
    {
        var telegramId = headerTelegramId ?? queryTelegramId;
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
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

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        var commit = string.IsNullOrWhiteSpace(commitMessage)
            ? $"Delete note: {path}"
            : commitMessage;

        await gitHubService.DeleteNoteAsync(context, path, commit);
        return Results.Ok(new { Message = $"Note at '{path}' successfully deleted." });
    }
}