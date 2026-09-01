using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Gitenberg.Web.Features.TelegramBot;

public class ConfigureWebhook(
    ITelegramBotClient botClient,
    BotConfiguration botConfig,
    ILogger<ConfigureWebhook> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(botConfig.BotToken))
        {
            logger.LogWarning("Telegram BotToken is not configured. Skipping webhook registration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(botConfig.HostAddress))
        {
            logger.LogWarning("Telegram HostAddress is not configured. Skipping webhook registration.");
            return;
        }

        var webhookAddress = $"{botConfig.HostAddress.TrimEnd('/')}/api/bot/webhook";
        logger.LogInformation("Setting webhook to: {WebhookAddress}", webhookAddress);

        await botClient.SetWebhook(
            url: webhookAddress,
            allowedUpdates: [UpdateType.Message],
            secretToken: botConfig.SecretToken,
            cancellationToken: cancellationToken
        );
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Removing webhook");
        try
        {
            await botClient.DeleteWebhook(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while deleting webhook");
        }
    }
}
