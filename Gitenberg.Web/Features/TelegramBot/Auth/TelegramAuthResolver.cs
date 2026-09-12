using Microsoft.AspNetCore.Http;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

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
