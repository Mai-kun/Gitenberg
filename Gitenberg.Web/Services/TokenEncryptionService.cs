using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Gitenberg.Web.Services;

public class TokenEncryptionService : ITokenEncryptionService
{
    private readonly ITimeLimitedDataProtector _protector;

    public TokenEncryptionService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
                     .CreateProtector("Gitenberg.TokenEncryption")
                     .ToTimeLimitedDataProtector();
    }

    public string EncryptToken(string token, TimeSpan lifetime)
    {
        if (string.IsNullOrEmpty(token))
        {
            return token;
        }

        return _protector.Protect(token, lifetime);
    }

    public string DecryptToken(string encryptedToken)
    {
        if (string.IsNullOrEmpty(encryptedToken))
        {
            return encryptedToken;
        }

        return _protector.Unprotect(encryptedToken);
    }
}