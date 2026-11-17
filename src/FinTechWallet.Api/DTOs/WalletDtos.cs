namespace FinTechWallet.Api.DTOs;

public record CreateWalletRequest(
    string OwnerName,
    string Currency = "ZAR",
    decimal InitialDeposit = 0m);

public record TransferRequest(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string Description = "Wallet transfer");

public record WalletResponse(
    Guid Id,
    string OwnerName,
    string Currency,
    decimal Balance,
    DateTime CreatedAtUtc);

public record TransferResponse(
    Guid TransactionId,
    string IdempotencyKey,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string Description,
    DateTime CreatedAtUtc);