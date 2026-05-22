namespace Gitenberg.Web.Models;

public class User
{
    public long TelegramId { get; set; }

    public string? GitHubToken { get; set; }

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastActivityAt { get; set; }
}