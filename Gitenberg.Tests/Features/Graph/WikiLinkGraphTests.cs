using FluentAssertions;
using Gitenberg.Web.Features.Graph;
using Xunit;

namespace Gitenberg.Tests.Features.Graph;

public class WikiLinkGraphTests
{
    [Fact]
    public void Build_ShouldLinkNotesByBareName()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("inbox/note.md", "See [[ideas]] here."),
            ("ideas.md", "Root note."),
        ]);

        graph.Nodes.Should().HaveCount(2);
        graph.Links.Should().ContainSingle(l => l.Source == "inbox/note.md" && l.Target == "ideas.md");
    }

    [Fact]
    public void Build_ShouldLinkNotesByPathWithMdSuffix()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[folder/b]]"),
            ("folder/b.md", "Target."),
        ]);

        graph.Links.Should().ContainSingle(l => l.Source == "a.md" && l.Target == "folder/b.md");
    }

    [Fact]
    public void Build_ShouldUseLabelPartOnlyForResolution()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[b|pretty label]]"),
            ("b.md", "Target."),
        ]);

        graph.Links.Should().ContainSingle(l => l.Source == "a.md" && l.Target == "b.md");
    }

    [Fact]
    public void Build_ShouldResolveBareNameToShortestPath_WhenNamesCollide()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("root.md", "[[note]]"),
            ("deeply/nested/folder/note.md", "Far."),
            ("short/note.md", "Close."),
        ]);

        graph.Links.Should().ContainSingle(l => l.Target == "short/note.md");
    }

    [Fact]
    public void Build_ShouldSkipSelfLinksAndUnresolvedTargets()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[a]] and [[missing]] stay out."),
        ]);

        graph.Nodes.Should().HaveCount(1);
        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public void Build_ShouldDeduplicateRepeatedLinks()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[b]] then [[b|again]] then [[b.md]]"),
            ("b.md", "Target."),
        ]);

        graph.Links.Should().ContainSingle();
        graph.Nodes.Should().Contain(n => n.Path == "a.md" && n.Degree == 1);
    }

    [Fact]
    public void Build_ShouldSkipLinksInsideCodeFences()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "```\n[[b]]\n```"),
            ("b.md", "Target."),
        ]);

        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public void Build_ShouldCountDegreeForBothEnds()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[b]]"),
            ("b.md", "[[c]]"),
            ("c.md", "End."),
        ]);

        graph.Nodes.Should().Contain(n => n.Path == "b.md" && n.Degree == 2);
        graph.Nodes.Should().Contain(n => n.Path == "a.md" && n.Degree == 1);
        graph.Nodes.Should().Contain(n => n.Path == "c.md" && n.Degree == 1);
    }

    [Fact]
    public void Build_ShouldTreatOrphanNotesAsZeroDegreeNodes()
    {
        var graph = WikiLinkGraph.Build(
        [
            ("a.md", "[[b]]"),
            ("b.md", "Target."),
            ("lonely.md", "No links at all."),
        ]);

        graph.Nodes.Should().Contain(n => n.Path == "lonely.md" && n.Degree == 0);
    }

    [Fact]
    public void Build_ShouldCapNodesAndDropDanglingLinks()
    {
        var notes = new List<(string Path, string Content)>
        {
            ("z-last.md", "Orphan."),
            ("a.md", "[[overflow]]"),
        };
        for (var i = 0; i < 12; i++)
        {
            notes.Add(($"overflow{i:00}.md", string.Empty));
        }

        var graph = WikiLinkGraph.Build(notes, maxNodes: 10);

        graph.Nodes.Should().HaveCount(10);
        graph.Links.Should().BeEmpty(); // a.md and overflow* did not fit into the cap
    }

    [Fact]
    public void Build_ShouldReturnEmptyGraph_WhenNoNotes()
    {
        var graph = WikiLinkGraph.Build([]);

        graph.Nodes.Should().BeEmpty();
        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public void ExtractTargets_ShouldNormalizeWhitespaceAndSuffix()
    {
        var targets = WikiLinkGraph.ExtractTargets("x [[ Note .md | label ]] and [[a/b.md]]");

        targets.Should().Equal(new[] { "Note", "a/b" },
            because: "the '.md' suffix and surrounding whitespace are trimmed, interior spaces stay part of the name");
    }

    [Fact]
    public void ExtractTargets_ShouldIgnoreDegenerateLinks()
    {
        var targets = WikiLinkGraph.ExtractTargets("[[|x]] [[ ]] [[real]]");

        targets.Should().ContainSingle().Which.Should().Be("real");
    }
}
