using System.Globalization;
using System.Security.Claims;
using FleetProof.Shared;
using FleetProof.SubscriptionManagement;
using FleetProof.VehicleInformation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FleetProof.VehicleMonitoring;

public sealed class VehicleWatch : IOwnedDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AccountId { get; init; }
    public Guid VehicleId { get; init; }
    public bool Active { get; set; }
    public string Data { get; set; } = "{}";
}

public sealed class MonitoringAlert : IOwnedDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AccountId { get; init; }
    public string Data { get; set; } = "{}";
}

/// <summary>One accepted check of a monitoring. Rows are only added; the history is never rewritten.</summary>
public sealed class MonitoringCheck
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AccountId { get; init; }
    public Guid MonitoringId { get; init; }
    public DateTime CheckedAt { get; init; }
    public string Result { get; init; } = "";
}

internal static class MonitoringEndpoints
{
    private const string CheckHistoryIndex = "IX_monitoring_checks_monitoring_id_checked_at";
    private const int MaxChecksListed = 50;

    private static class CheckResult
    {
        public const string NoChanges = "no_changes";
        public const string ChangeDetected = "change_detected";
    }

    internal static void ConfigureMonitoring(this ModelBuilder builder)
    {
        builder.ConfigureDocument<VehicleWatch>("vehicle_monitorings");
        builder.ConfigureDocument<MonitoringAlert>("monitoring_alerts");
        var entity = builder.Entity<VehicleWatch>();
        entity.Property(x => x.VehicleId).HasColumnName("vehicle_id");
        entity.Property(x => x.Active).HasColumnName("active");
        entity.HasIndex(x => new { x.AccountId, x.VehicleId }).IsUnique().HasFilter("active");
        entity.HasOne<Vehicle>().WithMany().HasForeignKey(x => new { x.AccountId, x.VehicleId })
            .HasPrincipalKey(x => new { x.AccountId, x.Id });
        entity.HasAlternateKey(x => new { x.AccountId, x.Id });

        var check = builder.Entity<MonitoringCheck>();
        check.ToTable("monitoring_checks");
        check.Property(x => x.Id).HasColumnName("id");
        check.Property(x => x.AccountId).HasColumnName("user_id");
        check.Property(x => x.MonitoringId).HasColumnName("monitoring_id");
        check.Property(x => x.CheckedAt).HasColumnName("checked_at").HasColumnType("timestamp with time zone");
        check.Property(x => x.Result).HasColumnName("result").HasMaxLength(20);
        check.HasIndex(x => new { x.MonitoringId, x.CheckedAt }).IsUnique().HasDatabaseName(CheckHistoryIndex);
        // The composite key ties every check to a monitoring of the same account.
        check.HasOne<VehicleWatch>().WithMany().HasForeignKey(x => new { x.AccountId, x.MonitoringId })
            .HasPrincipalKey(x => new { x.AccountId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void MapMonitoring(this RouteGroupBuilder api)
    {
        var watches = api.MapGroup("/monitorings").RequireAuthorization();
        watches.MapGet("", (ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct) =>
            db.List<VehicleWatch, Watch>(principal.AccountId(), ct));
        watches.MapPost("", (Watch input, ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct) =>
            Save(null, input, principal, db, ct)).Produces<Watch>(201);
        watches.MapPut("/{id:guid}", (Guid id, Watch input, ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct) =>
            Save(id, input, principal, db, ct)).Produces<Watch>();
        watches.MapGet("/{id:guid}/checks", ListChecks).Produces<CheckItem[]>().Produces(404);

        var alerts = api.MapGroup("/alerts").RequireAuthorization();
        alerts.MapGet("", (ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct) =>
            db.List<MonitoringAlert, Alert>(principal.AccountId(), ct));
        alerts.MapPost("", RecordAlert).Produces<Alert>(201);
    }

    private static async Task<IResult> ListChecks(Guid id, ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct)
    {
        var accountId = principal.AccountId();
        // The monitoring is looked up by id and by the account in the token, so another account gets a 404.
        if (!await db.Owned<VehicleWatch>(accountId).AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
        var checks = await db.Set<MonitoringCheck>().AsNoTracking()
            .Where(x => x.AccountId == accountId && x.MonitoringId == id)
            .OrderByDescending(x => x.CheckedAt).ThenByDescending(x => x.Id)
            .Take(MaxChecksListed)
            .Select(x => new CheckItem(x.Id, x.CheckedAt, x.Result))
            .ToArrayAsync(ct);
        return Results.Ok(checks);
    }

    private static async Task<IResult> Save(Guid? id, Watch input, ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct)
    {
        if (input.IntervalMinutes is < 1 or > 525600 || input.Status is not ("active" or "inactive") ||
            input.VehicleId == Guid.Empty)
            return Results.BadRequest(new { message = "Invalid monitoring schedule" });

        var accountId = principal.AccountId();
        if (input.Status == "active" && !await db.HasPlan(accountId, "monitoring", ct))
            return SubscriptionEndpoints.PlanRequired();
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(
            v => v.AccountId == accountId && v.Id == input.VehicleId, ct);
        if (vehicle is null) return Results.NotFound();

        // An update locks its monitoring row until the commit. Two requests for the same monitoring
        // run one after the other, so a retry or a late check always sees the latest saved state.
        await using var transaction = id is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (id is Guid locked)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM vehicle_monitorings WHERE id = {locked} AND user_id = {accountId} FOR UPDATE", ct);
        var row = id is Guid existing
            ? await db.Owned<VehicleWatch>(accountId).SingleOrDefaultAsync(x => x.Id == existing, ct)
            : new VehicleWatch { AccountId = accountId, VehicleId = vehicle.Id };
        if (row is null) return Results.NotFound();
        if (row.VehicleId != vehicle.Id) return Results.BadRequest(new { message = "Vehicle cannot change" });

        var previous = id is null ? null : row.Read<Watch>();
        var now = DateTimeOffset.UtcNow;

        // A new check is an active monitoring that reports an instant later than its last accepted check.
        DateTime? checkedAt = null;
        if (previous is not null && input.Status == "active" && !string.IsNullOrEmpty(input.LastCheckedAt) &&
            input.LastCheckedAt != previous.LastCheckedAt)
        {
            if (!TryReadInstant(input.LastCheckedAt, out var instant))
                return Results.BadRequest(new { message = "Invalid check timestamp" });
            DateTime? latest = TryReadInstant(previous.LastCheckedAt, out var stored) ? stored : null;
            // The same instant again (a retry, even with another format) is not a new check: it is ignored.
            var alreadyAccepted = instant == latest || await db.Set<MonitoringCheck>().AnyAsync(
                x => x.MonitoringId == row.Id && x.CheckedAt == instant, ct);
            if (!alreadyAccepted)
            {
                if (instant < latest)
                    return Results.Conflict(new { message = "Check is older than the latest accepted check" });
                checkedAt = instant;
            }
        }

        var checkedNow = checkedAt is not null;
        var current = Capture(vehicle);
        var saved = input with
        {
            Id = row.Id, Snapshot = previous is null || checkedNow ? current : previous.Snapshot,
            LastCheckedAt = checkedNow ? input.LastCheckedAt : previous?.LastCheckedAt ?? "",
            NextCheckAt = previous is null || checkedNow || previous.Status != input.Status
                ? now.AddMinutes(input.IntervalMinutes).ToString("O") : previous.NextCheckAt
        };
        row.Active = input.Status == "active";
        row.Write(saved);
        if (id is null) db.Add(row);
        if (checkedAt is DateTime instantChecked)
            // The check and the new snapshot are saved together, in the same SaveChangesAsync.
            db.Add(new MonitoringCheck
            {
                AccountId = accountId, MonitoringId = row.Id, CheckedAt = instantChecked,
                Result = SameSnapshot(previous!.Snapshot, current) ? CheckResult.NoChanges : CheckResult.ChangeDetected
            });
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (IsCheckHistoryCollision(e))
        { return Results.Conflict(new { message = "Check already recorded" }); }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        { return Results.Conflict(new { message = "Vehicle already monitored" }); }
        return Results.Json(saved, statusCode: id is null ? 201 : 200);
    }

    private static async Task<IResult> RecordAlert(Alert input, ClaimsPrincipal principal, FleetProofDb db, CancellationToken ct)
    {
        var accountId = principal.AccountId();
        if (!await db.HasPlan(accountId, "monitoring", ct)) return SubscriptionEndpoints.PlanRequired();
        var watch = await db.Owned<VehicleWatch>(accountId).AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == input.MonitoringId && x.VehicleId == input.VehicleId && x.Active, ct);
        if (watch is null) return Results.NotFound();
        var vehicle = await db.Vehicles.AsNoTracking().SingleAsync(v => v.Id == watch.VehicleId && v.AccountId == accountId, ct);
        var before = watch.Read<Watch>().Snapshot!;
        var after = Capture(vehicle);
        var changes = after.Where(x => before.GetValueOrDefault(x.Key) != x.Value)
            .Select(x => new Change(x.Key, before.GetValueOrDefault(x.Key), x.Value)).ToArray();
        if (changes.Length == 0) return Results.Conflict(new { message = "No vehicle changes detected" });
        var row = new MonitoringAlert { AccountId = accountId };
        var saved = new Alert(row.Id, watch.Id, watch.VehicleId, changes, DateTimeOffset.UtcNow);
        row.Write(saved);
        db.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Json(saved, statusCode: 201);
    }

    private static Dictionary<string, string> Capture(Vehicle v) => new()
    {
        ["plate"] = v.Plate, ["risk"] = v.Risk, ["site"] = v.Site, ["owner"] = v.Owner, ["type"] = v.Type
    };

    // Same keys and same values; the order of the stored JSON does not matter.
    private static bool SameSnapshot(Dictionary<string, string>? before, Dictionary<string, string> after) =>
        before is not null && before.Count == after.Count &&
        after.All(x => before.TryGetValue(x.Key, out var value) && value == x.Value);

    // Reads the instant as UTC and keeps microseconds, the precision of PostgreSQL timestamptz.
    private static bool TryReadInstant(string? text, out DateTime instant)
    {
        instant = default;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        var utc = parsed.UtcDateTime;
        instant = new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
        return true;
    }

    private static bool IsCheckHistoryCollision(DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation &&
        violation.ConstraintName == CheckHistoryIndex;

    private sealed record Watch(Guid Id, Guid VehicleId, int IntervalMinutes, string? Status,
        Dictionary<string, string>? Snapshot, string? LastCheckedAt, string? NextCheckAt);
    private sealed record Alert(Guid Id, Guid MonitoringId, Guid VehicleId, Change[]? Changes, DateTimeOffset? CreatedAt);
    private sealed record Change(string Field, string? Before, string After);
    private sealed record CheckItem(Guid Id, DateTime CheckedAt, string Result);
}
