using Microsoft.AspNetCore.Builder;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

public static class TelegramAuthEndpointExtensions
{
    public static RouteGroupBuilder RequireTelegramAuth(this RouteGroupBuilder group)
        => group.AddEndpointFilter<TelegramAuthFilter>();

    public static RouteHandlerBuilder RequireTelegramAuth(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<TelegramAuthFilter>();
}
