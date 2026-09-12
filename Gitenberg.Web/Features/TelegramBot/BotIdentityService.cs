using Telegram.Bot;

namespace Gitenberg.Web.Features.TelegramBot;

public class BotIdentityService(ITelegramBotClient botClient)
{
    private string? _username;
    private bool _resolved;

    public async Task<string?> GetUsernameAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved)
        {
            return _username;
        }

        _resolved = true;
        try
        {
            _username = (await botClient.GetMe(cancellationToken)).Username;
        }
        catch
        {
            // Deep links are optional; without a username only the WebApp button is sent.
        }

        return _username;
    }
}
