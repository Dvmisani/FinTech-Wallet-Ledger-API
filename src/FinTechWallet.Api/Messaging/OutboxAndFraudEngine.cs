using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FinTechWallet.Api.Data;
using FinTechWallet.Api.Domain;
using FinTechWallet.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace FinTechWallet.Api.Messaging;

public record OutboxEnvelope(
    Guid MessageId,
    string EventType,
    string PayloadJson,
    string CorrelationId,
    DateTime CreatedAtUtc);

public record TransferEventPayload(
    Guid EventId,
    string IdempotencyKey,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string Currency,
    string Description,
    Guid? ReversedTransactionId,
    DateTime OccurredAtUtc);

public interface IOutboxEventBus
{
    ValueTask PublishAsync(OutboxEnvelope envelope, CancellationToken ct = default);
    IAsyncEnumerable<OutboxEnvelope> ReadAllAsync(CancellationToken ct = default);
}

public sealed class ChannelOutboxEventBus : IOutboxEventBus
{
    private readonly Channel<OutboxEnvelope> _channel = Channel.CreateUnbounded<OutboxEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask PublishAsync(OutboxEnvelope envelope, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(envelope, ct);

    public IAsyncEnumerable<OutboxEnvelope> ReadAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);
}

public sealed class OutboxRelayWorker : BackgroundService
{
    private const int MaxRetries = 5;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboxEventBus _eventBus;
    private readonly ILogger<OutboxRelayWorker> _logger;

    public OutboxRelayWorker(
        IServiceScopeFactory scopeFactory,
        IOutboxEventBus eventBus,
        ILogger<OutboxRelayWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await RelayBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxRelayWorker encountered an error while polling OutboxMessages.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task<int> RelayBatchAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // READPAST skips rows locked by concurrent worker replicas so events are never double-published
            var batch = await db.OutboxMessages
                .FromSqlRaw("""
                    SELECT TOP (25) *
                    FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE PublishedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL
                    ORDER BY CreatedAtUtc ASC
                    """)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }

            foreach (var msg in batch)
            {
                try
                {
                    await _eventBus.PublishAsync(
                        new OutboxEnvelope(msg.Id, msg.EventType, msg.PayloadJson, msg.CorrelationId, msg.CreatedAtUtc),
                        ct);

                    msg.PublishedAtUtc = DateTime.UtcNow;
                    msg.LastError = null;
                }
                catch (Exception ex)
                {
                    msg.RetryCount += 1;
                    msg.LastError = ex.Message;

                    if (msg.RetryCount >= MaxRetries)
                    {
                        msg.DeadLetteredAtUtc = DateTime.UtcNow;
                        _logger.LogWarning("OutboxMessage {MessageId} moved to Dead-Letter state after {Retries} retries.",
                            msg.Id, msg.RetryCount);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return batch.Count;
        });
    }
}

public sealed class FraudScreeningConsumer : BackgroundService
{
    public const decimal HighValueThreshold = 10_000m;
    public const int VelocityWindowSeconds = 60;
    public const int VelocitySpikeThreshold = 3;

    private readonly IOutboxEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FraudScreeningConsumer> _logger;
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _slidingWindows = new();

    public FraudScreeningConsumer(
        IOutboxEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        ILogger<FraudScreeningConsumer> logger)
    {
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _eventBus.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ScreenEnvelopeAsync(envelope, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FraudScreeningConsumer failed processing OutboxMessage {MessageId}.", envelope.MessageId);
            }
        }
    }

    public async Task ScreenEnvelopeAsync(OutboxEnvelope envelope, CancellationToken ct = default)
    {
        if (!string.Equals(envelope.EventType, "TransferCompletedEvent", StringComparison.Ordinal))
            return;

        var payload = JsonSerializer.Deserialize<TransferEventPayload>(envelope.PayloadJson);
        if (payload is null)
            return;

        // Exclude initial System Settlement Treasury funding deposits
        if (payload.IdempotencyKey.StartsWith("init-", StringComparison.Ordinal)
            || payload.SourceWalletId == TransferService.SystemTreasuryWalletId)
        {
            return;
        }

        var alertsToCreate = new List<(string Rule, int Score)>();

        if (payload.Amount >= HighValueThreshold)
        {
            alertsToCreate.Add(("HIGH_VALUE_TRANSFER", 80));
        }

        var recentCount = RecordAndCountVelocity(payload.SourceWalletId, payload.OccurredAtUtc);
        if (recentCount >= VelocitySpikeThreshold)
        {
            alertsToCreate.Add(("VELOCITY_SPIKE", 90));
        }

        if (alertsToCreate.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        foreach (var (rule, score) in alertsToCreate)
        {
            var alreadyExists = await db.FraudAlerts
                .AsNoTracking()
                .AnyAsync(a => a.TransactionId == payload.EventId && a.RuleTriggered == rule, ct);

            if (alreadyExists)
                continue;

            db.FraudAlerts.Add(new FraudAlert
            {
                TransactionId = payload.EventId,
                SourceWalletId = payload.SourceWalletId,
                DestinationWalletId = payload.DestinationWalletId,
                Amount = payload.Amount,
                Currency = payload.Currency,
                RuleTriggered = rule,
                RiskScore = score,
                CorrelationId = envelope.CorrelationId
            });

            _logger.LogWarning(
                "Fraud alert {Rule} triggered for Transaction {TransactionId} (Source={SourceWalletId}, Amount={Amount} {Currency}, CorrelationId={CorrelationId})",
                rule, payload.EventId, payload.SourceWalletId, payload.Amount, payload.Currency, envelope.CorrelationId);
        }

        await db.SaveChangesAsync(ct);
    }

    private int RecordAndCountVelocity(Guid walletId, DateTime occurredAtUtc)
    {
        var queue = _slidingWindows.GetOrAdd(walletId, _ => new Queue<DateTime>());
        lock (queue)
        {
            var cutoff = occurredAtUtc.AddSeconds(-VelocityWindowSeconds);
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            queue.Enqueue(occurredAtUtc);
            return queue.Count;
        }
    }
}

