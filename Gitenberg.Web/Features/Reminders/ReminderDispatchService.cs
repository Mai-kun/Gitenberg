using Gitenberg.Web.Database;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Gitenberg.Web.Features.Reminders;

/// <summary>
/// Periodically delivers due reminders to the user's Telegram chat. A failed
/// delivery bumps Attempts; after MaxAttempts failures the reminder is left
/// unsent and no longer retried.
/// </summary>
public class ReminderDispatchService(
    IServiceScopeFactory scopeFactory,
    BotConfiguration botConfig,
    ILogger<ReminderDispatchService> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await DispatchDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Reminder dispatch cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown requested.
        }
    }

    internal async Task DispatchDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reminderService = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
        var identity = scope.ServiceProvider.GetRequiredService<BotIdentityService>();

        var due = await reminderService.GetDueAsync(DateTime.UtcNow);
        if (due.Count == 0)
        {
            return;
        }

        var botUsername = await identity.GetUsernameAsync(cancellationToken);

        foreach (var reminder in due)
        {
            var user = await dbContext.Users
                .FirstOrDefaultAsync(u => u.TelegramId == reminder.TelegramId, cancellationToken);
            if (user is null)
            {
                // No account to deliver to — retire the reminder instead of retrying forever.
                await reminderService.MarkSentAsync(reminder.Id);
                continue;
            }

            try
            {
                await botClient.SendMessage(
                    reminder.TelegramId,
                    BuildMessageText(reminder),
                    replyMarkup: BuildKeyboard(botUsername, reminder, botConfig.HostAddress),
                    cancellationToken: cancellationToken
                );
                await reminderService.MarkSentAsync(reminder.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await reminderService.IncrementAttemptsAsync(reminder.Id);
                logger.LogWarning(
                    ex,
                    "Failed to deliver reminder {ReminderId} to {TelegramId} at {FireAt}.",
                    reminder.Id,
                    reminder.TelegramId,
                    reminder.FireAtUtc
                );
            }
        }
    }

    internal static string BuildMessageText(Reminder reminder)
    {
        var lines = new List<string> { "⏰ Напоминание", string.Empty };
        if (!string.IsNullOrWhiteSpace(reminder.Text))
        {
            lines.Add(reminder.Text);
            lines.Add(string.Empty);
        }
        if (!string.IsNullOrWhiteSpace(reminder.NotePath))
        {
            lines.Add($"📄 {reminder.NotePath}");
        }
        return string.Join("\n", lines).TrimEnd();
    }

    // The t.me/startapp URL button deep-links into the note of the repository
    // the reminder belongs to; it is only built when the payload fits
    // Telegram's 64-char startapp limit. The WebApp button (no deep link) is
    // always offered as the fallback.
    internal static InlineKeyboardMarkup? BuildKeyboard(string? botUsername, Reminder reminder, string hostAddress)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        if (!string.IsNullOrWhiteSpace(reminder.NotePath))
        {
            var noteUrl = DeepLinkBuilder.BuildNoteUrl(botUsername, reminder.RepositoryId, reminder.NotePath);
            if (noteUrl != null)
            {
                rows.Add([InlineKeyboardButton.WithUrl("Открыть заметку", noteUrl)]);
            }
        }

        if (!string.IsNullOrWhiteSpace(hostAddress))
        {
            rows.Add([InlineKeyboardButton.WithWebApp("Открыть приложение", new WebAppInfo { Url = hostAddress })]);
        }

        return rows.Count > 0 ? new InlineKeyboardMarkup(rows) : null;
    }
}
