using System.Net;
using System.Net.Http.Json;
using FinTechWallet.Api.Data;
using FinTechWallet.Api.Domain;
using FinTechWallet.Api.DTOs;
using FinTechWallet.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinTechWallet.IntegrationTests;

public class ConcurrencyAndLedgerTests : IClassFixture<LedgerApiFactory>
{
    private readonly LedgerApiFactory _factory;
    private readonly HttpClient _client;

    public ConcurrencyAndLedgerTests(LedgerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ConcurrentWithdrawals_ShouldPreventOverdraft_AndMaintainZeroSumLedger()
    {
        var source = await CreateWalletAsync("Thabo Mokoena", 1000m);
        var destination = await CreateWalletAsync("Lerato Ndlovu", 0m);

        var tasks = Enumerable.Range(1, 20).Select(i =>
            SendTransferAsync(source.Id, destination.Id, 100m, $"Concurrent transfer #{i}", $"conc-{Guid.NewGuid()}"));

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(10, succeeded);
        Assert.Equal(10, rejected);

        var updatedSource = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{source.Id}");
        var updatedDest = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{destination.Id}");

        Assert.NotNull(updatedSource);
        Assert.NotNull(updatedDest);
        Assert.Equal(0m, updatedSource.Balance);
        Assert.Equal(1000m, updatedDest.Balance);
    }

    [Fact]
    public async Task BidirectionalHighContentionLoad_ShouldHaveZeroDeadlocks_AndPreserveAllInvariants()
    {
        var walletA = await CreateWalletAsync("Contention Alpha", 5000m);
        var walletB = await CreateWalletAsync("Contention Beta", 5000m);

        // Fire 200 opposing A -> B and B -> A transfers in parallel
        var tasks = Enumerable.Range(0, 200).Select(i =>
        {
            var isAtoB = i % 2 == 0;
            var fromId = isAtoB ? walletA.Id : walletB.Id;
            var toId = isAtoB ? walletB.Id : walletA.Id;
            return SendTransferAsync(fromId, toId, 10m, $"Bidirectional #{i}", $"bidir-{Guid.NewGuid()}");
        });

        var responses = await Task.WhenAll(tasks);

        // Zero 500s / SQL 1205 deadlocks; every request either commits (200) or hits valid insufficient funds (422)
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(200, okCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        // 1. Global double-entry zero-sum invariant: SUM(Debits) == SUM(Credits)
        var totalDebits = await db.LedgerEntries
            .Where(e => e.Type == EntryType.Debit)
            .SumAsync(e => e.Amount);

        var totalCredits = await db.LedgerEntries
            .Where(e => e.Type == EntryType.Credit)
            .SumAsync(e => e.Amount);

        Assert.Equal(totalDebits, totalCredits);

        // 2. Every wallet's cached balance must equal SUM(Credits) - SUM(Debits) for that wallet
        var wallets = await db.Wallets.AsNoTracking().ToListAsync();
        var allEntries = await db.LedgerEntries.AsNoTracking().ToListAsync();

        foreach (var wallet in wallets)
        {
            var walletCredits = allEntries
                .Where(e => e.WalletId == wallet.Id && e.Type == EntryType.Credit)
                .Sum(e => e.Amount);
            var walletDebits = allEntries
                .Where(e => e.WalletId == wallet.Id && e.Type == EntryType.Debit)
                .Sum(e => e.Amount);

            Assert.Equal(walletCredits - walletDebits, wallet.Balance);

            // 3. No customer wallet may ever have a negative balance
            if (!wallet.OwnerName.StartsWith("System Settlement Treasury"))
            {
                Assert.True(wallet.Balance >= 0m, $"Wallet {wallet.OwnerName} went negative: {wallet.Balance}");
            }
        }

        // 4. Transactional Outbox recorded an event for every committed transaction
        var txCount = await db.LedgerTransactions.CountAsync();
        var outboxCount = await db.OutboxMessages.CountAsync();
        Assert.Equal(txCount, outboxCount);
    }

    [Fact]
    public async Task Idempotency_ShouldReturnOriginalOnReplay_AndRejectPayloadMismatchWith409()
    {
        var sender = await CreateWalletAsync("Idempotency Sender", 2000m);
        var recipient = await CreateWalletAsync("Idempotency Recipient", 0m);
        var sharedKey = $"idem-race-{Guid.NewGuid()}";

        // 15 simultaneous identical requests with the exact same Idempotency-Key
        var concurrentTasks = Enumerable.Range(1, 15).Select(_ =>
            SendTransferAsync(sender.Id, recipient.Id, 250m, "Rent payment", sharedKey));

        var responses = await Task.WhenAll(concurrentTasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var payloads = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<TransferResponse>()));
        var distinctTxIds = payloads.Select(p => p!.TransactionId).Distinct().ToList();
        Assert.Single(distinctTxIds);

        // Replaying the same Idempotency-Key with a DIFFERENT amount must return 409 Conflict
        var mismatchResponse = await SendTransferAsync(sender.Id, recipient.Id, 500m, "Rent payment", sharedKey);
        Assert.Equal(HttpStatusCode.Conflict, mismatchResponse.StatusCode);

        // Balance must reflect only one R250 debit
        var updatedSender = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{sender.Id}");
        Assert.Equal(1750m, updatedSender!.Balance);
    }

    [Fact]
    public async Task AppendOnlyTrigger_ShouldBlockUpdateAndDeleteOnLedgerEntries()
    {
        var sender = await CreateWalletAsync("Immutable Sender", 500m);
        var recipient = await CreateWalletAsync("Immutable Recipient", 0m);

        var txResponse = await SendTransferAsync(sender.Id, recipient.Id, 100m, "Immutable test", $"imm-{Guid.NewGuid()}");
        txResponse.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var updateEx = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE TOP (1) LedgerEntries SET Amount = 999999.0000"));
        Assert.Equal(50001, updateEx.Number);

        var deleteEx = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE TOP (1) FROM LedgerEntries"));
        Assert.Equal(50001, deleteEx.Number);
    }

    [Fact]
    public async Task ReversalAndPagination_ShouldCreateCompensatingEntries_AndPageStatements()
    {
        var sender = await CreateWalletAsync("Reversal Sender", 1000m);
        var recipient = await CreateWalletAsync("Reversal Recipient", 100m);

        var transferResponse = await SendTransferAsync(sender.Id, recipient.Id, 300m, "Monthly settlement", $"stmt-{Guid.NewGuid()}");
        transferResponse.EnsureSuccessStatusCode();
        var transfer = await transferResponse.Content.ReadFromJsonAsync<TransferResponse>();
        Assert.NotNull(transfer);

        // Reverse the transfer via compensating double-entry transaction
        var reverseResponse = await _client.PostAsync($"/api/transfers/{transfer.TransactionId}/reverse", content: null);
        reverseResponse.EnsureSuccessStatusCode();

        // Attempting to reverse the same transaction twice must return 409 Conflict
        var secondReverse = await _client.PostAsync($"/api/transfers/{transfer.TransactionId}/reverse", content: null);
        Assert.Equal(HttpStatusCode.Conflict, secondReverse.StatusCode);

        // Balances should be back to original R1000 and R100
        var finalSender = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{sender.Id}");
        var finalRecipient = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{recipient.Id}");
        Assert.Equal(1000m, finalSender!.Balance);
        Assert.Equal(100m, finalRecipient!.Balance);

        // Sender now has 3 ledger entries (Initial funding Credit, Transfer Debit, Reversal Credit)
        var allSenderEntries = await _client.GetFromJsonAsync<List<LedgerEntryResponse>>($"/api/wallets/{sender.Id}/transactions");
        Assert.NotNull(allSenderEntries);
        Assert.Equal(3, allSenderEntries.Count);

        // Verify pagination (?page=1&pageSize=2 returns 2 entries, ?page=2&pageSize=2 returns 1 entry)
        var page1 = await _client.GetFromJsonAsync<List<LedgerEntryResponse>>($"/api/wallets/{sender.Id}/transactions?page=1&pageSize=2");
        var page2 = await _client.GetFromJsonAsync<List<LedgerEntryResponse>>($"/api/wallets/{sender.Id}/transactions?page=2&pageSize=2");
        Assert.Equal(2, page1!.Count);
        Assert.Single(page2!);

        // Unknown wallet returns 404
        var missingResponse = await _client.GetAsync($"/api/wallets/{Guid.NewGuid()}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task ValidationEdgeCases_ShouldRejectInvalidTransfersCleanly()
    {
        var zarWallet = await CreateWalletAsync("ZAR Account", 500m, "ZAR");
        var zarPeer = await CreateWalletAsync("ZAR Peer", 100m, "ZAR");
        var usdWallet = await CreateWalletAsync("USD Account", 500m, "USD");

        // 1. Insufficient funds -> 422
        var overdraft = await SendTransferAsync(zarWallet.Id, zarPeer.Id, 9999m, "Too large", $"val-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overdraft.StatusCode);

        // 2. Zero or negative amount -> 400
        var zeroAmount = await SendTransferAsync(zarWallet.Id, zarPeer.Id, 0m, "Zero", $"val-{Guid.NewGuid()}");
        var negativeAmount = await SendTransferAsync(zarWallet.Id, zarPeer.Id, -50m, "Negative", $"val-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, zeroAmount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negativeAmount.StatusCode);

        // 3. Self-transfer -> 400
        var selfTransfer = await SendTransferAsync(zarWallet.Id, zarWallet.Id, 50m, "Self", $"val-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, selfTransfer.StatusCode);

        // 4. Unknown wallet -> 404
        var unknownDest = await SendTransferAsync(zarWallet.Id, Guid.NewGuid(), 50m, "Missing", $"val-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknownDest.StatusCode);

        // 5. Cross-currency mismatch -> 400
        var crossCurrency = await SendTransferAsync(zarWallet.Id, usdWallet.Id, 50m, "FX attempt", $"val-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, crossCurrency.StatusCode);
    }

    [Fact]
    public async Task OutboxAndFraudEngine_ShouldRelayEventsAndFlagVelocityAndHighValueAlerts()
    {
        var sender = await CreateWalletAsync("Fraud Screening Sender", 25000m);
        var receiver = await CreateWalletAsync("Fraud Screening Receiver", 0m);

        // 1. Trigger HIGH_VALUE_TRANSFER (>= R10,000.00)
        var highValueTx = await SendTransferAsync(sender.Id, receiver.Id, 12500m, "Equipment settlement", $"hv-{Guid.NewGuid()}");
        highValueTx.EnsureSuccessStatusCode();

        // 2. Trigger VELOCITY_SPIKE (3rd transfer within 60 seconds from the same wallet)
        var tx2 = await SendTransferAsync(sender.Id, receiver.Id, 200m, "Rapid payment 2", $"vel2-{Guid.NewGuid()}");
        var tx3 = await SendTransferAsync(sender.Id, receiver.Id, 200m, "Rapid payment 3", $"vel3-{Guid.NewGuid()}");
        tx2.EnsureSuccessStatusCode();
        tx3.EnsureSuccessStatusCode();

        // Poll until the background OutboxRelayWorker and FraudScreeningConsumer process the events
        List<FraudAlert> walletAlerts = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(200);
            var alerts = await _client.GetFromJsonAsync<List<FraudAlert>>("/api/fraud-alerts") ?? [];
            walletAlerts = alerts.Where(a => a.SourceWalletId == sender.Id).ToList();
            if (walletAlerts.Any(a => a.RuleTriggered == "HIGH_VALUE_TRANSFER")
                && walletAlerts.Any(a => a.RuleTriggered == "VELOCITY_SPIKE"))
            {
                break;
            }
        }

        Assert.Contains(walletAlerts, a => a.RuleTriggered == "HIGH_VALUE_TRANSFER" && a.Amount == 12500m);
        Assert.Contains(walletAlerts, a => a.RuleTriggered == "VELOCITY_SPIKE");
    }

    private async Task<WalletResponse> CreateWalletAsync(string ownerName, decimal initialDeposit, string currency = "ZAR")
    {
        var response = await _client.PostAsJsonAsync("/api/wallets", new CreateWalletRequest(ownerName, currency, initialDeposit));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
    }

    private async Task<HttpResponseMessage> SendTransferAsync(
        Guid sourceId,
        Guid destinationId,
        decimal amount,
        string description,
        string idempotencyKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/transfers");
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        message.Content = JsonContent.Create(new TransferRequest(sourceId, destinationId, amount, description));
        return await _client.SendAsync(message);
    }
}



