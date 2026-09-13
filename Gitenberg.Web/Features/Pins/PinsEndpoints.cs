using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Pins;

public static class PinsEndpoints
{
    public static void MapPinsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pins")
                       .WithTags("Pins")
                       .RequireAuth();

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
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var pins = await pinsService.ListAsync(userId.Value, repository.RepositoryId);
        return Results.Ok(pins.Select(p => (object)new { Path = p.ItemPath, PinnedAt = p.PinnedAt }).ToList());
    }

    public static async Task<IResult> PinItem(
        [FromBody] PinItemRequest? request,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "'Path' is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        await pinsService.PinAsync(userId.Value, repository.RepositoryId, request.Path);

        return Results.Ok(new { Message = $"'{request.Path.Trim('/')}' pinned." });
    }

    public static async Task<IResult> UnpinItem(
        [FromQuery] string path,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PinsService pinsService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (string.IsNullOrWhiteSpace(path) || path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        await pinsService.UnpinAsync(userId.Value, repository.RepositoryId, path);

        return Results.Ok(new { Message = $"'{path.Trim('/')}' unpinned." });
    }
}
