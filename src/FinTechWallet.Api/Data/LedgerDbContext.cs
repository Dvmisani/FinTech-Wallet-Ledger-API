using FinTechWallet.Api.Domain;
using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;

namespace FinTechWallet.Api.Data;

public class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.OwnerName).HasMaxLength(150).IsRequired();
            entity.Property(w => w.Currency).HasMaxLength(3).IsRequired();
            entity.Property(w => w.Balance).HasPrecision(18, 2);
        });

        modelBuilder.Entity<LedgerTransaction>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.HasIndex(t => t.IdempotencyKey).IsUnique();
            entity.Property(t => t.Description).HasMaxLength(250);
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);

            entity.HasOne(e => e.Transaction)
                  .WithMany(t => t.Entries)
                  .HasForeignKey(e => e.TransactionId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Wallet)
                  .WithMany(w => w.Entries)
                  .HasForeignKey(e => e.WalletId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}