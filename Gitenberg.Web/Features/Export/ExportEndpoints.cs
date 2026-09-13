using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Auth;
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
                       .RequireAuth();

        group.MapGet("/archive", DownloadArchive)
             .WithName("ExportArchive")
             .WithSummary("Download a ZIP archive of the entire notes repository");
    }

    public static async Task<IResult> DownloadArchive(
        [FromQuery(Name = "reference")] string? reference,
        AppDbContext dbContext,
        IGitHubService gitHubService,
        IRepositoryContextResolver repositoryResolver,
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

        var archive = await gitHubService.GetRepositoryArchiveAsync(repository.Context, reference);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        var fileName = $"{repository.Repository.RepositoryName}-{stamp}.zip";

        return Results.File(archive, "application/zip", fileName);
    }
}
