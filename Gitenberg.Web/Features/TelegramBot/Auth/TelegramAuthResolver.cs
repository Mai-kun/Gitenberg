using Microsoft.AspNetCore.Http;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

/// <summary>
/// Resolves the effective Telegram user id for an endpoint: the id verified from signed
/// initData by <see cref="TelegramAuthFilter"/> takes precedence, falling back to the
/// X-Telegram-Id header and telegramId query parameter (Development / direct test invocation).
/// </summary>
public static class TelegramAuthResolver
{
    public static long? Resolve(HttpContext? httpContext, long? headerTelegramId, long? queryTelegramId)
    {
        if (httpContext?.Items.TryGetValue(TelegramAuthFilter.ItemsKey, out var value) == true
            && value is long verifiedId)
        {
            return verifiedId;
        }

        return headerTelegramId ?? queryTelegramId;
    }
}
