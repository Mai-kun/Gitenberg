using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Gitenberg.Web.Features.TelegramBot;

public class UpdateHandler(
    ITelegramBotClient botClient,
    AppDbContext dbContext,
    BotConfiguration botConfig,
    ILogger<UpdateHandler> logger)
{
    public async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Type != UpdateType.Message
            || update.Message is not { Text: { } messageText } message)
        {
            return;
        }

        if (messageText.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartCommandAsync(message, cancellationToken);
        }
    }

    private async Task HandleStartCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var telegramId = message.From?.Id ?? 0;
        if (telegramId == 0)
        {
            logger.LogWarning("Received /start command but From user is null or ID is 0.");
            return;
        }

        logger.LogInformation("Processing /start command for Telegram ID: {TelegramId}", telegramId);

        try
        {
            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);

            if (user != null)
            {
                var text =
                    $"Привет! Ты успешно авторизован. Твой рабочий репозиторий: {user.RepositoryOwner}/{user.RepositoryName}.";
                var replyMarkup = new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithWebApp("Открыть заметки", new WebAppInfo { Url = botConfig.HostAddress })
                );

                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    text,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                var text =
                    "Привет! Ты еще не зарегистрирован в Gitenberg. Нажми на кнопку ниже, чтобы привязать свой репозиторий GitHub и начать удобно вести заметки.";
                var replyMarkup = new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithWebApp("Зарегистрироваться", new WebAppInfo { Url = botConfig.HostAddress })
                );

                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    text,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling /start command for Telegram ID: {TelegramId}", telegramId);
        }
    }
}