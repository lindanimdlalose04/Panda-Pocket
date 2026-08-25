using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PandaPocket.Services.Rate.Endpoints;

/// <summary>
/// The default health response is the single word "Healthy", which is useless
/// when a check fails and you need to know which dependency is at fault. This
/// writes each check by name with its status and duration.
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                // The Mongo driver's exception message is a multi-line cluster
                // diagnostic running to a couple of thousand characters. Useful
                // in a log, unreadable on a status page, so it is trimmed to
                // its first line here. The full detail is still in Seq.
                error = FirstLine(e.Value.Exception?.Message)
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, Options));
    }

    private static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var line = message.Split('\n', '\r')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
