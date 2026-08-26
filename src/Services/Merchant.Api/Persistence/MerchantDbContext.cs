using Microsoft.EntityFrameworkCore;
using PandaPocket.Shared.Persistence;
using PandaPocket.Services.Merchant.Domain;

namespace PandaPocket.Services.Merchant.Persistence;

public sealed class MerchantDbContext(DbContextOptions<MerchantDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Merchant> Merchants => Set<Domain.Merchant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Domain.Merchant>(e =>
        {
            e.ToTable("merchants");
            e.HasKey(x => x.Id);

            e.Property(x => x.BusinessName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.FeePercent).HasColumnType("numeric(5,2)");
            e.Property(x => x.WebhookUrl).HasMaxLength(500);
            e.Property(x => x.WebhookSecret).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_merchants_email");

            e.HasMany(x => x.ApiKeys).WithOne(k => k.Merchant).HasForeignKey(k => k.MerchantId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Users).WithOne(u => u.Merchant).HasForeignKey(u => u.MerchantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);

            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(32).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100).IsRequired();

            // Every authenticated request is a lookup by hash, so this index is
            // on the hottest path in the system. Unique as well, because two
            // rows with the same hash would make authentication ambiguous.
            e.HasIndex(x => x.KeyHash).IsUnique().HasDatabaseName("ux_api_keys_hash");
            e.HasIndex(x => x.MerchantId).HasDatabaseName("ix_api_keys_merchant");

            e.Ignore(x => x.IsActive);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);

            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();

            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_users_email");
        });

        // Last, so anything named explicitly above is left alone.
        b.ApplySnakeCaseNames();
    }
}
