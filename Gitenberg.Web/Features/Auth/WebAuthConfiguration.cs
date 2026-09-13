namespace Gitenberg.Web.Features.Auth;

public class WebAuthConfiguration
{
    public const string SectionName = "WebAuth";

    public string CookieName { get; set; } = "gitenberg_session";

    public int SessionLifetimeDays { get; set; } = 30;
}
