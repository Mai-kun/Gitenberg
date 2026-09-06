using Gitenberg.Web.Database;
using Gitenberg.Web.Features.TelegramBot.Auth;
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
                       .RequireTelegramAuth();

        group.MapGet("/heatmap", GetHeatmap)
             .WithName("GetActivityHeatmap")
             .WithSummary("Daily note-activity counts for the heatmap widget");
    }

    public static async Task<IResult> GetHeatmap(
        [FromHeader(Name = "X-Timezone-Offset")] int? timezoneOffset,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." }
            );
        }

        var service = new ActivityService(dbContext);
        var days = await service.GetHeatmapAsync(telegramId.Value, timezoneOffset);
        return Results.Ok(new { Days = days, Total = days.Sum(d => d.Count) });
    }
}
