using System.Security.Cryptography;
using System.Text;

namespace Gitenberg.Web.Features.TelegramBot;

/// <summary>
/// Creates and validates short-lived HMAC-signed links used by inline search
/// results so a note file can be downloaded without exposing the user's
/// GitHub token or leaving the endpoint open to anyone.
/// </summary>
public class InlineFileLinkService(BotConfiguration botConfig)
{
    public const int LifetimeMinutes = 60;

    public const string PathSegment = "/api/bot/inline-file";

    // Telegram only accepts URL-delivered documents as PDF or ZIP, so the
    // "file" result wraps the note markdown in a ZIP archive.
    public const string MarkdownFormat = "md";
    public const string ZipFormat = "zip";

    // The webhook secret doubles as the signing key; the bot token is the
    // fallback so links keep working before the secret is configured.
    private string SigningKey =>
        !string.IsNullOrWhiteSpace(botConfig.SecretToken) ? botConfig.SecretToken : botConfig.BotToken;

    public string CreateSignature(long telegramId, string notePath, long expiresAtUnixSeconds, string format = MarkdownFormat)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey));
        var hash = hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"{telegramId}\n{notePath}\n{expiresAtUnixSeconds}\n{format}")
        );
        return Convert.ToHexString(hash);
    }

    /// <summary>Builds a signed download URL valid for <see cref="LifetimeMinutes"/>.</summary>
    public string BuildFileUrl(long telegramId, string notePath, string format, string hostAddress)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(LifetimeMinutes).ToUnixTimeSeconds();
        return BuildFileUrl(
            telegramId,
            notePath,
            format,
            expiresAt,
            CreateSignature(telegramId, notePath, expiresAt, format),
            hostAddress
        );
    }

    public string BuildFileUrl(
        long telegramId,
        string notePath,
        string format,
        long expiresAtUnixSeconds,
        string signature,
        string hostAddress
    ) =>
        $"{hostAddress.TrimEnd('/')}{PathSegment}" +
        $"?uid={telegramId}&exp={expiresAtUnixSeconds}&format={format}&sig={signature}&path={Uri.EscapeDataString(notePath)}";

    public bool TryValidate(long telegramId, string notePath, long expiresAtUnixSeconds, string format, string? signature)
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnixSeconds)
        {
            return false;
        }

        var expected = CreateSignature(telegramId, notePath, expiresAtUnixSeconds, format);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature)
        );
    }
}
