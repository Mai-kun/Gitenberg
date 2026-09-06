using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Activity;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
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
    IGitHubService gitHubService,
    IRepositoryContextResolver repositoryResolver,
    InlineSearchHandler inlineSearchHandler,
    ReminderService reminderService,
    ActivityService activityService,
    ILogger<UpdateHandler> logger)
{
    public async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update is { Type: UpdateType.InlineQuery, InlineQuery: { } inlineQuery })
        {
            await inlineSearchHandler.HandleInlineQueryAsync(inlineQuery, cancellationToken);
            return;
        }

        if (update is not { Type: UpdateType.Message, Message: { } message })
        {
            return;
        }

        if (message.Text?.StartsWith('/') == true)
        {
            // Bot commands may carry a @BotName suffix when used in groups.
            var command = message.Text.Split(' ', 2)[0];
            var mention = command.IndexOf('@');
            if (mention > 1)
            {
                command = command[..mention];
            }

            if (command.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                await HandleStartCommandAsync(message, cancellationToken);
            }
            else if (command.Equals("/remind", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRemindCommandAsync(message, cancellationToken);
            }

            return;
        }

        // Quick capture accepts plain text, forwarded posts and photos (with
        // or without a caption); everything else is ignored.
        if (message.Text is null && message.Caption is null && message.Photo is not { Length: > 0 })
        {
            return;
        }

        await HandleQuickCaptureAsync(message, cancellationToken);
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
                var repository = await repositoryResolver.ResolveActiveAsync(telegramId, cancellationToken);
                var repoLabel = repository != null
                    ? $"{repository.Repository.DisplayName} ({repository.Repository.RepositoryOwner}/{repository.Repository.RepositoryName})"
                    : "не настроен";
                var text =
                    $"Привет! Ты успешно авторизован. Твой активный репозиторий: {repoLabel}.";
                await botClient.SendMessage(
                    message.Chat.Id,
                    text,
                    replyMarkup: WebAppKeyboard("Открыть заметки"),
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                var text =
                    "Привет! Ты еще не зарегистрирован в Gitenberg. Нажми на кнопку ниже, чтобы привязать свой репозиторий GitHub и начать удобно вести заметки.";
                await botClient.SendMessage(
                    message.Chat.Id,
                    text,
                    replyMarkup: WebAppKeyboard("Зарегистрироваться"),
                    cancellationToken: cancellationToken
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling /start command for Telegram ID: {TelegramId}", telegramId);
        }
    }

    private async Task HandleQuickCaptureAsync(Message message, CancellationToken cancellationToken)
    {
        var telegramId = message.From?.Id ?? 0;
        if (telegramId == 0)
        {
            logger.LogWarning("Received quick capture message but From user is null or ID is 0.");
            return;
        }

        try
        {
            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
            if (user == null)
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "Сначала зарегистрируйтесь с помощью /start",
                    replyMarkup: WebAppKeyboard("Зарегистрироваться"),
                    cancellationToken: cancellationToken
                );
                return;
            }

            var repository = await repositoryResolver.ResolveActiveAsync(telegramId, cancellationToken);
            if (repository == null)
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "GitHub-репозиторий не настроен. Откройте настройки в приложении.",
                    replyMarkup: WebAppKeyboard("Открыть заметки"),
                    cancellationToken: cancellationToken
                );
                return;
            }

            var context = repository.Context;
            var inboxPath = repository.Repository.InboxPath.Trim('/');
            var attachmentsPath = repository.Repository.AttachmentsPath.Trim('/');

            var notePath = $"{inboxPath}/{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.md";

            var relativeImagePath = await SavePhotoAsync(message, context, notePath, attachmentsPath, cancellationToken);

            var text = message.Text ?? message.Caption ?? string.Empty;
            var source = QuickCaptureNoteBuilder.GetForwardSource(message);
            var content = QuickCaptureNoteBuilder.BuildNote(text, source, relativeImagePath, DateTime.Now);

            await gitHubService.CreateOrUpdateNoteAsync(context, notePath, content, "Add quick capture note");
            await activityService.RecordAsync(telegramId, null);

            await botClient.SendMessage(
                message.Chat.Id,
                $"✅ Сохранено в {notePath}",
                replyMarkup: WebAppKeyboard("Открыть заметки"),
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling quick capture for Telegram ID: {TelegramId}", telegramId);
            await botClient.SendMessage(
                message.Chat.Id,
                "❌ Не удалось сохранить заметку. Попробуйте позже.",
                cancellationToken: cancellationToken
            );
        }
    }

    // /remind <когда> <текст>: creates a note (quick-capture style) containing
    // the marker line, then schedules the reminder through the same reconcile
    // path the marker machinery uses.
    private async Task HandleRemindCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var telegramId = message.From?.Id ?? 0;
        if (telegramId == 0)
        {
            logger.LogWarning("Received /remind command but From user is null or ID is 0.");
            return;
        }

        var input = message.Text ?? string.Empty;
        var firstSpace = input.IndexOf(' ');
        var spec = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();

        var parsed = ReminderParser.TryParse(spec);
        if (parsed.FireAtUtc is not { } fireAt)
        {
            await botClient.SendMessage(message.Chat.Id, parsed.Error ?? ReminderParser.HelpText, cancellationToken: cancellationToken);
            return;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        if (user == null)
        {
            await botClient.SendMessage(
                message.Chat.Id,
                "Сначала зарегистрируйтесь с помощью /start",
                replyMarkup: WebAppKeyboard("Зарегистрироваться"),
                cancellationToken: cancellationToken
            );
            return;
        }

        try
        {
            var repository = await repositoryResolver.ResolveActiveAsync(telegramId, cancellationToken);
            if (repository == null)
            {
                await botClient.SendMessage(
                    message.Chat.Id,
                    "GitHub-репозиторий не настроен. Откройте настройки в приложении.",
                    replyMarkup: WebAppKeyboard("Открыть заметки"),
                    cancellationToken: cancellationToken
                );
                return;
            }

            var context = repository.Context;
            var inboxPath = repository.Repository.InboxPath.Trim('/');
            var notePath = $"{inboxPath}/{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_reminder.md";
            var content = BuildReminderNote(fireAt, parsed.Text, spec);

            await gitHubService.CreateOrUpdateNoteAsync(context, notePath, content, $"Add reminder note: {notePath}");
            await reminderService.UpsertForNoteAsync(telegramId, repository.RepositoryId, notePath, content);
            await activityService.RecordAsync(telegramId, null);

            await botClient.SendMessage(
                message.Chat.Id,
                $"✅ Напоминание на {ReminderService.FormatLocal(fireAt)}\n\n📄 {notePath}",
                replyMarkup: WebAppKeyboard("Открыть заметки"),
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling /remind command for Telegram ID: {TelegramId}", telegramId);
            await botClient.SendMessage(
                message.Chat.Id,
                "❌ Не удалось создать напоминание. Попробуйте позже.",
                cancellationToken: cancellationToken
            );
        }
    }

    // The note embeds the original "@remind …" marker so the reminder stays
    // reconcilable: editing or erasing the marker later updates or cancels it.
    private static string BuildReminderNote(DateTime fireAtUtc, string text, string spec)
    {
        var lines = new List<string>
        {
            $"# Напоминание {ReminderService.FormatLocal(fireAtUtc)}",
            string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            lines.Add(text);
            lines.Add(string.Empty);
        }

        lines.Add($"@remind {spec}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Downloads and commits the largest photo in the message; returns the
    /// markdown image path relative to the note file, or null when there is no photo.
    /// </summary>
    private async Task<string?> SavePhotoAsync(
        Message message,
        GitHubRepositoryContext context,
        string notePath,
        string attachmentsPath,
        CancellationToken cancellationToken
    )
    {
        if (message.Photo is not { Length: > 0 } photos)
        {
            return null;
        }

        // Telegram sends one entry per resolution; the last one is the largest.
        var photo = photos[^1];
        var file = await botClient.GetFile(photo.FileId, cancellationToken);

        await using var stream = new MemoryStream();
        await botClient.DownloadFile(file.FilePath!, stream, cancellationToken);

        var attachmentPath = $"{attachmentsPath}/{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.jpg";
        await gitHubService.UploadBinaryFileAsync(
            context,
            attachmentPath,
            stream.ToArray(),
            "Add image attachment"
        );

        return QuickCaptureNoteBuilder.GetRelativeImagePath(notePath, attachmentPath);
    }

    private InlineKeyboardMarkup WebAppKeyboard(string buttonText) =>
        new(InlineKeyboardButton.WithWebApp(buttonText, new WebAppInfo { Url = botConfig.HostAddress }));
}
