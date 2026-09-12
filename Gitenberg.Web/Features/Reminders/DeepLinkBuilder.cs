using System.Text;

namespace Gitenberg.Web.Features.Reminders;

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
