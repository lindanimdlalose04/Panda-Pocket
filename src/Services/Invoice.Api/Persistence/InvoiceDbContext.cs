using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Invoice.Domain;

namespace PandaPocket.Services.Invoice.Persistence;

public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Invoice> Invoices => Set<Domain.Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<InvoiceStatusHistory> StatusHistory => Set<InvoiceStatusHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Domain.Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);

            e.Property(x => x.Reference).HasMaxLength(100).IsRequired();
            e.Property(x => x.Asset).HasMaxLength(20).IsRequired();
            e.Property(x => x.PayToAddress).HasMaxLength(128).IsRequired();

            // Money and crypto amounts are decimal, never double. Binary floating
            // point cannot represent 0.1 exactly, and a payment gateway that
            // loses fractions of a cent to rounding is not one anybody should use.
            e.Property(x => x.AmountZar).HasColumnType("numeric(18,2)");
            e.Property(x => x.LockedRate).HasColumnType("numeric(24,8)");

            // Eight decimal places because that is one satoshi, the smallest
            // divisible unit of Bitcoin.
            e.Property(x => x.CryptoAmount).HasColumnType("numeric(24,8)");

            // Stored as text rather than an integer. An integer enum is compact
            // but makes the table unreadable during a demo and turns any
            // reordering of the enum into silent data corruption.
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // A merchant listing its invoices filters by merchant and orders by
            // recency, which is exactly this index.
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt }).HasDatabaseName("ix_invoices_merchant_created");

            // The expiry sweeper looks for pending invoices past their deadline.
            e.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_invoices_status_expires");

            // A merchant's own reference must be unique to that merchant, so a
            // retried create with the same reference cannot produce two invoices.
            e.HasIndex(x => new { x.MerchantId, x.Reference }).IsUnique().HasDatabaseName("ux_invoices_merchant_reference");

            e.HasMany(x => x.Payments).WithOne(p => p.Invoice).HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.History).WithOne(h => h.Invoice).HasForeignKey(h => h.InvoiceId).OnDelete(DeleteBehavior.Cascade);

            e.Ignore(x => x.TotalReceived);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);

            e.Property(x => x.TxHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.AmountCrypto).HasColumnType("numeric(24,8)");
            e.Property(x => x.CorrelationId).HasMaxLength(64);

            // The idempotency guard and the replay detector, in one line of DDL.
            // Enforced by the database, so it holds even under concurrent
            // requests that application-level checks would race on.
            e.HasIndex(x => x.TxHash).IsUnique().HasDatabaseName("ux_payments_tx_hash");
        });

        b.Entity<InvoiceStatusHistory>(e =>
        {
            e.ToTable("invoice_status_history");
            e.HasKey(x => x.Id);

            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(300).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64);

            e.HasIndex(x => new { x.InvoiceId, x.CreatedAt }).HasDatabaseName("ix_status_history_invoice_created");

            // The future graph loader reads by correlation id to reconstruct a
            // session across services.
            e.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_status_history_correlation");
        });

        // Last, so anything named explicitly above is left alone.
        b.ApplySnakeCaseNames();
    }
}
