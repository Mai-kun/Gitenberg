namespace Gitenberg.Web.Services.Abstractions;

public interface ITokenEncryptionService
{
    string EncryptToken(string token, TimeSpan lifetime);

    string DecryptToken(string encryptedToken);
}
