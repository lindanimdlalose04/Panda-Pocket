using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Soc.Persistence;

namespace PandaPocket.Services.Soc.Endpoints;

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/soc").WithTags("Alerts");

        // -------------------------------------------------------------------
        // The rule catalogue, which is also the rule documentation
        // -------------------------------------------------------------------
        group.MapGet("/rules", async (SocDbContext db, CancellationToken ct) =>
        {
            var rules = await db.Rules.AsNoTracking().OrderBy(r => r.RuleId).ToListAsync(ct);
            return Results.Ok(rules);
        })
        .WithName("ListRules")
        .WithSummary("Every detection rule, with its purpose, condition and recommended response");

        // -------------------------------------------------------------------
        // Alerts
        // -------------------------------------------------------------------
        group.MapGet("/alerts", async (
            SocDbContext db, string? status, string? severity, string? ruleId,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            // Nullable, because a non-nullable int query parameter is REQUIRED in
            // minimal APIs: omitting it answers 400 rather than using a default.
            var pageNumber = page is null or <= 0 ? 1 : page.Value;
            var size = pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;

            var query = db.Alerts.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))   query = query.Where(a => a.Status == status);
            if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(a => a.Severity == severity);
            if (!string.IsNullOrWhiteSpace(ruleId))   query = query.Where(a => a.RuleId == ruleId);

            var total = await query.CountAsync(ct);

            var items = await query
                .OrderByDescending(a => a.LastSeenAt)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .Select(a => new AlertView(
                    a.AlertId, a.FirstSeenAt, a.LastSeenAt, a.RuleId, a.RuleName, a.Severity,
                    a.Status, a.Description, a.RecommendedAction, a.AffectedService,
                    a.AffectedUserId, a.AffectedEntity, a.SourceIp, a.EventCount, a.IncidentId))
                .ToListAsync(ct);

            return Results.Ok(new AlertPage(total, pageNumber, size, items));
        })
        .WithName("ListAlerts")
        .WithSummary("Alerts, newest activity first")
        .Produces<AlertPage>();

        // -------------------------------------------------------------------
        // One alert, with the events that caused it.
        //
        // This is the endpoint the specification's traceability requirement
        // rests on, and the one the RAG layer will read: from an alert, back to
        // the individual events, and on to the rule that connected them.
        // -------------------------------------------------------------------
        group.MapGet("/alerts/{id:guid}", async (Guid id, SocDbContext db, CancellationToken ct) =>
        {
            var alert = await db.Alerts.AsNoTracking()
                .Include(a => a.Events)
                .FirstOrDefaultAsync(a => a.AlertId == id, ct);

            if (alert is null)
            {
                return Results.Problem(title: "Alert not found", statusCode: StatusCodes.Status404NotFound);
            }

            var rule = await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.RuleId == alert.RuleId, ct);

            var eventIds = alert.Events.Select(e => e.EventId).ToList();

            var events = await db.Events.AsNoTracking()
                .Where(e => eventIds.Contains(e.EventId))
                .OrderBy(e => e.Timestamp)
                .Select(e => new SocEventView(
                    e.EventId, e.Timestamp, e.ServiceName, e.EventType, e.Severity,
                    e.CorrelationId, e.UserId, e.SourceIp, e.Endpoint, e.HttpMethod,
                    e.StatusCode, e.Message, e.AffectedEntity, e.MetadataJson))
                .ToListAsync(ct);

            return Results.Ok(new AlertDetail(
                new AlertView(
                    alert.AlertId, alert.FirstSeenAt, alert.LastSeenAt, alert.RuleId, alert.RuleName,
                    alert.Severity, alert.Status, alert.Description, alert.RecommendedAction,
                    alert.AffectedService, alert.AffectedUserId, alert.AffectedEntity,
                    alert.SourceIp, alert.EventCount, alert.IncidentId),
                rule,
                events));
        })
        .WithName("GetAlert")
        .WithSummary("One alert with the rule that raised it and every event behind it")
        .Produces<AlertDetail>();

        // -------------------------------------------------------------------
        // Triage
        // -------------------------------------------------------------------
        group.MapPost("/alerts/{id:guid}/status", async (
            Guid id, UpdateAlertStatusRequest request, SocDbContext db, CancellationToken ct) =>
        {
            var allowed = new[] { AlertStatus.Open, AlertStatus.Acknowledged, AlertStatus.Closed };
            if (!allowed.Contains(request.Status))
            {
                return Results.Problem(
                    title: "Invalid status",
                    detail: $"Status must be one of {string.Join(", ", allowed)}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var alert = await db.Alerts.FirstOrDefaultAsync(a => a.AlertId == id, ct);
            if (alert is null) return Results.Problem(title: "Alert not found", statusCode: 404);

            alert.Status = request.Status;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
        .WithName("UpdateAlertStatus")
        .WithSummary("Acknowledge or close an alert");

        return app;
    }
}

public sealed record AlertView(
    Guid AlertId, DateTime FirstSeenAt, DateTime LastSeenAt, string RuleId, string RuleName,
    string Severity, string Status, string Description, string RecommendedAction,
    string? AffectedService, Guid? AffectedUserId, string? AffectedEntity, string? SourceIp,
    int EventCount, Guid? IncidentId);

public sealed record AlertPage(int TotalCount, int Page, int PageSize, IReadOnlyList<AlertView> Items);

public sealed record AlertDetail(
    AlertView Alert, DetectionRuleRecord? Rule, IReadOnlyList<SocEventView> Events);

public sealed record UpdateAlertStatusRequest(string Status);
