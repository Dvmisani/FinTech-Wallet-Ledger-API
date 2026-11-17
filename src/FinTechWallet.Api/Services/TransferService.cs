using FinTechWallet.Api.Data;
using FinTechWallet.Api.Domain;
using FinTechWallet.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace FinTechWallet.Api.Services;

public class TransferException : Exception
{
    public int StatusCode { get; }

    public TransferException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
    }
}

public class TransferService
{
    // Well-known system settlement wallet so initial deposits also obey double-entry zero-sum rules
    public static readonly Guid SystemTreasuryWalletId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly LedgerDbContext _db;

    public TransferService(LedgerDbContext db)
    {
        _db = db;
    }

    public async Task<WalletResponse> CreateWalletAsync(CreateWalletRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerName))
            throw new TransferException("OwnerName is required.", 400);

        if (request.InitialDeposit < 0)
            throw new TransferException("Initial deposit cannot be negative.", 400);

        var wallet = new Wallet
        {
            OwnerName = request.OwnerName.Trim(),
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "ZAR" : request.Currency.Trim().ToUpperInvariant(),
            Balance = 0m
        };

        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync(ct);

        if (request.InitialDeposit > 0)
        {
            await EnsureTreasuryWalletExistsAsync(wallet.Currency, ct);
            await ExecuteTransferInternalAsync(
                SystemTreasuryWalletId,
                wallet.Id,
                request.InitialDeposit,
                "Initial wallet funding",
                $"init-{wallet.Id}",
                allowNegativeSource: true,
                ct);

            var funded = await _db.Wallets.AsNoTracking().FirstAsync(w => w.Id == wallet.Id, ct);
            return new WalletResponse(funded.Id, funded.OwnerName, funded.Currency, funded.Balance, funded.CreatedAtUtc);
        }

        return new WalletResponse(wallet.Id, wallet.OwnerName, wallet.Currency, wallet.Balance, wallet.CreatedAtUtc);
    }

    public async Task<WalletResponse?> GetWalletAsync(Guid id, CancellationToken ct = default)
    {
        var wallet = await _db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return wallet is null
            ? null
            : new WalletResponse(wallet.Id, wallet.OwnerName, wallet.Currency, wallet.Balance, wallet.CreatedAtUtc);
    }

    public async Task<IReadOnlyList<WalletResponse>> GetWalletsAsync(CancellationToken ct = default)
    {
        return await _db.Wallets
            .AsNoTracking()
            .Where(w => w.Id != SystemTreasuryWalletId)
            .OrderByDescending(w => w.CreatedAtUtc)
            .Select(w => new WalletResponse(w.Id, w.OwnerName, w.Currency, w.Balance, w.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LedgerEntryResponse>?> GetWalletTransactionsAsync(Guid walletId, CancellationToken ct = default)
    {
        var walletExists = await _db.Wallets.AsNoTracking().AnyAsync(w => w.Id == walletId, ct);
        if (!walletExists)
            return null;

        var entries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.WalletId == walletId)
            .Include(e => e.Transaction)
                .ThenInclude(t => t.Entries)
            .OrderByDescending(e => e.Transaction.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        var counterpartyIds = entries
            .SelectMany(e => e.Transaction.Entries)
            .Select(te => te.WalletId)
            .Where(id => id != walletId)
            .Distinct()
            .ToList();

        var walletNames = await _db.Wallets
            .AsNoTracking()
            .Where(w => counterpartyIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.OwnerName, ct);

        return entries.Select(e =>
        {
            var counterpartyId = e.Transaction.Entries
                .FirstOrDefault(other => other.WalletId != walletId)?.WalletId ?? Guid.Empty;

            var counterparty = walletNames.TryGetValue(counterpartyId, out var name)
                ? name
                : "System Settlement Treasury";

            return new LedgerEntryResponse(
                e.Id,
                e.TransactionId,
                e.Transaction.IdempotencyKey,
                e.Type.ToString(),
                e.Amount,
                counterparty,
                e.Transaction.Description,
                e.Transaction.CreatedAtUtc);
        }).ToList();
    }

    public async Task<TransferResponse> TransferAsync(TransferRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new TransferException("Idempotency-Key header is required.", 400);

        if (request.Amount <= 0)
            throw new TransferException("Transfer amount must be greater than zero.", 400);

        if (request.SourceWalletId == request.DestinationWalletId)
            throw new TransferException("Source and destination wallets must be different.", 400);

        return await ExecuteTransferInternalAsync(
            request.SourceWalletId,
            request.DestinationWalletId,
            request.Amount,
            request.Description,
            idempotencyKey.Trim(),
            allowNegativeSource: false,
            ct);
    }

    private async Task<TransferResponse> ExecuteTransferInternalAsync(
        Guid sourceWalletId,
        Guid destinationWalletId,
        decimal amount,
        string description,
        string idempotencyKey,
        bool allowNegativeSource,
        CancellationToken ct)
    {
        // Fast-path idempotency check before acquiring locks
        var existing = await GetExistingTransactionAsync(idempotencyKey, ct);
        if (existing is not null)
            return existing;

        // Sort IDs deterministically to prevent circular deadlocks under bidirectional concurrent transfers
        var firstId = sourceWalletId.CompareTo(destinationWalletId) < 0 ? sourceWalletId : destinationWalletId;
        var secondId = firstId == sourceWalletId ? destinationWalletId : sourceWalletId;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            try
            {
                // Acquire pessimistic row-level update locks on both wallets
                var lockedWallets = await _db.Wallets
                    .FromSqlRaw(
                        "SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE Id IN ({0}, {1}) ORDER BY Id",
                        firstId,
                        secondId)
                    .ToListAsync(ct);

                var source = lockedWallets.FirstOrDefault(w => w.Id == sourceWalletId)
                    ?? throw new TransferException($"Source wallet '{sourceWalletId}' was not found.", 404);

                var destination = lockedWallets.FirstOrDefault(w => w.Id == destinationWalletId)
                    ?? throw new TransferException($"Destination wallet '{destinationWalletId}' was not found.", 404);

                if (!string.Equals(source.Currency, destination.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new TransferException("Cross-currency transfers are not supported.", 400);

                if (!allowNegativeSource && source.Balance < amount)
                    throw new TransferException("Insufficient funds.", 422);

                var ledgerTx = new LedgerTransaction
                {
                    IdempotencyKey = idempotencyKey,
                    Description = string.IsNullOrWhiteSpace(description) ? "Wallet transfer" : description.Trim()
                };

                var debitEntry = new LedgerEntry
                {
                    TransactionId = ledgerTx.Id,
                    WalletId = source.Id,
                    Type = EntryType.Debit,
                    Amount = amount
                };

                var creditEntry = new LedgerEntry
                {
                    TransactionId = ledgerTx.Id,
                    WalletId = destination.Id,
                    Type = EntryType.Credit,
                    Amount = amount
                };

                source.Balance -= amount;
                destination.Balance += amount;

                _db.LedgerTransactions.Add(ledgerTx);
                _db.LedgerEntries.AddRange(debitEntry, creditEntry);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new TransferResponse(
                    ledgerTx.Id,
                    ledgerTx.IdempotencyKey,
                    source.Id,
                    destination.Id,
                    amount,
                    ledgerTx.Description,
                    ledgerTx.CreatedAtUtc);
            }
            catch (DbUpdateException)
            {
                // Handles concurrent duplicate IdempotencyKey race conditions cleanly
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();

                var duplicate = await GetExistingTransactionAsync(idempotencyKey, ct);
                if (duplicate is not null)
                    return duplicate;

                throw;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    private async Task<TransferResponse?> GetExistingTransactionAsync(string idempotencyKey, CancellationToken ct)
    {
        var tx = await _db.LedgerTransactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);

        if (tx is null)
            return null;

        var debit = tx.Entries.First(e => e.Type == EntryType.Debit);
        var credit = tx.Entries.First(e => e.Type == EntryType.Credit);

        return new TransferResponse(
            tx.Id,
            tx.IdempotencyKey,
            debit.WalletId,
            credit.WalletId,
            debit.Amount,
            tx.Description,
            tx.CreatedAtUtc);
    }

    private async Task EnsureTreasuryWalletExistsAsync(string currency, CancellationToken ct)
    {
        var exists = await _db.Wallets.AnyAsync(w => w.Id == SystemTreasuryWalletId, ct);
        if (!exists)
        {
            _db.Wallets.Add(new Wallet
            {
                Id = SystemTreasuryWalletId,
                OwnerName = "System Settlement Treasury",
                Currency = currency,
                Balance = 0m
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}
