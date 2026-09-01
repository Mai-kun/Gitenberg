using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

/// <summary>
/// Endpoint filter that authenticates Telegram Mini App requests via signed <c>initData</c>
/// supplied in the Authorization header ("tma &lt;initData&gt;", "Bearer &lt;initData&gt;" or raw).
/// On success the verified user id is stored in <see cref="HttpContext.Items"/> under
/// <see cref="ItemsKey"/>. In Development, requests without an Authorization header are allowed
/// through so the legacy X-Telegram-Id / telegramId fallback keeps working; Production requires
/// signed initData.
/// </summary>
public sealed class TelegramAuthFilter(
    ITelegramAuthValidator validator,
    IWebHostEnvironment environment,
    ILogger<TelegramAuthFilter> logger
) : IEndpointFilter
{
    public const string ItemsKey = "TelegramUserId";

    private const string TmaScheme = "tma";
    private const string BearerScheme = "Bearer";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            if (environment.IsDevelopment())
            {
                return await next(context);
            }

            return Results.Unauthorized();
        }

        var initData = ExtractInitData(authorizationHeader);
        var result = validator.Validate(initData);
        if (!result.IsValid || result.User is null)
        {
            logger.LogWarning("Telegram initData validation failed: {Error}", result.Error);
            return Results.Unauthorized();
        }

        httpContext.Items[ItemsKey] = result.User.Id;
        return await next(context);
    }

    private static string ExtractInitData(string authorizationHeader)
    {
        var separatorIndex = authorizationHeader.IndexOf(' ');
        if (separatorIndex < 0)
        {
            return authorizationHeader;
        }

        var scheme = authorizationHeader[..separatorIndex];
        if (scheme.Equals(TmaScheme, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader[(separatorIndex + 1)..].Trim();
        }

        // Raw initData contains no spaces, so a value with spaces but an unknown scheme is
        // simply invalid and will fail validation.
        return authorizationHeader;
    }
}
