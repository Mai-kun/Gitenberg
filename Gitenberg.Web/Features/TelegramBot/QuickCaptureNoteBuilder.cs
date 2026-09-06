using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Gitenberg.Web.Features.TelegramBot;

/// <summary>
/// Pure helpers for quick-capture notes: forward-source detection, relative
/// image links and the markdown note template. Kept side-effect free for tests.
/// </summary>
public static class QuickCaptureNoteBuilder
{
    /// <summary>
    /// Human-readable title of the original forwarded source, or null when the
    /// message is not a forward.
    /// </summary>
    public static string? GetForwardSource(Message message)
    {
        switch (message.ForwardOrigin)
        {
            case MessageOriginChannel channel:
                return channel.Chat?.Title ?? channel.AuthorSignature;
            case MessageOriginChat chat:
                return chat.SenderChat?.Title ?? chat.AuthorSignature;
            case MessageOriginUser user:
                return GetUserName(user.SenderUser);
            case MessageOriginHiddenUser hidden:
                return hidden.SenderUserName;
        }

        // Legacy fields for older bot API payloads.
        if (message.ForwardFromChat?.Title is { } chatTitle)
        {
            return chatTitle;
        }
        if (message.ForwardFrom is { } forwardFrom)
        {
            return GetUserName(forwardFrom);
        }

        return message.ForwardSenderName;
    }

    private static string? GetUserName(User? user)
    {
        if (user is null)
        {
            return null;
        }

        var fullName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return fullName.Length > 0 ? fullName : user.Username;
    }

    /// <summary>
    /// Path of <paramref name="attachmentPath"/> relative to the note's folder,
    /// using forward slashes so the link renders on GitHub and in Obsidian.
    /// </summary>
    public static string GetRelativeImagePath(string notePath, string attachmentPath)
    {
        var noteSegments = notePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var attachmentSegments = attachmentPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The note's own segment is its file name; the relative link starts
        // from the note's directory.
        var noteDir = noteSegments[..^1];

        var common = 0;
        while (common < noteDir.Length
               && common < attachmentSegments.Length - 1
               && string.Equals(noteDir[common], attachmentSegments[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        var upCount = noteDir.Length - common;
        var up = string.Concat(Enumerable.Repeat("../", upCount));
        var relative = string.Join('/', attachmentSegments[common..]);
        return up + relative;
    }

    /// <summary>
    /// Markdown body of a quick-capture note. The source quote and image line
    /// are omitted when there is no forwarded source / photo.
    /// </summary>
    public static string BuildNote(string text, string? source, string? relativeImagePath, DateTime timestamp)
    {
        var lines = new List<string>
        {
            $"# Заметка от {timestamp:yyyy-MM-dd HH:mm}",
            string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(source))
        {
            lines.Add($"> 📢 **Источник:** {source}");
            lines.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(relativeImagePath))
        {
            lines.Add($"![Изображение]({relativeImagePath})");
            lines.Add(string.Empty);
        }

        lines.Add(text ?? string.Empty);

        return string.Join("\n", lines);
    }
}
