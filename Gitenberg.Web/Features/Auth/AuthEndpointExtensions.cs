using Microsoft.AspNetCore.Builder;

namespace Gitenberg.Web.Features.Auth;

public static class AuthEndpointExtensions
{
    public static RouteGroupBuilder RequireAuth(this RouteGroupBuilder group)
        => group.AddEndpointFilter<WebAuthFilter>();

    public static RouteHandlerBuilder RequireAuth(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<WebAuthFilter>();
}
