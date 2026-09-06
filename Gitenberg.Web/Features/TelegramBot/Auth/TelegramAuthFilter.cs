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

            // A body here (instead of a bare 401) lets the Mini App show a
            // meaningful message instead of a cryptic "HTTP 401:".
            return Results.Json(
                new { Error = "Telegram authorization failed: the Authorization header is missing. Open the Mini App from Telegram." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var initData = ExtractInitData(authorizationHeader);
        var result = validator.Validate(initData);
        if (!result.IsValid || result.User is null)
        {
            logger.LogWarning("Telegram initData validation failed: {Error}", result.Error);
            // The validator's reason (e.g. hash mismatch) points the operator
            // straight at the usual cause: a wrong or placeholder bot token.
            return Results.Json(
                new { Error = $"Telegram authorization failed: {result.Error}" },
                statusCode: StatusCodes.Status401Unauthorized);
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
