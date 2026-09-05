using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

// Добавьте using

namespace Gitenberg.Web.Features.TelegramBot.Auth;

public sealed class TelegramAuthValidator(string botToken) : ITelegramAuthValidator
{
    private static readonly TimeSpan MaxInitDataAge = TimeSpan.FromHours(24);

    public TelegramAuthResult Validate(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            return TelegramAuthResult.Invalid("initData is empty.");
        }

        var parsed = HttpUtility.ParseQueryString(initData);
        var parameters = new Dictionary<string, string>();

        string? receivedHash = null;

        foreach (var key in parsed.AllKeys)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var val = parsed[key] ?? string.Empty;

            if (key == "hash")
            {
                receivedHash = val;
            }
            else if (key != "signature")
            {
                parameters[key] = val;
            }
        }

        if (string.IsNullOrEmpty(receivedHash))
        {
            return TelegramAuthResult.Invalid("Missing 'hash' parameter.");
        }

        // Собираем data-check-string ровно так, как требует Telegram
        var dataCheckString = string.Join(
                "\n",
                parameters
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}={kv.Value}")
        );

        // Вычисляем ключ и хэш
        var secretKey = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("WebAppData"),
                Encoding.UTF8.GetBytes(botToken.Trim())
        );

        var computedHashBytes = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHashHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

        // Сравниваем
        if (!string.Equals(computedHashHex, receivedHash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter does not match the computed signature.");
        }

        // Проверка времени
        if (!parameters.TryGetValue("auth_date", out var authDateRaw)
            || !long.TryParse(authDateRaw, out var authDateSeconds))
        {
            return TelegramAuthResult.Invalid("Missing or invalid 'auth_date'.");
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateSeconds);
        if (DateTimeOffset.UtcNow - authDate > MaxInitDataAge)
        {
            return TelegramAuthResult.Invalid("initData is older than 24 hours.");
        }

        // Проверка пользователя
        if (!parameters.TryGetValue("user", out var userJson) || string.IsNullOrEmpty(userJson))
        {
            return TelegramAuthResult.Invalid("Missing 'user' parameter.");
        }

        try
        {
            var user = JsonSerializer.Deserialize<TelegramUser>(userJson);
            if (user is null || user.Id <= 0)
            {
                return TelegramAuthResult.Invalid("Invalid Telegram user id.");
            }

            return TelegramAuthResult.Valid(user);
        }
        catch (JsonException)
        {
            return TelegramAuthResult.Invalid("The 'user' parameter is not valid JSON.");
        }
    }
}