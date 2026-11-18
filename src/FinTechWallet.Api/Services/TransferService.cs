using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        var normalizedCurrency = string.IsNullOrWhiteSpace(request.Currency)
            ? "ZAR"
            : request.Currency.Trim().ToUpperInvariant();

        if (normalizedCurrency.Length != 3)
            throw new TransferException("Currency must be a 3-letter ISO code (e.g. ZAR).", 400);

        var wallet = new Wallet
        {
            OwnerName = request.OwnerName.Trim(),
            Currency = normalizedCurrency,
            Balance = 0m
        };

        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync(ct);

        if (request.InitialDeposit > 0)
        {
            var treasuryId = await EnsureTreasuryWalletExistsAsync(wallet.Currency, ct);
            await ExecuteTransferInternalAsync(
                treasuryId,
                wallet.Id,
                request.InitialDeposit,
                "Initial wallet funding",
                $"init-{wallet.Id}",
                reversedTransactionId: null,
                allowNegativeSource: true,
                correlationId: null,
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
            .Where(w => !w.OwnerName.StartsWith("System Settlement Treasury"))
            .OrderByDescending(w => w.CreatedAtUtc)
            .Select(w => new WalletResponse(w.Id, w.OwnerName, w.Currency, w.Balance, w.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LedgerEntryResponse>?> GetWalletTransactionsAsync(
        Guid walletId,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var walletExists = await _db.Wallets.AsNoTracking().AnyAsync(w => w.Id == walletId, ct);
        if (!walletExists)
            return null;

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);

        var transactions = await _db.LedgerTransactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .Where(t => t.Entries.Any(e => e.WalletId == walletId))
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        var counterpartyIds = transactions
            .SelectMany(t => t.Entries)
            .Select(e => e.WalletId)
            .Where(id => id != walletId)
            .Distinct()
            .ToList();

        var walletNames = await _db.Wallets
            .AsNoTracking()
            .Where(w => counterpartyIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.OwnerName, ct);

        return transactions.SelectMany(t =>
        {
            var counterpartyId = t.Entries
                .FirstOrDefault(other => other.WalletId != walletId)?.WalletId ?? Guid.Empty;

            var counterparty = walletNames.TryGetValue(counterpartyId, out var name)
                ? name
                : "System Settlement Treasury";

            return t.Entries
                .Where(e => e.WalletId == walletId)
                .Select(e => new LedgerEntryResponse(
                    e.Id,
                    t.Id,
                    t.IdempotencyKey,
                    e.Type.ToString(),
                    e.Amount,
                    counterparty,
                    t.Description,
                    t.CreatedAtUtc));
        }).ToList();
    }

    public async Task<TransferResponse> TransferAsync(
        TransferRequest request,
        string idempotencyKey,
        string? correlationId = null,
        CancellationToken ct = default)
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
            reversedTransactionId: null,
            allowNegativeSource: false,
            correlationId,
            ct);
    }

    public async Task<TransferResponse> ReverseTransferAsync(
        Guid transactionId,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        var original = await _db.LedgerTransactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct)
            ?? throw new TransferException($"Transaction '{transactionId}' was not found.", 404);

        if (original.ReversedTransactionId.HasValue)
            throw new TransferException("Reversal transactions cannot themselves be reversed.", 400);

        var alreadyReversed = await _db.LedgerTransactions
            .AsNoTracking()
            .AnyAsync(t => t.ReversedTransactionId == transactionId, ct);

        if (alreadyReversed)
            throw new TransferException($"Transaction '{transactionId}' has already been reversed.", 409);

        var originalDebit = original.Entries.First(e => e.Type == EntryType.Debit);
        var originalCredit = original.Entries.First(e => e.Type == EntryType.Credit);

        if (original.IdempotencyKey.StartsWith("init-", StringComparison.Ordinal))
            throw new TransferException("Initial system treasury funding cannot be reversed.", 400);

        // Compensating transfer swaps source and destination without mutating existing ledger rows
        return await ExecuteTransferInternalAsync(
            sourceWalletId: originalCredit.WalletId,
            destinationWalletId: originalDebit.WalletId,
            amount: originalDebit.Amount,
            description: $"Reversal of {original.Id}: {original.Description}",
            idempotencyKey: $"rev-{original.Id}",
            reversedTransactionId: original.Id,
            allowNegativeSource: false,
            correlationId,
            ct);
    }

    private async Task<TransferResponse> ExecuteTransferInternalAsync(
        Guid sourceWalletId,
        Guid destinationWalletId,
        decimal amount,
        string description,
        string idempotencyKey,
        Guid? reversedTransactionId,
        bool allowNegativeSource,
        string? correlationId,
        CancellationToken ct)
    {
        var normalizedDesc = string.IsNullOrWhiteSpace(description) ? "Wallet transfer" : description.Trim();
        var requestHash = ComputeRequestHash(sourceWalletId, destinationWalletId, amount, normalizedDesc);

        // Fast-path idempotency check with payload-hash verification
        var existing = await GetExistingTransactionAsync(idempotencyKey, requestHash, ct);
        if (existing is not null)
            return existing;

        // Deterministic sequential lock ordering guarantees SQL Server acquires row locks in identical order
        var firstId = sourceWalletId.CompareTo(destinationWalletId) < 0 ? sourceWalletId : destinationWalletId;
        var secondId = firstId == sourceWalletId ? destinationWalletId : sourceWalletId;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            try
            {
                var firstLocked = await _db.Wallets
                    .FromSqlRaw("SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE Id = {0}", firstId)
                    .FirstOrDefaultAsync(ct);

                var secondLocked = await _db.Wallets
                    .FromSqlRaw("SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE Id = {0}", secondId)
                    .FirstOrDefaultAsync(ct);

                var source = firstId == sourceWalletId ? firstLocked : secondLocked;
                var destination = firstId == destinationWalletId ? firstLocked : secondLocked;

                if (source is null)
                    throw new TransferException($"Source wallet '{sourceWalletId}' was not found.", 404);

                if (destination is null)
                    throw new TransferException($"Destination wallet '{destinationWalletId}' was not found.", 404);

                if (!string.Equals(source.Currency, destination.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new TransferException("Cross-currency transfers are not supported.", 400);

                if (!allowNegativeSource && source.Balance < amount)
                    throw new TransferException("Insufficient funds.", 422);

                var ledgerTx = new LedgerTransaction
                {
                    IdempotencyKey = idempotencyKey,
                    RequestHash = requestHash,
                    Description = normalizedDesc,
                    ReversedTransactionId = reversedTransactionId
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

                // Transactional Outbox entry written atomically with ledger mutations
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    EventType = reversedTransactionId.HasValue ? "TransferReversedEvent" : "TransferCompletedEvent",
                    CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        EventId = ledgerTx.Id,
                        ledgerTx.IdempotencyKey,
                        SourceWalletId = source.Id,
                        DestinationWalletId = destination.Id,
                        Amount = amount,
                        Currency = source.Currency,
                        Description = ledgerTx.Description,
                        ReversedTransactionId = reversedTransactionId,
                        OccurredAtUtc = ledgerTx.CreatedAtUtc
                    })
                });

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
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();

                // Simultaneous duplicate IdempotencyKey collision: return winner if payload matches, else 409
                var duplicate = await GetExistingTransactionAsync(idempotencyKey, requestHash, ct);
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

    private async Task<TransferResponse?> GetExistingTransactionAsync(
        string idempotencyKey,
        string expectedRequestHash,
        CancellationToken ct)
    {
        var tx = await _db.LedgerTransactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);

        if (tx is null)
            return null;

        if (!string.Equals(tx.RequestHash, expectedRequestHash, StringComparison.Ordinal))
        {
            throw new TransferException(
                "Idempotency-Key has already been used with a different transfer payload.",
                409);
        }

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

    public static Guid GetTreasuryWalletId(string currency)
    {
        if (string.Equals(currency, "ZAR", StringComparison.OrdinalIgnoreCase))
            return SystemTreasuryWalletId;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"TREASURY:{currency.ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task<Guid> EnsureTreasuryWalletExistsAsync(string currency, CancellationToken ct)
    {
        var treasuryId = GetTreasuryWalletId(currency);
        var exists = await _db.Wallets.AnyAsync(w => w.Id == treasuryId, ct);
        if (!exists)
        {
            _db.Wallets.Add(new Wallet
            {
                Id = treasuryId,
                OwnerName = $"System Settlement Treasury ({currency})",
                Currency = currency,
                Balance = 0m
            });
            await _db.SaveChangesAsync(ct);
        }
        return treasuryId;
    }

    private static string ComputeRequestHash(
        Guid sourceWalletId,
        Guid destinationWalletId,
        decimal amount,
        string normalizedDescription)
    {
        var canonical = string.Join("|",
            sourceWalletId.ToString("D"),
            destinationWalletId.ToString("D"),
            amount.ToString("0.0000", CultureInfo.InvariantCulture),
            normalizedDescription);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}


