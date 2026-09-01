using System.Text.Json.Serialization;

namespace Gitenberg.Web.Features.TelegramBot.Auth;

public record TelegramUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("username")] string? Username);
