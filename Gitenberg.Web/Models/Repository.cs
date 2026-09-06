namespace Gitenberg.Web.Models;

/// <summary>
/// One GitHub repository connected by a user. A user may keep several
/// (e.g. "Personal" and "Work") and switch the active one in settings;
/// every repository carries its own encrypted token.
/// </summary>
public class Repository
{
    public int Id { get; set; }

    public long TelegramUserId { get; set; }

    /// <summary>User-facing label, e.g. "Личное" or "Работа".</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public string? GitHubToken { get; set; }

    public string InboxPath { get; set; } = "inbox";

    public string AttachmentsPath { get; set; } = "inbox/attachments";

    public DateTime CreatedAt { get; set; }
}
