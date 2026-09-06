using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitenberg.Web.Features.Reminders;

public record ReminderParseResult(DateTime? FireAtUtc, string Text, string? Error);

/// <summary>
/// Parses the whole tail after "@remind" or "/remind": "when [text]".
/// Two-token "date time" is tried before a single token, so
/// "2026-09-10 15:00 Купить молоко" never loses its time into the text.
/// Absolute times are interpreted in the server's local time zone and
/// returned as UTC.
/// </summary>
public static partial class ReminderParser
{
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "dd.MM.yyyy HH:mm"];
    private static readonly string[] DateOnlyFormats = ["yyyy-MM-dd", "dd.MM.yyyy"];
    private static readonly TimeSpan DateOnlyDefaultTime = new(9, 0, 0);

    [GeneratedRegex(@"^(\d+)(m|min|мин|м|h|ч|d|д|w|нед|н)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeSingleTokenRegex();

    [GeneratedRegex(@"^(\d+)\s+(мин|м|ч|д|нед|н)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeSpacedRegex();

    public const string HelpText =
        "Не удалось распознать срок. Примеры: /remind 2h Позвонить врачу, " +
        "/remind 30м Проверить почту, /remind 2026-09-10 15:00 Встреча, " +
        "/remind 10.09.2026 15:00 Встреча, /remind 15:00 Чай.";

    public static ReminderParseResult TryParse(string? input, DateTime? utcNow = null)
    {
        var nowUtc = utcNow ?? DateTime.UtcNow;
        var tokens = (input ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return new ReminderParseResult(null, string.Empty, HelpText);
        }

        // 1) First two tokens as "date time".
        if (tokens.Length >= 2
            && DateTime.TryParseExact(
                string.Join(' ', tokens[0], tokens[1]),
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime))
        {
            return Success(dateTime, tokens[2..]);
        }

        // 2) Two tokens as a spaced relative offset ("2 ч", "15 мин").
        if (tokens.Length >= 2)
        {
            var spaced = RelativeSpacedRegex().Match(string.Join(' ', tokens[0], tokens[1]));
            if (spaced.Success && TryParseRelative(spaced.Groups[1].Value, spaced.Groups[2].Value, nowUtc, out var fireAtSpaced))
            {
                return new ReminderParseResult(fireAtSpaced, string.Join(' ', tokens[2..]), null);
            }
        }

        // 3) Single token: relative offset, date-only or time-only.
        var single = tokens[0];
        var relative = RelativeSingleTokenRegex().Match(single);
        if (relative.Success && TryParseRelative(relative.Groups[1].Value, relative.Groups[2].Value, nowUtc, out var fireAtRelative))
        {
            return new ReminderParseResult(fireAtRelative, string.Join(' ', tokens[1..]), null);
        }

        if (DateTime.TryParseExact(single, DateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            var local = dateOnly.Date + DateOnlyDefaultTime;
            return new ReminderParseResult(ToUtc(local), string.Join(' ', tokens[1..]), null);
        }

        if (DateTime.TryParseExact(single, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOnly))
        {
            var local = DateTime.Today + timeOnly.TimeOfDay;
            if (local <= nowUtc.ToLocalTime())
            {
                local = local.AddDays(1);
            }
            return new ReminderParseResult(ToUtc(local), string.Join(' ', tokens[1..]), null);
        }

        return new ReminderParseResult(null, string.Empty, HelpText);
    }

    private static ReminderParseResult Success(DateTime absoluteLocal, string[] textTokens)
    {
        return new ReminderParseResult(ToUtc(absoluteLocal), string.Join(' ', textTokens), null);
    }

    private static bool TryParseRelative(string amountText, string unit, DateTime nowUtc, out DateTime fireAtUtc)
    {
        fireAtUtc = nowUtc;
        if (!int.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        var unitLower = unit.ToLowerInvariant();
        var duration = unitLower switch
        {
            "m" or "min" or "мин" or "м" => TimeSpan.FromMinutes(amount),
            "h" or "ч" => TimeSpan.FromHours(amount),
            "d" or "д" => TimeSpan.FromDays(amount),
            "w" or "нед" or "н" => TimeSpan.FromDays(amount * 7),
            _ => TimeSpan.Zero,
        };

        if (duration == TimeSpan.Zero)
        {
            return false;
        }

        fireAtUtc = nowUtc + duration;
        return true;
    }

    private static DateTime ToUtc(DateTime unspecifiedLocal)
    {
        return DateTime.SpecifyKind(unspecifiedLocal, DateTimeKind.Local).ToUniversalTime();
    }
}
