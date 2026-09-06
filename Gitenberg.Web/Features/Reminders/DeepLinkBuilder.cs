using System.Text;

namespace Gitenberg.Web.Features.Reminders;

/// <summary>
/// Builds t.me deep links that open the Mini App on a specific note via the
/// startapp parameter (Base64Url of "note:&lt;repoId&gt;:&lt;path&gt;"). The
/// repository id lets the Mini App switch repositories before opening the
/// note; payloads without it (legacy) resolve to the active repository.
/// Telegram limits startapp to 64 chars of [A-Za-z0-9_-]; longer note paths
/// yield no URL and the caller falls back to the plain WebApp button.
/// </summary>
public static class DeepLinkBuilder
{
    public const int MaxPayloadLength = 64;

    public static string EncodePayload(int repositoryId, string notePath) =>
        Base64UrlEncode($"note:{repositoryId}:{notePath.Trim('/')}");

    public static string? BuildNoteUrl(string? botUsername, int repositoryId, string? notePath)
    {
        if (string.IsNullOrWhiteSpace(botUsername) || string.IsNullOrWhiteSpace(notePath))
        {
            return null;
        }

        var payload = EncodePayload(repositoryId, notePath);
        if (payload.Length > MaxPayloadLength)
        {
            return null;
        }

        return $"https://t.me/{botUsername}?startapp={payload}";
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
