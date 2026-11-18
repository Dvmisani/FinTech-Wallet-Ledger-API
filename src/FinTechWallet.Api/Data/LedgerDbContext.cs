using FinTechWallet.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTechWallet.Api.Data;

public class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<FraudAlert> FraudAlerts => Set<FraudAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.OwnerName).HasMaxLength(150).IsRequired();
            entity.Property(w => w.Currency).HasMaxLength(3).IsRequired();
            entity.Property(w => w.Balance).HasColumnType("decimal(19,4)");
        });

        modelBuilder.Entity<LedgerTransaction>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.HasIndex(t => t.IdempotencyKey).IsUnique();
            entity.Property(t => t.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Description).HasMaxLength(250);
            entity.HasIndex(t => t.ReversedTransactionId)
                .IsUnique()
                .HasFilter("[ReversedTransactionId] IS NOT NULL");
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(19,4)");

            entity.HasOne(e => e.Transaction)
                .WithMany(t => t.Entries)
                .HasForeignKey(e => e.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Wallet)
                .WithMany(w => w.Entries)
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.EventType).HasMaxLength(100).IsRequired();
            entity.Property(o => o.CorrelationId).HasMaxLength(64);
            entity.HasIndex(o => new { o.PublishedAtUtc, o.DeadLetteredAtUtc, o.CreatedAtUtc });
        });

        modelBuilder.Entity<FraudAlert>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Amount).HasColumnType("decimal(19,4)");
            entity.Property(f => f.Currency).HasMaxLength(3).IsRequired();
            entity.Property(f => f.RuleTriggered).HasMaxLength(80).IsRequired();
            entity.Property(f => f.Status).HasMaxLength(30).IsRequired();
            entity.Property(f => f.CorrelationId).HasMaxLength(64);
            entity.HasIndex(f => new { f.TransactionId, f.RuleTriggered }).IsUnique();
        });
    }
}
