namespace Gitenberg.Web.Features.TelegramBot.Auth;

public record TelegramAuthResult(bool IsValid, TelegramUser? User = null, string? Error = null)
{
    public static TelegramAuthResult Valid(TelegramUser user) => new(true, user);

    public static TelegramAuthResult Invalid(string error) => new(false, User: null, Error: error);
}
