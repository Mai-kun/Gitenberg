using System.Security.Cryptography;
using FluentAssertions;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Gitenberg.Tests.Services;

public class TokenEncryptionServiceTests
{
    private readonly TokenEncryptionService _service;

    public TokenEncryptionServiceTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _service = new TokenEncryptionService(provider);
    }

    [Fact]
    public void EncryptToken_ShouldReturnProtectedDataWithMagicHeader_WhenTokenIsValid()
    {
        // Arrange
        const string token = "github_pat_1234567890abcdefghijklmnopqrstuvwxyz";

        // Act
        var encrypted = _service.EncryptToken(token, TimeSpan.FromMinutes(5));

        // Assert
        encrypted.Should().NotBeNullOrEmpty();
        encrypted.Should().StartWith("CfDJ8");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EncryptToken_ShouldReturnInput_WhenTokenIsNullOrEmpty(string? token)
    {
        // Act
        var encrypted = _service.EncryptToken(token!, TimeSpan.FromMinutes(5));

        // Assert
        encrypted.Should().Be(token);
    }

    [Fact]
    public void DecryptToken_ShouldRestoreOriginalToken_WhenEncryptedTokenIsValid()
    {
        // Arrange
        const string originalToken = "my-secret-github-token-value";
        var encrypted = _service.EncryptToken(originalToken, TimeSpan.FromMinutes(5));

        // Act
        var decrypted = _service.DecryptToken(encrypted);

        // Assert
        decrypted.Should().Be(originalToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DecryptToken_ShouldReturnInput_WhenEncryptedTokenIsNullOrEmpty(string? encryptedToken)
    {
        // Act
        var decrypted = _service.DecryptToken(encryptedToken!);

        // Assert
        decrypted.Should().Be(encryptedToken);
    }

    [Fact]
    public void EncryptToken_ShouldProduceDifferentCiphertext_ForSameInputMultipleTimes()
    {
        // Arrange
        const string token = "same-token-input";

        // Act
        var encrypted1 = _service.EncryptToken(token, TimeSpan.FromMinutes(5));
        var encrypted2 = _service.EncryptToken(token, TimeSpan.FromMinutes(5));

        // Assert
        encrypted1.Should().NotBe(
            encrypted2,
            "because a random Initialization Vector (IV) / salt should be generated for each encryption operation"
        );

        // Both should decrypt to the same value
        _service.DecryptToken(encrypted1).Should().Be(token);
        _service.DecryptToken(encrypted2).Should().Be(token);
    }

    [Fact]
    public void DecryptToken_ShouldThrowCryptographicException_WhenDecryptingWithDifferentKey()
    {
        // Arrange
        const string originalToken = "secret-token";

        var provider1 = new EphemeralDataProtectionProvider();
        var service1 = new TokenEncryptionService(provider1);

        var provider2 = new EphemeralDataProtectionProvider();
        var service2 = new TokenEncryptionService(provider2);

        var encrypted = service1.EncryptToken(originalToken, TimeSpan.FromMinutes(5));

        // Act
        Action act = () => service2.DecryptToken(encrypted);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void DecryptToken_ShouldThrowCryptographicException_WhenEncryptedTextIsMalformed()
    {
        // Arrange
        const string malformed = "invalid-token-string";

        // Act
        Action act = () => _service.DecryptToken(malformed);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void DecryptToken_ShouldThrowCryptographicException_WhenPayloadIsTampered()
    {
        // Arrange
        const string originalToken = "my-secret-token";
        var encrypted = _service.EncryptToken(originalToken, TimeSpan.FromMinutes(5));

        var tamperedChars = encrypted.ToCharArray();
        if (tamperedChars.Length > 0)
        {
            tamperedChars[^1] = tamperedChars[^1] == 'A' ? 'B' : 'A';
        }

        var tampered = new string(tamperedChars);

        // Act
        Action act = () => _service.DecryptToken(tampered);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task DecryptToken_ShouldThrowCryptographicException_WhenTokenHasExpired()
    {
        // Arrange
        const string originalToken = "temporary-token";

        var encrypted = _service.EncryptToken(originalToken, TimeSpan.FromSeconds(1));

        // Act & Assert
        await Task.Delay(1500);

        Action act = () => _service.DecryptToken(encrypted);
        act.Should().Throw<CryptographicException>();
    }
}