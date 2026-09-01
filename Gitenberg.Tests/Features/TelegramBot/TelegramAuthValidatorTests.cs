using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Xunit;

namespace Gitenberg.Tests.Features.TelegramBot;

public class TelegramAuthValidatorTests
{
    private const string TestBotToken = "123456:TEST-TOKEN";

    private readonly TelegramAuthValidator _validator = new(TestBotToken);

    /// <summary>
    /// Builds signed initData exactly the way Telegram signs it: URL-encode all values,
    /// sort the remaining keys alphabetically and HMAC-SHA256 the data check string with
    /// the WebAppData-derived secret key.
    /// </summary>
    private static string BuildInitData(
        long userId = 12345,
        string? firstName = "Ivan",
        string? lastName = null,
        string? username = "ivan_test",
        DateTimeOffset? authDate = null,
        string? hashOverride = null,
        bool includeSignature = false,
        string? botToken = TestBotToken)
    {
        var actualAuthDate = authDate ?? DateTimeOffset.UtcNow;
        var userJson = JsonSerializer.Serialize(new TelegramUser(userId, firstName, lastName, username));

        var pairs = new List<KeyValuePair<string, string>>
        {
            new("auth_date", ((long)actualAuthDate.ToUnixTimeSeconds()).ToString()),
            new("query_id", "AAF9bE0aAAAAAHl1TYtSXfRb"),
            new("user", userJson),
        };
        if (includeSignature)
        {
            pairs.Add(new("signature", "dummy_signature_value"));
        }

        var dataCheckString = string.Join(
            '\n',
            pairs
                .Where(kv => kv.Key != "signature")
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(botToken!));
        var hash = Convert.ToHexString(
            HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString))).ToLowerInvariant();

        pairs.Add(new("hash", hashOverride ?? hash));

        return string.Join('&', pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    [Fact]
    public void Validate_WithValidInitData_ReturnsTrueAndParsesUser()
    {
        var initData = BuildInitData(userId: 42, firstName: "Иван", username: "ivan_test");

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
        result.User.Should().NotBeNull();
        result.User!.Id.Should().Be(42);
        result.User.FirstName.Should().Be("Иван");
        result.User.LastName.Should().BeNull();
        result.User.Username.Should().Be("ivan_test");
    }

    [Fact]
    public void Validate_WithSignatureParameter_StillReturnsTrue()
    {
        var initData = BuildInitData(includeSignature: true);

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithTamperedUser_ReturnsFalse()
    {
        var initData = BuildInitData(userId: 12345);

        // Replace the signed user with a different one while keeping the original hash.
        var tamperedUserJson = JsonSerializer.Serialize(new TelegramUser(99999, "Fake", null, "fake"));
        var tamperedInitData = ReplaceValue(initData, "user", tamperedUserJson);

        var result = _validator.Validate(tamperedInitData);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithWrongHash_ReturnsFalse()
    {
        var wrongHash = new string('a', 64);
        var initData = BuildInitData(hashOverride: wrongHash);

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithNonHexHash_ReturnsFalse()
    {
        var initData = BuildInitData(hashOverride: "not-hex-at-all");

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithWrongBotToken_ReturnsFalse()
    {
        var initData = BuildInitData(botToken: "654321:OTHER-TOKEN");

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithExpiredAuthDate_ReturnsFalse()
    {
        var initData = BuildInitData(authDate: DateTimeOffset.UtcNow.AddHours(-25));

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    public void Validate_WithAuthDateInsideWindow_ReturnsTrue(int hoursAgo)
    {
        var initData = BuildInitData(authDate: DateTimeOffset.UtcNow.AddHours(-hoursAgo));

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMissingHash_ReturnsFalse()
    {
        var initData = BuildInitData();
        var withoutHash = string.Join(
            '&',
            initData.Split('&').Where(part => !part.StartsWith("hash=", StringComparison.Ordinal)));

        var result = _validator.Validate(withoutHash);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithMissingUser_ReturnsFalse()
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("auth_date", ((long)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString()),
            new("query_id", "AAF9bE0aAAAAAHl1TYtSXfRb"),
        };

        var dataCheckString = string.Join(
            '\n',
            pairs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("WebAppData"),
            Encoding.UTF8.GetBytes(TestBotToken));
        var hash = Convert.ToHexString(
            HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString))).ToLowerInvariant();

        pairs.Add(new("hash", hash));
        var initData = string.Join('&', pairs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithMissingAuthDate_ReturnsFalse()
    {
        var initData = BuildInitData();
        var withoutAuthDate = string.Join(
            '&',
            initData.Split('&').Where(part => !part.StartsWith("auth_date=", StringComparison.Ordinal)));

        var result = _validator.Validate(withoutAuthDate);

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-init-data")]
    public void Validate_WithGarbageInput_ReturnsFalse(string initData)
    {
        var result = _validator.Validate(initData);

        result.IsValid.Should().BeFalse();
        result.User.Should().BeNull();
    }

    private static string ReplaceValue(string initData, string key, string newValue)
    {
        var parts = initData.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var separatorIndex = parts[i].IndexOf('=');
            if (separatorIndex >= 0
                && Uri.UnescapeDataString(parts[i][..separatorIndex]) == key)
            {
                parts[i] = $"{parts[i][..separatorIndex]}={Uri.EscapeDataString(newValue)}";
                break;
            }
        }

        return string.Join('&', parts);
    }
}
