using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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
    }
}
