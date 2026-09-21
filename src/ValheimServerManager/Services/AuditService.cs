using System.Security.Claims;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed class AuditService(IServiceScopeFactory scopes, IHttpContextAccessor accessor)
{
    public async Task Write(string action, string target, string result = "success", string detail = "", string? correlationId = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "system",
            Action = action,
            Target = target,
            Result = result,
            Detail = detail,
            CorrelationId = correlationId ?? accessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync();
    }
}

