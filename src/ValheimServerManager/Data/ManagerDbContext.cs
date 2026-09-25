using Microsoft.EntityFrameworkCore;

namespace ValheimServerManager.Data;

public sealed class AuditRecord
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "system";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Result { get; set; } = "success";
    public string CorrelationId { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Kind { get; set; } = "generic";
    public string Secret { get; set; } = "";
    public string EventTypesJson { get; set; } = "[]";
    public string Template { get; set; } = "**{{type}}** on {{server}}";
    public bool Enabled { get; set; }
    public bool AllowPrivateNetwork { get; set; }
    public int ConsecutiveFailures { get; set; }
}

public sealed class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ResponseStatus { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class InstalledMod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Namespace { get; set; } = "manual";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "manual";
    public string DependenciesJson { get; set; } = "[]";
    public string FilesJson { get; set; } = "[]";
    public string Sha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Protected { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ManagerSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class JoinRequestRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlatformId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; } = 1;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string ResolvedBy { get; set; } = "";
    public string CorrelationId { get; set; } = "";
}

public sealed class KnownPlayer
{
    public string PlatformId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ManagerDbContext(DbContextOptions<ManagerDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<InstalledMod> InstalledMods => Set<InstalledMod>();
    public DbSet<ManagerSetting> ManagerSettings => Set<ManagerSetting>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<JoinRequestRecord> JoinRequests => Set<JoinRequestRecord>();
    public DbSet<KnownPlayer> KnownPlayers => Set<KnownPlayer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ManagerSetting>().HasKey(x => x.Key);
        builder.Entity<AuditRecord>().HasIndex(x => x.OccurredAt);
        builder.Entity<WebhookDelivery>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        builder.Entity<InstalledMod>().HasIndex(x => new { x.Namespace, x.Name }).IsUnique();
        builder.Entity<ApiToken>().HasIndex(x => x.TokenHash).IsUnique();
        builder.Entity<JoinRequestRecord>().HasIndex(x => new { x.Status, x.LastAttemptAt });
        builder.Entity<KnownPlayer>().HasKey(x => x.PlatformId);
    }
}
