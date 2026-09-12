namespace Gitenberg.Web.Features.Auth;

// Resolves the authenticated user id set by WebAuthFilter. Historically this
// was the Telegram user id; the web app stores the GitHub user id in the same
// internal key (Users.TelegramId column), so all repository-scoped services
// keep working unchanged.
public static class CurrentUserId
{
    public const string ItemsKey = "UserId";

    public static long? From(HttpContext? httpContext)
    {
        return httpContext?.Items.TryGetValue(ItemsKey, out var value) == true && value is long id
            ? id
            : null;
    }
}
