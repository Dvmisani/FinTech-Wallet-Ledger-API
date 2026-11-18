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
    public string RequestHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? ReversedTransactionId { get; set; }
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

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? DeadLetteredAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

public class FraudAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public Guid SourceWalletId { get; set; }
    public Guid DestinationWalletId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ZAR";
    public string RuleTriggered { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public string Status { get; set; } = "Flagged";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
