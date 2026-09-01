namespace Gitenberg.Web.Models;

public class IndexedNote
{
    public long TelegramUserId { get; set; }

    public string NotePath { get; set; } = string.Empty;

    public string Sha { get; set; } = string.Empty;
}
