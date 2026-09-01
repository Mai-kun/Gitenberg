using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

/// <summary>
/// Validates Telegram Mini App <c>initData</c> using the HMAC-SHA256 algorithm described at
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app.
/// </summary>
public sealed class TelegramAuthValidator(string botToken) : ITelegramAuthValidator
{
    private static readonly TimeSpan MaxInitDataAge = TimeSpan.FromHours(24);

    public TelegramAuthResult Validate(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            return TelegramAuthResult.Invalid("initData is empty.");
        }

        var parameters = ParseInitData(initData);
        if (parameters.Count == 0)
        {
            return TelegramAuthResult.Invalid("initData contains no parameters.");
        }

        if (!parameters.TryGetValue("hash", out var receivedHash) || string.IsNullOrEmpty(receivedHash))
        {
            return TelegramAuthResult.Invalid("initData is missing the 'hash' parameter.");
        }

        var dataCheckString = string.Join(
            '\n',
            parameters
                .Where(kv => kv.Key != "hash" && kv.Key != "signature")
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(botToken));
        var computedHash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));

        byte[] receivedHashBytes;
        try
        {
            receivedHashBytes = Convert.FromHexString(receivedHash);
        }
        catch (FormatException)
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter is not valid hexadecimal.");
        }

        if (!CryptographicOperations.FixedTimeEquals(computedHash, receivedHashBytes))
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter does not match the computed signature.");
        }

        if (!parameters.TryGetValue("auth_date", out var authDateRaw)
            || !long.TryParse(authDateRaw, out var authDateSeconds))
        {
            return TelegramAuthResult.Invalid("initData is missing a valid 'auth_date' parameter.");
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateSeconds);
        if (DateTimeOffset.UtcNow - authDate > MaxInitDataAge)
        {
            return TelegramAuthResult.Invalid("initData is older than 24 hours.");
        }

        if (!parameters.TryGetValue("user", out var userJson) || string.IsNullOrEmpty(userJson))
        {
            return TelegramAuthResult.Invalid("initData is missing the 'user' parameter.");
        }

        TelegramUser? user;
        try
        {
            user = JsonSerializer.Deserialize<TelegramUser>(userJson);
        }
        catch (JsonException)
        {
            return TelegramAuthResult.Invalid("The 'user' parameter is not valid JSON.");
        }

        if (user is null || user.Id <= 0)
        {
            return TelegramAuthResult.Invalid("The 'user' parameter is missing a valid Telegram user id.");
        }

        return TelegramAuthResult.Valid(user);
    }

    private static Dictionary<string, string> ParseInitData(string initData)
    {
        var parameters = new Dictionary<string, string>();

        foreach (var segment in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..separatorIndex]);
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..]);
            parameters[key] = value;
        }

        return parameters;
    }
}
