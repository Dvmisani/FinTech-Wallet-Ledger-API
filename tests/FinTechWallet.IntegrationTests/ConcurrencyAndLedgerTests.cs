using System.Net;
using System.Net.Http.Json;
using FinTechWallet.Api.Data;
using FinTechWallet.Api.Domain;
using FinTechWallet.Api.DTOs;
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
        // Arrange: Fund source wallet with R1,000.00 and destination wallet with R0.00
        var source = await CreateWalletAsync("Thabo Mokoena", 1000m);
        var destination = await CreateWalletAsync("Lerato Ndlovu", 0m);

        // Act: Fire 20 simultaneous R100.00 transfer requests (R2,000.00 total attempted)
        var tasks = Enumerable.Range(1, 20).Select(i =>
            SendTransferAsync(
                source.Id,
                destination.Id,
                100m,
                $"Parallel withdrawal #{i}",
                $"race-test-{Guid.NewGuid()}"));

        var responses = await Task.WhenAll(tasks);

        // Assert 1: Exactly 10 succeed (200 OK) and 10 are rejected (422 Unprocessable Entity)
        var succeededCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejectedCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(10, succeededCount);
        Assert.Equal(10, rejectedCount);

        // Assert 2: Source wallet is R0.00 (never negative) and destination has R1,000.00
        var updatedSource = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{source.Id}");
        var updatedDestination = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{destination.Id}");

        Assert.NotNull(updatedSource);
        Assert.NotNull(updatedDestination);
        Assert.Equal(0m, updatedSource.Balance);
        Assert.Equal(1000m, updatedDestination.Balance);

        // Assert 3: Verify the global Double-Entry Zero-Sum Invariant directly in SQL Server
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var totalDebits = await db.LedgerEntries
            .Where(e => e.Type == EntryType.Debit)
            .SumAsync(e => e.Amount);

        var totalCredits = await db.LedgerEntries
            .Where(e => e.Type == EntryType.Credit)
            .SumAsync(e => e.Amount);

        Assert.Equal(totalDebits, totalCredits);
    }

    [Fact]
    public async Task ConcurrentDuplicateIdempotencyKeys_ShouldCommitOnlyOnce()
    {
        // Arrange
        var source = await CreateWalletAsync("Sipho Khumalo", 500m);
        var destination = await CreateWalletAsync("Naledi Dlamini", 0m);
        var sharedIdempotencyKey = $"idem-{Guid.NewGuid()}";

        // Act: Replay 10 identical transfer requests simultaneously with the same Idempotency-Key
        var tasks = Enumerable.Range(1, 10).Select(_ =>
            SendTransferAsync(
                source.Id,
                destination.Id,
                200m,
                "Idempotent payment retry",
                sharedIdempotencyKey));

        var responses = await Task.WhenAll(tasks);

        // Assert: All return 200 OK with the exact same TransactionId, and balance is debited once
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var receipts = new List<TransferResponse>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<TransferResponse>();
            Assert.NotNull(body);
            receipts.Add(body);
        }

        Assert.Single(receipts.Select(r => r.TransactionId).Distinct());

        var updatedSource = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{source.Id}");
        Assert.NotNull(updatedSource);
        Assert.Equal(300m, updatedSource.Balance);
    }

    [Fact]
    public async Task BidirectionalConcurrentTransfers_ShouldCompleteWithoutDeadlocks()
    {
        // Arrange: Two wallets sending funds to each other at the exact same time
        var walletA = await CreateWalletAsync("Wallet A", 1000m);
        var walletB = await CreateWalletAsync("Wallet B", 1000m);

        // Act: 10 transfers A -> B and 10 transfers B -> A simultaneously
        var aToB = Enumerable.Range(1, 10).Select(i =>
            SendTransferAsync(walletA.Id, walletB.Id, 10m, $"A->B #{i}", $"ab-{Guid.NewGuid()}"));

        var bToA = Enumerable.Range(1, 10).Select(i =>
            SendTransferAsync(walletB.Id, walletA.Id, 10m, $"B->A #{i}", $"ba-{Guid.NewGuid()}"));

        var responses = await Task.WhenAll(aToB.Concat(bToA));

        // Assert: Zero SQL deadlocks; all 20 succeed and net balances remain R1,000.00 each
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var finalA = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{walletA.Id}");
        var finalB = await _client.GetFromJsonAsync<WalletResponse>($"/api/wallets/{walletB.Id}");

        Assert.NotNull(finalA);
        Assert.NotNull(finalB);
        Assert.Equal(1000m, finalA.Balance);
        Assert.Equal(1000m, finalB.Balance);
    }

    [Fact]
    public async Task GetWalletTransactions_ShouldReturnBalancedDoubleEntryStatement()
    {
        var sender = await CreateWalletAsync("Sender Statement", 1000m);
        var recipient = await CreateWalletAsync("Recipient Statement", 250m);

        var transferRequest = new TransferRequest(
            sender.Id,
            recipient.Id,
            300m,
            "Monthly settlement");

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(transferRequest)
        };
        message.Headers.Add("Idempotency-Key", $"stmt-{Guid.NewGuid()}");

        var transferResponse = await _client.SendAsync(message);
        transferResponse.EnsureSuccessStatusCode();

        // Verify sender statement shows initial deposit (Credit) + transfer (Debit)
        var senderTxResponse = await _client.GetAsync($"/api/wallets/{sender.Id}/transactions");
        senderTxResponse.EnsureSuccessStatusCode();
        var senderEntries = await senderTxResponse.Content.ReadFromJsonAsync<List<LedgerEntryResponse>>();

        Assert.NotNull(senderEntries);
        Assert.Equal(2, senderEntries.Count);

        var senderDebit = Assert.Single(senderEntries, e => e.Type == "Debit");
        Assert.Equal(300m, senderDebit.Amount);
        Assert.Equal("Recipient Statement", senderDebit.CounterpartyName);
        Assert.Equal("Monthly settlement", senderDebit.Description);

        var senderInitCredit = Assert.Single(senderEntries, e => e.Type == "Credit");
        Assert.Equal(1000m, senderInitCredit.Amount);
        Assert.Equal("System Settlement Treasury", senderInitCredit.CounterpartyName);

        // Verify recipient statement shows initial deposit (Credit) + incoming transfer (Credit)
        var recipientEntries = await _client.GetFromJsonAsync<List<LedgerEntryResponse>>(
            $"/api/wallets/{recipient.Id}/transactions");

        Assert.NotNull(recipientEntries);
        Assert.Equal(2, recipientEntries.Count);
        var incomingCredit = Assert.Single(recipientEntries, e => e.Description == "Monthly settlement");
        Assert.Equal("Credit", incomingCredit.Type);
        Assert.Equal(300m, incomingCredit.Amount);
        Assert.Equal("Sender Statement", incomingCredit.CounterpartyName);

        // Verify unknown wallet returns 404
        var missingResponse = await _client.GetAsync($"/api/wallets/{Guid.NewGuid()}/transactions");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    private async Task<WalletResponse> CreateWalletAsync(string ownerName, decimal initialDeposit)
    {
        var response = await _client.PostAsJsonAsync("/api/wallets", new CreateWalletRequest(ownerName, "ZAR", initialDeposit));
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