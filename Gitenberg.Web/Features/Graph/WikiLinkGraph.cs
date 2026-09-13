using System.Text.RegularExpressions;

namespace Gitenberg.Web.Features.Graph;

public sealed record GraphNode(string Path, string Name, int Degree);

public sealed record GraphLink(string Source, string Target);

public sealed record WikiGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphLink> Links);

// Turns vault notes into a link graph. [[WikiLink]] targets are resolved to
// existing notes the way the browser preview does it: a path match first,
// then a vault-wide match on the file name (shortest path wins when several
// folders hold the same name). Targets that resolve to nothing are dropped.
public static partial class WikiLinkGraph
{
    [GeneratedRegex(@"\[\[([^\]<>\n]+?)\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceRegex();

    public static WikiGraph Build(IEnumerable<(string Path, string Content)> notes, int maxNodes = 1000)
    {
        var paths = new List<string>();
        var targetsByNote = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, content) in notes)
        {
            paths.Add(path);
            targetsByNote[path] = ExtractTargets(content);
        }

        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            byPath.TryAdd(path, path);
            byPath.TryAdd(TrimMd(path), path);

            var name = NoteName(path);
            if (!byName.TryGetValue(name, out var candidates))
            {
                candidates = [];
                byName[name] = candidates;
            }
            candidates.Add(path);
        }

        string? Resolve(string target)
        {
            if (byPath.TryGetValue(target, out var direct))
            {
                return direct;
            }

            // A target carrying a folder separator only ever matches a path;
            // a bare name may match the file name anywhere in the vault.
            if (target.Contains('/') || !byName.TryGetValue(target, out var candidates))
            {
                return null;
            }
            return candidates.MinBy(p => p.Length);
        }

        var degree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var edges = new HashSet<(string Source, string Target)>();
        foreach (var (path, targets) in targetsByNote)
        {
            foreach (var target in targets)
            {
                var resolved = Resolve(target);
                if (resolved == null || string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (edges.Add((path, resolved)))
                {
                    degree[path] = degree.GetValueOrDefault(path) + 1;
                    degree[resolved] = degree.GetValueOrDefault(resolved) + 1;
                }
            }
        }

        var nodes = paths.OrderBy(p => p, StringComparer.Ordinal).Take(maxNodes).ToList();
        var nodeSet = new HashSet<string>(nodes, StringComparer.OrdinalIgnoreCase);
        var links = edges
            .Where(e => nodeSet.Contains(e.Source) && nodeSet.Contains(e.Target))
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .Select(e => new GraphLink(e.Source, e.Target))
            .ToList();

        return new WikiGraph(
            nodes.Select(p => new GraphNode(p, NoteName(p), degree.GetValueOrDefault(p))).ToList(),
            links
        );
    }

    // WikiLink targets ([[target]] or [[target|label]]) in one note body,
    // code fences skipped — the same occurrences the preview would render.
    public static List<string> ExtractTargets(string? content)
    {
        var targets = new List<string>();
        if (string.IsNullOrEmpty(content))
        {
            return targets;
        }

        var inFence = false;
        foreach (var line in content.Split('\n'))
        {
            if (FenceRegex().IsMatch(line))
            {
                inFence = !inFence;
            }
            if (inFence)
            {
                continue;
            }
            foreach (Match match in WikiLinkRegex().Matches(line))
            {
                var target = TrimMd(match.Groups[1].Value.Split('|')[0].Trim()).Trim();
                if (target.Length > 0)
                {
                    targets.Add(target);
                }
            }
        }
        return targets;
    }

    private static string TrimMd(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    private static string NoteName(string notePath)
    {
        var name = notePath.Trim('/').Split('/')[^1];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}
