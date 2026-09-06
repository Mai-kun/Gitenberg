using FluentAssertions;
using Gitenberg.Web.Features.Search;
using Xunit;

namespace Gitenberg.Tests.Features.Search;

public class FtsQueryBuilderTests
{
    [Theory]
    [InlineData("kernel", "\"kernel\"*")]
    [InlineData("kernel scheduling", "\"kernel\"* \"scheduling\"*")]
    [InlineData("  spaced   out  ", "\"spaced\"* \"out\"*")]
    public void Build_ShouldCreateQuotedPrefixTerms(string rawQuery, string expected)
    {
        FtsQueryBuilder.Build(rawQuery).Should().Be(expected);
    }

    [Fact]
    public void Build_ShouldStripQuotes_FromUserInput()
    {
        // An unbalanced quote used to be invalid FTS5 MATCH syntax.
        FtsQueryBuilder.Build("\"").Should().BeEmpty();
        FtsQueryBuilder.Build("foo\"bar").Should().Be("\"foobar\"*");
    }

    [Fact]
    public void Build_ShouldIgnorePunctuationOnlyTerms()
    {
        FtsQueryBuilder.Build("# !! ???").Should().BeEmpty();
    }

    [Fact]
    public void Build_ShouldLimitTheNumberOfTerms()
    {
        var query = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"term{i}"));
        var expression = FtsQueryBuilder.Build(query);
        expression.Split(' ').Should().HaveCount(FtsQueryBuilder.MaxTerms);
    }
}
