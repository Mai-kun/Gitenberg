namespace Gitenberg.Web.Features.Shares;

/// <summary>Settings of the public share links feature.</summary>
public class ShareConfiguration
{
    public const string SectionName = "Sharing";

    /// <summary>
    /// Absolute base URL (e.g. https://gitenberg.wisp.uno) used to build the
    /// share links handed to the user. Empty means "same host as the bot",
    /// i.e. fall back to TelegramBot:HostAddress.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>Builds absolute /share/{token} URLs from the configured hosts.</summary>
public static class ShareLinkUrlBuilder
{
    public const string PathPrefix = "/share/";

    public static string BuildUrl(string? publicBaseUrl, string hostAddress, string token)
    {
        var baseHost = !string.IsNullOrWhiteSpace(publicBaseUrl) ? publicBaseUrl : hostAddress;
        return $"{(baseHost ?? string.Empty).TrimEnd('/')}{PathPrefix}{Uri.EscapeDataString(token)}";
    }
}
