using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Gitenberg.Web.Features.Auth;

// Cookie-session authentication for the web app. The development bypass
// (requests without a valid session proceed unauthenticated) mirrors the old
// Telegram filter's behavior so local API testing stays friction-free; the
// endpoints themselves reject with 400 when no user id was resolved.
public sealed class WebAuthFilter(
    WebAuthService sessions,
    WebAuthConfiguration config,
    IWebHostEnvironment environment,
    ILogger<WebAuthFilter> logger
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var cookie = httpContext.Request.Cookies[config.CookieName];

        var userId = await sessions.ResolveSessionAsync(cookie, TimeSpan.FromDays(config.SessionLifetimeDays));
        if (userId != null)
        {
            httpContext.Items[CurrentUserId.ItemsKey] = userId.Value;
            return await next(context);
        }

        if (environment.IsDevelopment())
        {
            logger.LogWarning("Auth bypassed in development mode for request to {Path}. Authentication is disabled for testing purposes.",
                httpContext.Request.Path);
            return await next(context);
        }

        return Results.Json(
            new { Error = "Not signed in: open the app and sign in with your GitHub token." },
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
