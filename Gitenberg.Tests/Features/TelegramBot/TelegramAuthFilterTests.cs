using System.Text.Json;
using FluentAssertions;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gitenberg.Tests.Features.TelegramBot;

public class TelegramAuthFilterTests
{
    private const string TestBotToken = "123456:TEST-TOKEN";

    private readonly TelegramAuthValidator _validator = new(
        TestBotToken, NullLogger<TelegramAuthValidator>.Instance);

    private static string BuildValidInitData(long userId = 12345)
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var userJson = JsonSerializer.Serialize(new TelegramUser(userId, "Ivan", null, "ivan_test"));
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("auth_date", authDate.ToString()),
            new("user", userJson),
        };

        var dataCheckString = string.Join(
            '\n',
            pairs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        var secretKey = System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("WebAppData"),
            System.Text.Encoding.UTF8.GetBytes(TestBotToken));
        var hash = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(secretKey, System.Text.Encoding.UTF8.GetBytes(dataCheckString)))
            .ToLowerInvariant();

        pairs.Add(new("hash", hash));
        return string.Join('&', pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static async Task<(object? FilterResult, bool NextInvoked, HttpContext HttpContext)> InvokeAsync(
        string? authorizationHeader,
        string environmentName = "Development",
        bool setHeader = true)
    {
        var httpContext = new DefaultHttpContext();
        if (setHeader && authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }

        var environment = new FakeWebHostEnvironment { EnvironmentName = environmentName };
        var filter = new TelegramAuthFilter(
            new TelegramAuthValidator(TestBotToken, NullLogger<TelegramAuthValidator>.Instance),
            environment,
            NullLogger<TelegramAuthFilter>.Instance);

        var nextInvoked = false;
        EndpointFilterDelegate next = _ =>
        {
            nextInvoked = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        var invocationContext = EndpointFilterInvocationContext.Create(httpContext);
        var filterResult = await filter.InvokeAsync(invocationContext, next);

        return (filterResult, nextInvoked, httpContext);
    }

    [Theory]
    [InlineData("tma")]
    [InlineData("Bearer")]
    [InlineData("")]
    public async Task InvokeAsync_WithValidInitData_SetsTelegramUserIdAndInvokesNext(string scheme)
    {
        var initData = BuildValidInitData(userId: 777);
        var header = scheme.Length == 0 ? initData : $"{scheme} {initData}";

        var (filterResult, nextInvoked, httpContext) = await InvokeAsync(header);

        nextInvoked.Should().BeTrue();
        filterResult.Should().NotBeNull();
        httpContext.Items[TelegramAuthFilter.ItemsKey].Should().Be(777L);
    }

    // Results.Json wraps the payload in Json<TValue>; reading the value via
    // reflection avoids wiring up the JSON response DI services the Execute
    // path would need.
    private static string ExtractErrorValue(object? filterResult)
    {
        var value = filterResult!.GetType().GetProperty("Value")?.GetValue(filterResult);
        return (string?)value?.GetType().GetProperty("Error")?.GetValue(value) ?? string.Empty;
    }

    [Fact]
    public async Task InvokeAsync_WithTamperedInitData_ReturnsUnauthorizedAndDoesNotInvokeNext()
    {
        var initData = BuildValidInitData(userId: 777);
        var tampered = initData.Replace("ivan_test", "hacker");

        var (filterResult, nextInvoked, httpContext) = await InvokeAsync($"tma {tampered}");

        nextInvoked.Should().BeFalse();
        var statusCodeResult = filterResult as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        httpContext.Items.ContainsKey(TelegramAuthFilter.ItemsKey).Should().BeFalse();

        // The 401 carries a descriptive body so the client can show a
        // meaningful message instead of a bare "HTTP 401:".
        ExtractErrorValue(filterResult).Should().StartWith("Telegram authorization failed:");
    }

    [Fact]
    public async Task InvokeAsync_WithMalformedAuthorizationHeader_ReturnsUnauthorized()
    {
        var (filterResult, nextInvoked, _) = await InvokeAsync("Basic dXNlcjpwYXNzd29yZA==");

        nextInvoked.Should().BeFalse();
        var statusCodeResult = filterResult as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task InvokeAsync_WithMissingAuthorizationHeader_BehavesPerEnvironment(string environmentName)
    {
        var (filterResult, nextInvoked, httpContext) = await InvokeAsync(null, environmentName, setHeader: false);

        if (environmentName == "Development")
        {
            nextInvoked.Should().BeTrue();
            httpContext.Items.ContainsKey(TelegramAuthFilter.ItemsKey).Should().BeFalse();
        }
        else
        {
            nextInvoked.Should().BeFalse();
            var statusCodeResult = filterResult as IStatusCodeHttpResult;
            statusCodeResult.Should().NotBeNull();
            statusCodeResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

            ExtractErrorValue(filterResult).Should().Contain("Authorization header is missing");
        }
    }

    // Results.Json serializes with Web defaults (camelCase); older payloads in
    // this app use PascalCase, so accept either key name.
    private static string GetErrorProperty(JsonElement root)
    {
        if (root.TryGetProperty("error", out var camel)) return camel.GetString()!;
        return root.GetProperty("Error").GetString()!;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
    }
}
