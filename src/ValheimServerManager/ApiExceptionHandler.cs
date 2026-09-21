using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ValheimServerManager;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, Services.AuditService audit) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            ArgumentException or InvalidDataException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            TimeoutException or TaskCanceledException => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError
        };
        if (status >= 500) logger.LogError(exception, "Request {CorrelationId} failed", context.TraceIdentifier);
        else logger.LogWarning(exception, "Request {CorrelationId} was rejected", context.TraceIdentifier);
        if (context.Request.Path.StartsWithSegments("/api/v1") && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            try { await audit.Write("api.failure", context.Request.Path, "failure", exception.Message, context.TraceIdentifier); }
            catch (Exception auditError) { logger.LogError(auditError, "Could not audit failed request {CorrelationId}", context.TraceIdentifier); }
        }
        context.Response.StatusCode = status;
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status == 500 ? "The request failed." : exception.Message,
            Detail = status == 500 ? "See the server log with the supplied correlation ID." : exception.Message,
            Extensions = { ["correlationId"] = context.TraceIdentifier }
        }, cancellationToken);
        return true;
    }
}
