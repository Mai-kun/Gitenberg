using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Pins;

/// <summary>
/// Pinned notes and folders of the active repository. Pins are per-user UI
/// metadata stored in SQLite (not committed to GitHub), scoped per repository.
/// </summary>
public static class PinsEndpoints
{
    public static void MapPinsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pins")
                       .WithTags("Pins")
                       .RequireTelegramAuth();

        group.MapGet("/", ListPins)
             .WithName("ListPins")
             .WithSummary("Pinned notes and folders of the active repository");

        group.MapPut("/", PinItem)
             .WithName("PinItem")
             .WithSummary("Pin a note or folder");

        group.MapDelete("/", UnpinItem)
             .WithName("UnpinItem")
             .WithSummary("Unpin a note or folder");
    }

    public static async Task<IResult> ListPins(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
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

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var pins = await pinsService.ListAsync(telegramId.Value, repository.RepositoryId);
        return Results.Ok(pins.Select(p => (object)new { Path = p.ItemPath, PinnedAt = p.PinnedAt }).ToList());
    }

    public static async Task<IResult> PinItem(
        [FromBody] PinItemRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "'Path' is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        await pinsService.PinAsync(telegramId.Value, repository.RepositoryId, request.Path);

        return Results.Ok(new { Message = $"'{request.Path.Trim('/')}' pinned." });
    }

    public static async Task<IResult> UnpinItem(
        [FromQuery] string path,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        if (string.IsNullOrWhiteSpace(path) || path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        await pinsService.UnpinAsync(telegramId.Value, repository.RepositoryId, path);

        return Results.Ok(new { Message = $"'{path.Trim('/')}' unpinned." });
    }
}
