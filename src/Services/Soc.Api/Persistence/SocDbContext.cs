using Microsoft.EntityFrameworkCore;
using PandaPocket.Shared.Persistence;

namespace PandaPocket.Services.Soc.Persistence;

public sealed class SocDbContext(DbContextOptions<SocDbContext> options) : DbContext(options)
{
    public DbSet<SocEventRecord> Events => Set<SocEventRecord>();
    public DbSet<DetectionRuleRecord> Rules => Set<DetectionRuleRecord>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertEventLink> AlertEvents => Set<AlertEventLink>();
    public DbSet<Incident> Incidents => Set<Incident>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<DetectionRuleRecord>(entity =>
        {
            entity.ToTable("detection_rules");
            entity.HasKey(r => r.RuleId);
            entity.Property(r => r.RuleId).HasMaxLength(8);
            entity.Property(r => r.Name).HasMaxLength(128).IsRequired();
            entity.Property(r => r.Purpose).HasMaxLength(512).IsRequired();
            entity.Property(r => r.InputEventTypes).HasMaxLength(256).IsRequired();
            entity.Property(r => r.Condition).HasMaxLength(512).IsRequired();
            entity.Property(r => r.Severity).HasMaxLength(16).IsRequired();
            entity.Property(r => r.AlertType).HasMaxLength(64).IsRequired();
            entity.Property(r => r.RecommendedAction).HasMaxLength(512).IsRequired();
            entity.Property(r => r.ThreatReference).HasMaxLength(128);
        });

        builder.Entity<Alert>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(a => a.AlertId);
            entity.Property(a => a.RuleId).HasMaxLength(8).IsRequired();
            entity.Property(a => a.RuleName).HasMaxLength(128).IsRequired();
            entity.Property(a => a.Severity).HasMaxLength(16).IsRequired();
            entity.Property(a => a.Status).HasMaxLength(16).IsRequired();
            entity.Property(a => a.Description).HasMaxLength(1024).IsRequired();
            entity.Property(a => a.RecommendedAction).HasMaxLength(512).IsRequired();
            entity.Property(a => a.AffectedService).HasMaxLength(64);
            entity.Property(a => a.AffectedEntity).HasMaxLength(128);
            entity.Property(a => a.SourceIp).HasMaxLength(64);
            entity.Property(a => a.SuppressionKey).HasMaxLength(160).IsRequired();

            // Suppression looks up "is there an open alert for this rule and this
            // entity", every cycle, for every hit. Without this index that is a
            // scan of the alert table per hit.
            entity.HasIndex(a => new { a.SuppressionKey, a.Status })
                  .HasDatabaseName("ix_alerts_suppression");

            entity.HasIndex(a => a.LastSeenAt).HasDatabaseName("ix_alerts_last_seen");

            entity.HasMany(a => a.Events)
                  .WithOne()
                  .HasForeignKey(e => e.AlertId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AlertEventLink>(entity =>
        {
            entity.ToTable("alert_events");

            // Composite key, so linking the same event to the same alert twice is
            // impossible rather than merely unlikely. A rule re-evaluating over
            // an overlapping window sees the same events again every cycle.
            entity.HasKey(e => new { e.AlertId, e.EventId });
            entity.HasIndex(e => e.EventId).HasDatabaseName("ix_alert_events_event");
        });

        builder.Entity<Incident>(entity =>
        {
            entity.ToTable("incidents");
            entity.HasKey(i => i.IncidentId);
            entity.Property(i => i.Title).HasMaxLength(256).IsRequired();
            entity.Property(i => i.Severity).HasMaxLength(16).IsRequired();
            entity.Property(i => i.Status).HasMaxLength(16).IsRequired();
            entity.Property(i => i.Summary).HasMaxLength(1024).IsRequired();
        });

        builder.Entity<SocEventRecord>(entity =>
        {
            entity.ToTable("soc_events");
            entity.HasKey(e => e.EventId);

            entity.Property(e => e.ServiceName).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Severity).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();

            // An IPv6 address with a zone suffix does not fit in 39 characters,
            // and inet would refuse the malformed values an attacker can easily
            // produce. Storing what arrived, as text, is what an SOC wants.
            entity.Property(e => e.SourceIp).HasMaxLength(64);

            entity.Property(e => e.Endpoint).HasMaxLength(256);
            entity.Property(e => e.HttpMethod).HasMaxLength(10);
            entity.Property(e => e.Message).HasMaxLength(512);
            entity.Property(e => e.AffectedEntity).HasMaxLength(128);

            // jsonb rather than text, so a rule can still reach into metadata
            // for the one event type that needs it without parsing strings.
            entity.Property(e => e.MetadataJson).HasColumnType("jsonb");

            // The indexes are the detection rules, written down.
            //
            // Every rule is "events of this type, about this actor, inside this
            // window", so each index leads with the discriminator and ends with
            // the timestamp. Without them each rule is a sequential scan every
            // evaluation cycle, which is survivable at demo volumes and would not
            // be at any real one.
            entity.HasIndex(e => new { e.EventType, e.Timestamp })
                  .HasDatabaseName("ix_soc_events_type_time");

            // R1 credential probing and R6 checkout enumeration: one address,
            // many targets.
            entity.HasIndex(e => new { e.SourceIp, e.Timestamp })
                  .HasDatabaseName("ix_soc_events_source_time");

            // R2 quota abuse and R5 payout redirection: one merchant, over time.
            entity.HasIndex(e => new { e.UserId, e.Timestamp })
                  .HasDatabaseName("ix_soc_events_user_time");

            // Tracing one request across services, which is the traceability the
            // specification asks to be demonstrated.
            entity.HasIndex(e => e.CorrelationId)
                  .HasDatabaseName("ix_soc_events_correlation");
        });

        builder.ApplySnakeCaseNames();
    }
}
