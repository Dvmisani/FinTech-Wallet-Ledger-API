namespace FinTechWallet.Api.Domain;

public enum EntryType
{
    Debit = 1,
    Credit = 2
}

public class Wallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerName { get; set; } = string.Empty;
    public string Currency { get; set; } = "ZAR";
    public decimal Balance { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}

public class LedgerTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}

public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public LedgerTransaction Transaction { get; set; } = null!;

    public Guid WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;

    public EntryType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}