using FluentAssertions;
using Gitenberg.Web.Features.TelegramBot;
using Xunit;

namespace Gitenberg.Tests.Features.TelegramBot;

public class InlineFileLinkServiceTests
{
    private readonly BotConfiguration _botConfig = new()
    {
        HostAddress = "https://bot.test",
        SecretToken = "webhook-secret",
    };

    private InlineFileLinkService CreateService(BotConfiguration? config = null) =>
        new(config ?? _botConfig);

    [Fact]
    public void TryValidate_ShouldAcceptALinkSignedForTheSameUserPathFormatAndExpiry()
    {
        var service = CreateService();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = service.CreateSignature(42, "notes/kernel.md", expiresAt, InlineFileLinkService.ZipFormat);

        service.TryValidate(42, "notes/kernel.md", expiresAt, InlineFileLinkService.ZipFormat, signature)
            .Should().BeTrue();
    }

    [Fact]
    public void TryValidate_ShouldRejectAWrongFormat()
    {
        var service = CreateService();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = service.CreateSignature(42, "notes/kernel.md", expiresAt, InlineFileLinkService.ZipFormat);

        service.TryValidate(42, "notes/kernel.md", expiresAt, InlineFileLinkService.MarkdownFormat, signature)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_ShouldRejectATamperedPath()
    {
        var service = CreateService();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = service.CreateSignature(42, "notes/kernel.md", expiresAt);

        service.TryValidate(42, "notes/secret.md", expiresAt, InlineFileLinkService.MarkdownFormat, signature)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_ShouldRejectATamperedUser()
    {
        var service = CreateService();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = service.CreateSignature(42, "notes/kernel.md", expiresAt);

        service.TryValidate(43, "notes/kernel.md", expiresAt, InlineFileLinkService.MarkdownFormat, signature)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_ShouldRejectAnExpiredLink()
    {
        var service = CreateService();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var signature = service.CreateSignature(42, "notes/kernel.md", expiresAt);

        service.TryValidate(42, "notes/kernel.md", expiresAt, InlineFileLinkService.MarkdownFormat, signature)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_ShouldRejectWhenSignatureIsMissing()
    {
        CreateService().TryValidate(42, "notes/kernel.md", long.MaxValue, InlineFileLinkService.MarkdownFormat, null)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_ShouldRejectLinksSignedWithADifferentKey()
    {
        var signer = CreateService();
        var validator = CreateService(new BotConfiguration { SecretToken = "another-secret" });

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = signer.CreateSignature(42, "notes/kernel.md", expiresAt);

        validator.TryValidate(42, "notes/kernel.md", expiresAt, InlineFileLinkService.MarkdownFormat, signature)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(InlineFileLinkService.MarkdownFormat)]
    [InlineData(InlineFileLinkService.ZipFormat)]
    public void BuildFileUrl_ShouldProduceAValidatableUrl(string format)
    {
        var service = CreateService();
        var url = service.BuildFileUrl(42, "notes/kernel.md", format, _botConfig.HostAddress);

        url.Should().StartWith("https://bot.test/api/bot/inline-file?uid=42&");

        var query = new Uri(url).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty);

        var path = Uri.UnescapeDataString(query["path"]);
        service.TryValidate(42, path, long.Parse(query["exp"]), format, query["sig"]).Should().BeTrue();
    }
}
