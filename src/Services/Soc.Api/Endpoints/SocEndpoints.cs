using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Soc.Persistence;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Soc.Endpoints;

public static class SocEndpoints
{
    public static IEndpointRouteBuilder MapSocEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/soc").WithTags("SOC");

        // -------------------------------------------------------------------
        // Ingestion
        // -------------------------------------------------------------------
        group.MapPost("/events", async (
            List<SocEvent> events, SocDbContext db, ILogger<SocIngest> logger, CancellationToken ct) =>
        {
            if (events.Count == 0) return Results.NoContent();

            var received = DateTime.UtcNow;

            var records = events.Select(e => new SocEventRecord
            {
                EventId = e.EventId,
                Timestamp = e.Timestamp.UtcDateTime,
                ReceivedAt = received,
                ServiceName = e.ServiceName,
                EventType = e.EventType,
                Severity = e.Severity,
                CorrelationId = e.CorrelationId,
                UserId = e.UserId,
                SourceIp = e.SourceIp,
                Endpoint = e.Endpoint,
                HttpMethod = e.HttpMethod,
                StatusCode = e.StatusCode,
                Message = e.Message,
                AffectedEntity = e.AffectedEntity,
                MetadataJson = e.Metadata is null ? null : JsonSerializer.Serialize(e.Metadata)
            }).ToList();

            // The publisher retries a batch it could not confirm, so the same
            // event can arrive twice. Ignoring ids already present makes
            // ingestion idempotent, which is what lets the publisher retry at all
            // without inflating every count a detection rule computes.
            var ids = records.Select(r => r.EventId).ToList();
            var known = await db.Events
                .Where(e => ids.Contains(e.EventId))
                .Select(e => e.EventId)
                .ToListAsync(ct);

            var fresh = records.Where(r => !known.Contains(r.EventId)).ToList();

            if (fresh.Count > 0)
            {
                db.Events.AddRange(fresh);
                await db.SaveChangesAsync(ct);
            }

            logger.LogInformation(
                "Ingested {Fresh} events ({Duplicate} already known)", fresh.Count, records.Count - fresh.Count);

            return Results.Ok(new IngestResponse(fresh.Count, records.Count - fresh.Count));
        })
        .WithName("IngestEvents")
        .WithSummary("Receive a batch of security events from a service")
        .Produces<IngestResponse>();

        // -------------------------------------------------------------------
        // Reading
        // -------------------------------------------------------------------
        group.MapGet("/events", async (
            SocDbContext db,
            string? eventType,
            string? severity,
            string? serviceName,
            string? sourceIp,
            Guid? userId,
            string? correlationId,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            // Nullable on purpose. A non-nullable int query parameter is
            // REQUIRED in minimal APIs, so omitting it answers 400 instead of
            // taking the default. Coalescing after the fact does not help,
            // because the request never reaches the handler.
            var pageNumber = page is null or <= 0 ? 1 : page.Value;
            var size = pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;

            var query = db.Events.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(eventType))     query = query.Where(e => e.EventType == eventType);
            if (!string.IsNullOrWhiteSpace(severity))      query = query.Where(e => e.Severity == severity);
            if (!string.IsNullOrWhiteSpace(serviceName))   query = query.Where(e => e.ServiceName == serviceName);
            if (!string.IsNullOrWhiteSpace(sourceIp))      query = query.Where(e => e.SourceIp == sourceIp);
            if (!string.IsNullOrWhiteSpace(correlationId)) query = query.Where(e => e.CorrelationId == correlationId);
            if (userId is { } u)                           query = query.Where(e => e.UserId == u);

            var total = await query.CountAsync(ct);

            var items = await query
                .OrderByDescending(e => e.Timestamp)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .Select(e => new SocEventView(
                    e.EventId, e.Timestamp, e.ServiceName, e.EventType, e.Severity,
                    e.CorrelationId, e.UserId, e.SourceIp, e.Endpoint, e.HttpMethod,
                    e.StatusCode, e.Message, e.AffectedEntity, e.MetadataJson))
                .ToListAsync(ct);

            return Results.Ok(new SocEventPage(total, pageNumber, size, items));
        })
        .WithName("ListEvents")
        .WithSummary("Collected events, newest first, filtered")
        .Produces<SocEventPage>();

        // -------------------------------------------------------------------
        // What the collector currently holds, for the dashboard and the demo
        // -------------------------------------------------------------------
        group.MapGet("/summary", async (SocDbContext db, CancellationToken ct) =>
        {
            var since = DateTime.UtcNow.AddHours(-24);

            // Grouped into anonymous types, then ordered and mapped in memory.
            //
            // Projecting a grouping straight into a record and ordering by one
            // of its properties is not translatable: EF Core gave up with
            // "could not be translated" and the endpoint answered 500. There are
            // at most a few dozen groups, so sorting them here costs nothing.
            var typeGroups = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= since)
                .GroupBy(e => new { e.EventType, e.Severity })
                .Select(g => new { g.Key.EventType, g.Key.Severity, Count = g.Count() })
                .ToListAsync(ct);

            var byType = typeGroups
                .OrderByDescending(g => g.Count)
                .Select(g => new EventCount(g.EventType, g.Severity, g.Count))
                .ToList();

            var serviceGroups = await db.Events.AsNoTracking()
                .Where(e => e.Timestamp >= since)
                .GroupBy(e => e.ServiceName)
                .Select(g => new { ServiceName = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var byService = serviceGroups
                .OrderByDescending(g => g.Count)
                .Select(g => new ServiceCount(g.ServiceName, g.Count))
                .ToList();

            return Results.Ok(new SocSummary(
                await db.Events.CountAsync(ct), since, byType, byService));
        })
        .WithName("EventSummary")
        .WithSummary("Counts by event type and by originating service over the last day")
        .Produces<SocSummary>();

        return app;
    }

    /// <summary>Only a category for the ingestion logger.</summary>
    private sealed class SocIngest;
}

public sealed record IngestResponse(int Accepted, int Duplicates);

public sealed record SocEventView(
    Guid EventId, DateTime Timestamp, string ServiceName, string EventType, string Severity,
    string CorrelationId, Guid? UserId, string? SourceIp, string? Endpoint, string? HttpMethod,
    int? StatusCode, string? Message, string? AffectedEntity, string? Metadata);

public sealed record SocEventPage(int TotalCount, int Page, int PageSize, IReadOnlyList<SocEventView> Items);

public sealed record EventCount(string EventType, string Severity, int Count);
public sealed record ServiceCount(string ServiceName, int Count);
public sealed record SocSummary(
    int TotalStored, DateTime Since, IReadOnlyList<EventCount> ByType, IReadOnlyList<ServiceCount> ByService);
