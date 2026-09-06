using System.Globalization;
using FluentAssertions;
using Gitenberg.Web.Features.Tasks;
using Xunit;

namespace Gitenberg.Tests.Features.Tasks;

public class TaskListBuilderTests
{
    [Fact]
    public void ParsesBullets_NumberedAndStar_CheckStates()
    {
        var content = """
            # День

            - [ ] Купить хлеб
            * [x] Полить цветы
            + [ ] Позвонить маме
            1. [ ] Шаг первый
            2. [x] Шаг второй
            - обычный пункт без чекбокса
            """;

        var tasks = TaskListBuilder.Parse("journal/day.md", content);

        tasks.Should().HaveCount(5);
        tasks[0].Text.Should().Be("Купить хлеб");
        tasks[0].Checked.Should().BeFalse();
        tasks[1].Checked.Should().BeTrue();
        tasks[3].Text.Should().Be("Шаг первый");
        tasks[4].Checked.Should().BeTrue();
    }

    [Fact]
    public void OccurrenceIndex_IsSequential_AcrossTheWholeNote()
    {
        var content = "- [ ] a\n\nтекст\n- [ ] b\n- [x] c";

        var tasks = TaskListBuilder.Parse("n.md", content);

        tasks.Select(t => t.OccurrenceIndex).Should().Equal([0, 1, 2]);
    }

    [Fact]
    public void SkipsTaskLines_InsideCodeFences()
    {
        var content = """
            - [ ] реальная задача

            ```bash
            - [ ] это код, не задача
            echo "- [x] тоже код"
            ```

            - [x] вторая реальная
            """;

        var tasks = TaskListBuilder.Parse("n.md", content);

        tasks.Should().HaveCount(2);
        tasks[0].Text.Should().Be("реальная задача");
        tasks[1].OccurrenceIndex.Should().Be(1);
        tasks[1].Checked.Should().BeTrue();
    }

    [Fact]
    public void NestedIndentedTasks_AreIncluded()
    {
        var content = "- [ ] родитель\n    - [ ] вложенный\n\t- [x] таб-вложенный";

        var tasks = TaskListBuilder.Parse("n.md", content);

        tasks.Should().HaveCount(3);
        tasks[1].Text.Should().Be("вложенный");
        tasks[2].Checked.Should().BeTrue();
    }

    [Fact]
    public void NoteName_StripsMdExtension()
    {
        TaskListBuilder.NoteName("inbox/2026-09-06_reminder.md").Should().Be("2026-09-06_reminder");
        TaskListBuilder.NoteName("notes/readme.txt").Should().Be("readme.txt");
    }
}
