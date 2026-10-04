using Microsoft.AspNetCore.Diagnostics;
using Otantik.BuildingBlocks;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Api.ErrorHandling;

// Turns the exceptions the lower layers throw on purpose into the right status code, with the
// message as ProblemDetails.detail. Those messages are written to be shown at the till as they
// are ("Quantity must be between 1 and 100.", "Look the customer up by mobile number before
// applying points.").
//
// Anything else is a fault: logged here with a reference, and answered with only that
// reference, so no stack trace reaches a browser. On .NET 10 the exception middleware does not
// log what a handler has handled, so each failure is logged once, here.
internal sealed class ApplicationExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApplicationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Too late to send a different response; let the host drop the connection.
        if (httpContext.Response.HasStarted)
            return false;

        var (status, title, detail) = exception switch
        {
            DomainException => (StatusCodes.Status422UnprocessableEntity, "The request breaks a business rule.", exception.Message),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found.", exception.Message),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Not allowed.", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict.", exception.Message),

            // Customer lookup, points, a till that has never synced: things that need the cloud
            // while the internet is down. The till says so, and everything else keeps working.
            DeliverySystemUnavailableException => (StatusCodes.Status503ServiceUnavailable, "The delivery system cannot be reached.", exception.Message),

            // The till closed the tab or lost the network mid-request. Nothing to tell anyone.
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed request.", (string?)null),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", (string?)null),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            var reference = httpContext.TraceIdentifier;
            logger.LogError(exception, "Unhandled exception on {Method} {Path} (reference {Reference})",
                httpContext.Request.Method, httpContext.Request.Path, reference);
            detail = $"Something went wrong on our end. Please try again. (reference {reference})";
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }

    // Applied to every ProblemDetails the API writes: the ones above, controller Problem()
    // calls, and the bare 401/403/404s from UseStatusCodePages.
    //
    // Adds an `errors` array holding the detail: the shape the delivery system's API returns
    // and its Angular client reads (err.error?.errors?.[0]), so one error helper serves both.
    // Model-validation 400s keep their own `errors`, an object keyed by field, which that
    // client already handles.
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);

        problem.Detail ??= problem.Status switch
        {
            StatusCodes.Status401Unauthorized => "Sign in again.",
            StatusCodes.Status403Forbidden => "You are not allowed to do that.",
            StatusCodes.Status404NotFound => "Not found.",
            _ => null,
        };

        if (problem is not HttpValidationProblemDetails && problem.Detail is { } detail)
            problem.Extensions.TryAdd("errors", new[] { detail });
    }
}
