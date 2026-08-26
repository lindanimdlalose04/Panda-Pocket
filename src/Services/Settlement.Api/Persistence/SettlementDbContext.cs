using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Settlement.Domain;
using PandaPocket.Shared.Persistence;

namespace PandaPocket.Services.Settlement.Persistence;

public sealed class SettlementDbContext(DbContextOptions<SettlementDbContext> options) : DbContext(options)
{
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<MerchantBalance> MerchantBalances => Set<MerchantBalance>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasKey(x => x.Id);

            e.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.AmountZar).HasColumnType("numeric(18,2)");
            e.Property(x => x.BalanceAfter).HasColumnType("numeric(18,2)");
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.CorrelationId).HasMaxLength(64);

            // A statement is "this merchant, newest first", which is this index.
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt }).HasDatabaseName("ix_ledger_merchant_created");

            // Settling the same invoice twice would credit a merchant twice for
            // one payment. The unique index makes that impossible at the database
            // level rather than relying on the caller not to retry, which matters
            // because Invoice does retry this call by design.
            e.HasIndex(x => new { x.InvoiceId, x.EntryType })
                .IsUnique()
                .HasFilter("invoice_id IS NOT NULL")
                .HasDatabaseName("ux_ledger_invoice_entrytype");
        });

        b.Entity<MerchantBalance>(e =>
        {
            e.ToTable("merchant_balances");
            e.HasKey(x => x.MerchantId);

            e.Property(x => x.AvailableZar).HasColumnType("numeric(18,2)");
            e.Property(x => x.LifetimeCreditedZar).HasColumnType("numeric(18,2)");
            e.Property(x => x.LifetimeFeesZar).HasColumnType("numeric(18,2)");
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);

            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(500);

            // The dispatcher's query: pending deliveries that are due.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_webhook_status_next_attempt");
            e.HasIndex(x => x.MerchantId).HasDatabaseName("ix_webhook_merchant");
        });

        b.ApplySnakeCaseNames();
    }
}

/// <summary>
/// The current position for one merchant.
///
/// A cache of the ledger, not a second source of truth. Everything here is
/// derivable by summing ledger_entries; it exists so that showing a balance is
/// one row read rather than an aggregate over the merchant's entire history.
/// </summary>
public sealed class MerchantBalance
{
    public Guid MerchantId { get; set; }
    public decimal AvailableZar { get; set; }
    public decimal LifetimeCreditedZar { get; set; }
    public decimal LifetimeFeesZar { get; set; }
    public DateTime UpdatedAt { get; set; }
}
