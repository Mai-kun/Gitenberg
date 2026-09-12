namespace Gitenberg.Web.Features.Shares;

public class ShareConfiguration
{
    public const string SectionName = "Sharing";

    public string? PublicBaseUrl { get; set; }
}

public static class ShareLinkUrlBuilder
{
    public const string PathPrefix = "/share/";

    public static string BuildUrl(string? publicBaseUrl, string hostAddress, string token)
    {
        var baseHost = !string.IsNullOrWhiteSpace(publicBaseUrl) ? publicBaseUrl : hostAddress;
        return $"{(baseHost ?? string.Empty).TrimEnd('/')}{PathPrefix}{Uri.EscapeDataString(token)}";
    }
}
