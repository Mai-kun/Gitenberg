namespace Gitenberg.Web.Features.TelegramBot;

public class BotConfiguration
{
    public const string SectionName = "TelegramBot";

    public string BotToken { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public string SecretToken { get; set; } = string.Empty;
}
