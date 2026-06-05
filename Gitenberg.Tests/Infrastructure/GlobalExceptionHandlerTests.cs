using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Gitenberg.Tests.Infrastructure.FakeClasses;
using Gitenberg.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Octokit;
using Xunit;

namespace Gitenberg.Tests.Infrastructure;

public class GlobalExceptionHandlerTests
{
    private readonly FakeProblemDetailsService _problemDetailsService;
    private readonly FakeLogger<GlobalExceptionHandler> _logger;
    private readonly GlobalExceptionHandler _handler;

    public GlobalExceptionHandlerTests()
    {
        _problemDetailsService = new FakeProblemDetailsService();
        _logger = new FakeLogger<GlobalExceptionHandler>();
        _handler = new GlobalExceptionHandler(_problemDetailsService, _logger);
    }

    [Theory]
    [InlineData(typeof(NotFoundException), StatusCodes.Status404NotFound, "Resource Not Found on GitHub")]
    [InlineData(typeof(KeyNotFoundException), StatusCodes.Status404NotFound, "Requested Key Not Found")]
    [InlineData(typeof(AuthorizationException), StatusCodes.Status401Unauthorized, "GitHub Authorization Failed")]
    [InlineData(typeof(CryptographicException), StatusCodes.Status401Unauthorized, "Token Decryption Failed")]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest, "Invalid Argument")]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status400BadRequest, "Invalid Operation")]
    [InlineData(typeof(Exception), StatusCodes.Status500InternalServerError, "Internal Server Error")]
    public async Task TryHandleAsync_ShouldMapExceptionsToCorrectStatusCodeAndTitle(Type exceptionType, int expectedStatusCode, string expectedTitle)
    {
        // Arrange
        var context = new DefaultHttpContext();
        Exception exception;

        if (exceptionType == typeof(NotFoundException))
        {
            exception = new NotFoundException("Not found", HttpStatusCode.NotFound);
        }
        else if (exceptionType == typeof(AuthorizationException))
        {
            exception = new AuthorizationException();
        }
        else
        {
            exception = (Exception)Activator.CreateInstance(exceptionType, "Test error message")!;
        }

        // Act
        var result = await _handler.TryHandleAsync(context, exception, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        context.Response.StatusCode.Should().Be(expectedStatusCode);
        _problemDetailsService.WrittenContext.Should().NotBeNull();
        _problemDetailsService.WrittenContext!.ProblemDetails.Status.Should().Be(expectedStatusCode);
        _problemDetailsService.WrittenContext!.ProblemDetails.Title.Should().Be(expectedTitle);
        _problemDetailsService.WrittenContext!.ProblemDetails.Detail.Should().Be(exception.Message);
    }
}
