using System.Threading.RateLimiting;
using FinTechWallet.Api.Data;
using FinTechWallet.Api.Messaging;
using FinTechWallet.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null)));

builder.Services.AddScoped<TransferService>();
builder.Services.AddSingleton<IOutboxEventBus, ChannelOutboxEventBus>();
builder.Services.AddHostedService<OutboxRelayWorker>();
builder.Services.AddHostedService<FraudScreeningConsumer>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "local";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await db.Database.EnsureCreatedAsync();

        await db.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH('LedgerTransactions', 'RequestHash') IS NULL
                ALTER TABLE LedgerTransactions ADD RequestHash nvarchar(64) NOT NULL DEFAULT '';

            IF COL_LENGTH('LedgerTransactions', 'ReversedTransactionId') IS NULL
                ALTER TABLE LedgerTransactions ADD ReversedTransactionId uniqueidentifier NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LedgerTransactions_ReversedTransactionId')
                CREATE UNIQUE INDEX IX_LedgerTransactions_ReversedTransactionId
                ON LedgerTransactions (ReversedTransactionId)
                WHERE ReversedTransactionId IS NOT NULL;

            ALTER TABLE Wallets ALTER COLUMN Balance decimal(19,4) NOT NULL;
            ALTER TABLE LedgerEntries ALTER COLUMN Amount decimal(19,4) NOT NULL;

            IF OBJECT_ID('OutboxMessages', 'U') IS NULL
            BEGIN
                CREATE TABLE OutboxMessages (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    EventType nvarchar(100) NOT NULL,
                    PayloadJson nvarchar(max) NOT NULL,
                    CorrelationId nvarchar(64) NOT NULL,
                    CreatedAtUtc datetime2 NOT NULL,
                    PublishedAtUtc datetime2 NULL,
                    DeadLetteredAtUtc datetime2 NULL,
                    RetryCount int NOT NULL DEFAULT 0,
                    LastError nvarchar(max) NULL
                );
            END
            ELSE IF COL_LENGTH('OutboxMessages', 'DeadLetteredAtUtc') IS NULL
            BEGIN
                ALTER TABLE OutboxMessages ADD DeadLetteredAtUtc datetime2 NULL;
            END;

            IF OBJECT_ID('FraudAlerts', 'U') IS NULL
            BEGIN
                CREATE TABLE FraudAlerts (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    TransactionId uniqueidentifier NOT NULL,
                    SourceWalletId uniqueidentifier NOT NULL,
                    DestinationWalletId uniqueidentifier NOT NULL,
                    Amount decimal(19,4) NOT NULL,
                    Currency nvarchar(3) NOT NULL,
                    RuleTriggered nvarchar(80) NOT NULL,
                    RiskScore int NOT NULL,
                    Status nvarchar(30) NOT NULL,
                    CorrelationId nvarchar(64) NOT NULL,
                    CreatedAtUtc datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX IX_FraudAlerts_TransactionId_RuleTriggered
                ON FraudAlerts (TransactionId, RuleTriggered);
            END;
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR ALTER TRIGGER TR_LedgerEntries_AppendOnly
            ON LedgerEntries
            INSTEAD OF UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 50001, 'LedgerEntries are append-only and cannot be updated or deleted.', 1;
            END;
            """);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed on startup.");
    }
}

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
    }

    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.Use(async (context, next) =>
{
    var configuredApiKey = app.Configuration["DemoSecurity:ApiKey"];
    if (!string.IsNullOrWhiteSpace(configuredApiKey)
        && context.Request.Path.StartsWithSegments("/api")
        && HttpMethods.IsPost(context.Request.Method))
    {
        var providedKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.Equals(providedKey, configuredApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Valid X-Api-Key header is required." });
            return;
        }
    }

    await next();
});

app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
