namespace Gitenberg.Web.Features.TelegramBot.Auth;

public interface ITelegramAuthValidator
{
    TelegramAuthResult Validate(string initData);
}
