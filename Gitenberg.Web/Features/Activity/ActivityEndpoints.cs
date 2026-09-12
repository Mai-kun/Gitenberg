using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Gitenberg.Web.Features.Activity;

public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/activity")
                       .WithTags("Activity")
                       .RequireAuth();

        group.MapGet("/heatmap", GetHeatmap)
             .WithName("GetActivityHeatmap")
             .WithSummary("Daily note-activity counts for the heatmap widget");
    }

    public static async Task<IResult> GetHeatmap(
        [FromHeader(Name = "X-Timezone-Offset")] int? timezoneOffset,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return Results.BadRequest(new { Error = "'from' must not be later than 'to'." });
        }

        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'userId' query parameter." }
            );
        }

        var service = new ActivityService(dbContext);
        var days = await service.GetHeatmapAsync(userId.Value, timezoneOffset, from: from, to: to);
        return Results.Ok(new { Days = days, Total = days.Sum(d => d.Count) });
    }
}
