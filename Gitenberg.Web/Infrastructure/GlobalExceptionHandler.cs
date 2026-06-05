using System.Security.Cryptography;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Octokit;

namespace Gitenberg.Web.Infrastructure;

public class GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

        var (statusCode, title) = exception switch
        {
            NotFoundException
                => (StatusCodes.Status404NotFound, "Resource Not Found on GitHub"),

            KeyNotFoundException
                => (StatusCodes.Status404NotFound, "Requested Key Not Found"),

            AuthorizationException
                => (StatusCodes.Status401Unauthorized, "GitHub Authorization Failed"),

            CryptographicException
                => (StatusCodes.Status401Unauthorized, "Token Decryption Failed"),

            ArgumentException
                => (StatusCodes.Status400BadRequest, "Invalid Argument"),

            InvalidOperationException
                => (StatusCodes.Status400BadRequest, "Invalid Operation"),

            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
        };

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = title,
                    Detail = exception.Message,
                    Instance = httpContext.Request.Path,
                },
            }
        );
    }
}