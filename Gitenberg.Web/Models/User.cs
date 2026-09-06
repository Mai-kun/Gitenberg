namespace Gitenberg.Web.Models;

public class User
{
    public long TelegramId { get; set; }

    // Legacy single-repo columns: kept in the schema for migration
    // rollback safety, no longer read or written once repositories
    // have been seeded into the Repositories table.
    public string? GitHubToken { get; set; }

    public string RepositoryOwner { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    /// <summary>Repository currently used by notes, search, the bot and sync.</summary>
    public int? SelectedRepositoryId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastActivityAt { get; set; }

    public string InboxPath { get; set; } = "inbox";

    public string AttachmentsPath { get; set; } = "inbox/attachments";
}