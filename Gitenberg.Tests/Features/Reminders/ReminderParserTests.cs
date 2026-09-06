using System.Globalization;
using FluentAssertions;
using Gitenberg.Web.Features.Reminders;
using Xunit;

namespace Gitenberg.Tests.Features.Reminders;

public class ReminderParserTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime ExpectLocal(int year, int month, int day, int hour, int minute) =>
        DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Local).ToUniversalTime();

    [Fact]
    public void TwoTokenDateTime_KeepsTimeOutOfText()
    {
        var result = ReminderParser.TryParse("2026-09-10 15:00 Купить молоко", NowUtc);

        result.FireAtUtc.Should().Be(ExpectLocal(2026, 9, 10, 15, 0));
        result.Text.Should().Be("Купить молоко");
    }

    [Fact]
    public void TwoTokenRuDateTime_KeepsTimeOutOfText()
    {
        var result = ReminderParser.TryParse("10.09.2026 15:00 Позвонить врачу", NowUtc);

        result.FireAtUtc.Should().Be(ExpectLocal(2026, 9, 10, 15, 0));
        result.Text.Should().Be("Позвонить врачу");
    }

    [Fact]
    public void RelativeHours_ParsesWithText()
    {
        var result = ReminderParser.TryParse("2h Позвонить врачу", NowUtc);

        result.FireAtUtc.Should().Be(NowUtc.AddHours(2));
        result.Text.Should().Be("Позвонить врачу");
    }

    [Theory]
    [InlineData("30м", 30)]
    [InlineData("15мин", 15)]
    [InlineData("1д", 1440)]
    [InlineData("2нед", 20160)]
    [InlineData("1н", 10080)]
    public void RelativeRussianUnits_AreSupported(string input, int expectedMinutes)
    {
        var result = ReminderParser.TryParse(input, NowUtc);

        result.FireAtUtc.Should().Be(NowUtc.AddMinutes(expectedMinutes));
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public void RelativeSpacedUnit_IsSupported()
    {
        var result = ReminderParser.TryParse("2 ч Собрание", NowUtc);

        result.FireAtUtc.Should().Be(NowUtc.AddHours(2));
        result.Text.Should().Be("Собрание");
    }

    [Fact]
    public void DateOnly_DefaultsToNineAm()
    {
        var result = ReminderParser.TryParse("2026-09-10", NowUtc);

        result.FireAtUtc.Should().Be(ExpectLocal(2026, 9, 10, 9, 0));
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public void TimeOnly_UsesTodayOrTomorrow_ByLocalNow()
    {
        var nowLocal = NowUtc.ToLocalTime();
        var candidate = nowLocal.Date.AddHours(15);
        var expectedDay = candidate <= nowLocal ? candidate.AddDays(1) : candidate;

        var result = ReminderParser.TryParse("15:00 Чай", NowUtc);

        result.FireAtUtc.Should().Be(ExpectLocal(expectedDay.Year, expectedDay.Month, expectedDay.Day, 15, 0));
        result.Text.Should().Be("Чай");
    }

    [Fact]
    public void TimeOnly_RollsToTomorrow_WhenAlreadyPast()
    {
        var referenceLocal = NowUtc.ToLocalTime().Date.AddHours(23);
        var referenceUtc = DateTime.SpecifyKind(referenceLocal, DateTimeKind.Local).ToUniversalTime();

        var result = ReminderParser.TryParse("00:00", referenceUtc);

        var expectedDay = referenceLocal.Date.AddDays(1);
        result.FireAtUtc.Should().Be(ExpectLocal(expectedDay.Year, expectedDay.Month, expectedDay.Day, 0, 0));
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public void GarbageInput_ReturnsError()
    {
        var result = ReminderParser.TryParse("завтра как-нибудь", NowUtc);

        result.FireAtUtc.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void EmptyInput_ReturnsError()
    {
        var result = ReminderParser.TryParse("", NowUtc);

        result.FireAtUtc.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void NoteTitleExtractor_FallsBackToFileName()
    {
        ReminderService.ExtractNoteTitle(null, "inbox/2026-09-06_reminder.md").Should().Be("2026-09-06_reminder");
        ReminderService.ExtractNoteTitle("# Заголовок\n\nтекст", "inbox/note.md").Should().Be("Заголовок");
    }
}
