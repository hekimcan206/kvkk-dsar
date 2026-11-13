using KvkkDsar.Api.Services;
using KvkkDsar.Workflow;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace KvkkDsar.Api.Infrastructure;

/// <summary>
/// Maps expected failures to problem details: a step the workflow does not allow is a 409 with a machine-readable code,
/// bad input a 400, an unknown request a 404, lock waits and outages a retryable 503. Anything else stays a 500 without
/// internals in the body (and without identifiers in the log: requests are logged by id and reference only).
/// </summary>
internal sealed class DsarExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail, code) = exception switch
        {
            WorkflowException w => (StatusCodes.Status409Conflict, "The request's current state does not allow this step.", w.Message, w.Code),
            DsarValidationException v => (StatusCodes.Status400BadRequest, "Invalid application.", string.Join(" ", v.Errors), "validation"),
            KeyNotFoundException k => (StatusCodes.Status404NotFound, "Not found.", k.Message, "not_found"),
            PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected } =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry.", null, "retry"),
            NpgsqlException { IsTransient: true } or TimeoutException =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry.", null, "retry"),
            _ => (0, null, null, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail, Extensions = { ["code"] = code } },
            Exception = exception,
        });
    }
}
