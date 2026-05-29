namespace Gitenberg.Web.Services.Abstractions;

public interface ITokenEncryptionService
{
    /// <summary>
    /// Encrypts the user token with a specific lifetime.
    /// </summary>
    /// <param name="token">The plaintext token.</param>
    /// <param name="lifetime">The lifetime of the token.</param>
    /// <returns>The encrypted token as a Base64 encoded string.</returns>
    string EncryptToken(string token, TimeSpan lifetime);

    /// <summary>
    /// Decrypts the encrypted user token.
    /// </summary>
    /// <param name="encryptedToken">The encrypted token as a Base64 encoded string.</param>
    /// <returns>The decrypted plaintext token.</returns>
    string DecryptToken(string encryptedToken);
}
