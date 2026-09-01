namespace Gitenberg.Web.Features.Search;

public class SearchConfiguration
{
    public const string SectionName = "Search";

    public int IndexingIntervalMinutes { get; set; } = 60;
}
