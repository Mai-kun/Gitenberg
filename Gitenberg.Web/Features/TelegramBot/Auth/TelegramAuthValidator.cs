using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

public sealed class TelegramAuthValidator(
    string botToken,
    ILogger<TelegramAuthValidator> logger,
    IHttpClientFactory? httpClientFactory = null) : ITelegramAuthValidator
{
    private static readonly TimeSpan MaxInitDataAge = TimeSpan.FromHours(24);

    public TelegramAuthResult Validate(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            return TelegramAuthResult.Invalid("initData is empty.");
        }

        var segments = ParseSegments(initData);
        if (segments.Count == 0)
        {
            return TelegramAuthResult.Invalid("initData contains no parameters.");
        }

        if (!TryGetParameter(segments, "hash", out var receivedHash) || string.IsNullOrEmpty(receivedHash))
        {
            return TelegramAuthResult.Invalid("Missing 'hash' parameter.");
        }

        // Строка проверки по спецификации Telegram: все поля, кроме hash и signature,
        // с '\n' в качестве разделителя. Канонический вариант — из декодированных
        // значений (так делают официальный пример в доках и все библиотеки). Дополнительно
        // строим вариант из сырых percent-encoded значений: если подпись сойдётся только
        // на нём, значит initData подписан в закодированном виде — fallback примет его.
        var dataCheckString = BuildCheckString(segments, raw: false);
        var rawCheckString = BuildCheckString(segments, raw: true);

        // Токен приходит из переменной окружения и может содержать \r (Windows-перенос),
        // который попал бы в ключ HMAC и сделал бы совпадение невозможным.
        var cleanToken = botToken.Trim();
        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(cleanToken));

        var decodedHashBytes = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var rawHashBytes = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(rawCheckString));

        // Временная расширенная диагностика: сырой initData и оба варианта строки проверки
        // позволяют по консоли хостинга найти точную причину расхождения хэшей.
        var botId = cleanToken.Split(':', 2)[0];
        var tokenFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cleanToken)))[..8].ToLowerInvariant();

        logger.LogInformation("--- TELEGRAM AUTH VALIDATION ---");
        logger.LogInformation("Raw initData:\n{RawInitData}", initData);
        logger.LogInformation("DataCheckString (decoded):\n{DataCheckString}", dataCheckString);
        logger.LogInformation("DataCheckString (raw):\n{RawCheckString}", rawCheckString);
        logger.LogInformation("Received Hash: {ReceivedHash}", receivedHash);
        logger.LogInformation("Computed Hash (decoded): {ComputedHash}", Convert.ToHexString(decodedHashBytes).ToLowerInvariant());
        logger.LogInformation("Computed Hash (raw): {ComputedHash}", Convert.ToHexString(rawHashBytes).ToLowerInvariant());
        logger.LogInformation("Bot token: botId={BotId}, length={Length}, sha256={Fingerprint}", botId, cleanToken.Length, tokenFingerprint);

        byte[] receivedHashBytes;
        try
        {
            receivedHashBytes = Convert.FromHexString(receivedHash);
        }
        catch (FormatException)
        {
            return TelegramAuthResult.Invalid("The 'hash' parameter is not valid hex.");
        }

        // Константное по времени сравнение защищает от timing-атак; оба варианта
        // строки криптографически эквивалентны (тот же секрет), поэтому приём
        // любого из них не ослабляет проверку подлинности.
        var matchesDecoded = CryptographicOperations.FixedTimeEquals(decodedHashBytes, receivedHashBytes);
        var matchesRaw = !matchesDecoded && CryptographicOperations.FixedTimeEquals(rawHashBytes, receivedHashBytes);

        if (!matchesDecoded && !matchesRaw)
        {
            // Строка проверки воспроизводится байт-в-байт, токен сверен getMe —
            // при устойчивом несовпадении спрашиваем у Telegram, чей это токен.
            logger.LogWarning("Telegram initData hash mismatch (neither decoded nor raw variant matched). Verifying which bot the configured token belongs to...");
            ProbeTokenBot();

            return TelegramAuthResult.Invalid("The 'hash' parameter does not match the computed signature.");
        }

        if (matchesRaw)
        {
            logger.LogWarning("initData hash matched the RAW percent-encoded check-string, not the decoded one.");
        }

        if (!TryGetParameter(segments, "auth_date", out var authDateRaw)
            || !long.TryParse(authDateRaw, out var authDateSeconds))
        {
            return TelegramAuthResult.Invalid("Missing or invalid 'auth_date'.");
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateSeconds);
        if (DateTimeOffset.UtcNow - authDate > MaxInitDataAge)
        {
            return TelegramAuthResult.Invalid("initData is older than 24 hours.");
        }

        if (!TryGetParameter(segments, "user", out var userJson) || string.IsNullOrEmpty(userJson))
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

    private sealed record RawParameter(string RawKey, string RawValue, string Key, string Value);

    private static List<RawParameter> ParseSegments(string initData)
    {
        var segments = new List<RawParameter>();

        foreach (var segment in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var rawKey = segment[..separatorIndex];
            var rawValue = segment[(separatorIndex + 1)..];

            // '+' в application/x-www-form-urlencoded означает пробел, а
            // Uri.UnescapeDataString оставляет его как есть — заменяем вручную.
            var key = Uri.UnescapeDataString(rawKey.Replace("+", "%20"));
            var value = Uri.UnescapeDataString(rawValue.Replace("+", "%20"));
            segments.Add(new RawParameter(rawKey, rawValue, key, value));
        }

        return segments;
    }

    private static string BuildCheckString(List<RawParameter> segments, bool raw)
    {
        var ordered = segments
            .Where(p => raw ? p.RawKey != "hash" && p.RawKey != "signature" : p.Key != "hash" && p.Key != "signature")
            .OrderBy(p => raw ? p.RawKey : p.Key, StringComparer.Ordinal);

        return string.Join("\n", ordered.Select(p => raw ? $"{p.RawKey}={p.RawValue}" : $"{p.Key}={p.Value}"));
    }

    private static bool TryGetParameter(List<RawParameter> segments, string key, out string value)
    {
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            if (segments[i].Key == key)
            {
                value = segments[i].Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    // Огненно-забытая проба: диагностический запрос не должен задерживать 401-ответ
    // и не должен уронить обработку запроса при сетевой ошибке.
    private void ProbeTokenBot()
    {
        if (httpClientFactory is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await client.GetAsync($"https://api.telegram.org/bot{botToken.Trim()}/getMe", cts.Token);
                var payload = await response.Content.ReadAsStringAsync(cts.Token);

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.GetProperty("ok").GetBoolean())
                {
                    logger.LogWarning("Bot token check: Telegram rejected the token (getMe failed). The token is revoked or invalid.");
                    return;
                }

                var result = doc.RootElement.GetProperty("result");
                logger.LogWarning(
                    "Bot token check: the configured token belongs to @{Username} (id {ProbeBotId}). The Mini App must be opened from THIS bot, otherwise initData is signed with a different token.",
                    result.GetProperty("username").GetString(),
                    result.GetProperty("id").GetInt64());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bot token check probe failed.");
            }
        });
    }
}
