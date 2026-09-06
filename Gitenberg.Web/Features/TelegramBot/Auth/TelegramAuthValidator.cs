using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

public sealed class TelegramAuthValidator(string botToken, ILogger<TelegramAuthValidator> logger) : ITelegramAuthValidator
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
            return TelegramAuthResult.Invalid("Missing 'hash' parameter.");
        }

        // Строка проверки строится по спецификации Telegram: все поля, кроме
        // hash и signature, отсортированные по ключу, с '\n' в качестве разделителя.
        var dataCheckString = string.Join(
            "\n",
            parameters
                .Where(kv => kv.Key != "hash" && kv.Key != "signature")
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        // Токен приходит из переменной окружения и может содержать \r (Windows-перенос),
        // который попал бы в ключ HMAC и сделал бы совпадение невозможным.
        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(botToken.Trim()));

        var computedHashBytes = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHashHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

        // Временный диагностический лог: показывает точную строку проверки и оба хэша,
        // чтобы по консоли на хостинге можно было определить причину расхождения.
        logger.LogInformation("--- TELEGRAM AUTH VALIDATION ---");
        logger.LogInformation("DataCheckString:\n{DataCheckString}", dataCheckString);
        logger.LogInformation("Received Hash: {ReceivedHash}", receivedHash);
        logger.LogInformation("Computed Hash: {ComputedHash}", computedHashHex);

        byte[] receivedHashBytes;
        try
        {
            receivedHashBytes = Convert.FromHexString(receivedHash);
        }
        catch (FormatException)
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter is not valid hex.");
        }

        // Константное по времени сравнение защищает от timing-атак.
        if (!CryptographicOperations.FixedTimeEquals(computedHashBytes, receivedHashBytes))
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter does not match the computed signature.");
        }

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

            // '+' в application/x-www-form-urlencoded означает пробел, а
            // Uri.UnescapeDataString оставляет его как есть — заменяем вручную,
            // иначе строка проверки не совпадёт с подписанной Telegram.
            var key = Uri.UnescapeDataString(segment[..separatorIndex].Replace("+", "%20"));
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..].Replace("+", "%20"));
            parameters[key] = value;
        }

        return parameters;
    }
}
