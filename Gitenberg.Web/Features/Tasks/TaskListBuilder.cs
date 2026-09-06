using System.Text.RegularExpressions;

namespace Gitenberg.Web.Features.Tasks;

public sealed record NoteTask(string NotePath, string NoteName, int Line, int OccurrenceIndex, string Text, bool Checked);

/// <summary>
/// Extracts markdown task-list items ("- [ ]", "- [x]", "*", "+" and ordered
/// list checkboxes) from a note. Lines inside ``` / ~~~ code fences are
/// skipped. OccurrenceIndex is the 0-based ordinal of the checkbox within the
/// whole note and is the stable handle the frontend flips.
/// </summary>
public static partial class TaskListBuilder
{
    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceRegex();

    public static List<NoteTask> Parse(string notePath, string? content)
    {
        var tasks = new List<NoteTask>();
        if (string.IsNullOrEmpty(content))
        {
            return tasks;
        }

        var name = NoteName(notePath);
        var occurrence = 0;
        var inFence = false;
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (FenceRegex().IsMatch(lines[i]))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                continue;
            }

            var match = TaskLineRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            tasks.Add(new NoteTask(
                notePath,
                name,
                i,
                occurrence++,
                match.Groups[2].Value.TrimEnd(),
                !match.Groups[1].Value.Equals(" ", StringComparison.Ordinal)
            ));
        }

        return tasks;
    }

    public static string NoteName(string notePath)
    {
        var name = notePath.Trim('/').Split('/')[^1];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}
