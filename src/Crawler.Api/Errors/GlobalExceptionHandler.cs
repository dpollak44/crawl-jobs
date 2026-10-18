using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Errors;

/// <summary>
/// Last line of defence. Every unhandled exception becomes a Problem Details response with a plain-English
/// message and a reference id the user can quote, and one structured error log line carrying that id.
/// Stack traces never reach the client.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var reference = httpContext.TraceIdentifier;
        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {Reference} was abandoned by the client", reference); // nothing to tell, nobody to tell it to
            return true;
        }

        var (status, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "This record was changed by someone else",
                "Reload it and apply your change again."),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest,
                "The request was cancelled",
                "The client went away before we finished."),
            _ => (StatusCodes.Status500InternalServerError,
                "Something went wrong on our side",
                $"Nothing you did caused this. Quote reference {reference} if you contact support."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception {Reference} on {Method} {Path}", reference, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Handled failure {Reference} ({Status}) on {Method} {Path}", reference, status, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
            Extensions = { ["reference"] = reference },
        }, cancellationToken);

        return true;
    }
}
