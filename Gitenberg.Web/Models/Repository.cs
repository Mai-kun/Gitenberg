namespace Gitenberg.Web.Models;

public class Repository
{
    public int Id { get; set; }

    public long TelegramUserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public string? GitHubToken { get; set; }

    public string InboxPath { get; set; } = "inbox";

    public string AttachmentsPath { get; set; } = "inbox/attachments";

    public DateTime CreatedAt { get; set; }
}
