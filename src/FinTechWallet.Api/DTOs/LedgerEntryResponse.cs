namespace FinTechWallet.Api.DTOs;

public record LedgerEntryResponse(
    Guid EntryId,
    Guid TransactionId,
    string IdempotencyKey,
    string Type,
    decimal Amount,
    string CounterpartyName,
    string Description,
    DateTime CreatedAtUtc);
