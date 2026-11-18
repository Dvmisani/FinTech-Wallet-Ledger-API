# Double-Entry Wallet Ledger & Outbox Fraud Screening API

[![Build, Test & Deploy to Azure](https://github.com/dvmisani/FinTech-Wallet-Ledger-API/actions/workflows/azure-deploy.yml/badge.svg)](https://github.com/dvmisani/FinTech-Wallet-Ledger-API/actions/workflows/azure-deploy.yml)

An ACID-compliant, double-entry digital wallet ledger and event-driven fraud screening backend built with **C# / .NET 10**, **ASP.NET Core**, **Entity Framework Core**, and **SQL Server**, deployed on **Microsoft Azure**.

- **Live Cloud Demo (Azure):** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net
- **Interactive Swagger API Docs:** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net/swagger
- **Health Check Endpoint:** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net/health

---

## Transfer & Outbox Execution Flow

```text
POST /api/transfers (Idempotency-Key + X-Correlation-ID)
  |
  +-> 1. Compute SHA-256 RequestHash (Source|Dest|Amount|Description)
  |       +-> Key exists + hash matches  --> Return original 200 TransferResponse
  |       +-> Key exists + hash differs  --> Return 409 Conflict
  |
  +-> 2. BEGIN ATOMIC SQL TRANSACTION (CreateExecutionStrategy)
          +-> Lock lower Wallet.Id  : SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE Id = @firstId
          +-> Lock higher Wallet.Id : SELECT * FROM Wallets WITH (UPDLOCK, ROWLOCK) WHERE Id = @secondId
          +-> Validate currency match & Source.Balance >= Amount (else 422)
          +-> Write balanced LedgerEntries (1 Debit + 1 Credit, decimal(19,4))
          +-> Insert TransferCompletedEvent into OutboxMessages
          +-> COMMIT TRANSACTION
                |
                v
      OutboxRelayWorker (WITH UPDLOCK, READPAST, ROWLOCK)
                |
                v
      FraudScreeningConsumer (Idempotent by EventId)
          +-> Amount >= R10,000.00           --> Flag HIGH_VALUE_TRANSFER
          +-> >= 3 transfers in 60s window   --> Flag VELOCITY_SPIKE
```

---

## Key Engineering Guarantees

- **Double-Entry Accounting & `decimal(19,4)` Precision:** Every transfer generates paired `Debit` and `Credit` `LedgerEntry` rows linked to a single `LedgerTransaction` inside an atomic database transaction. Monetary columns use `decimal(19,4)` alongside explicit 3-letter ISO currency codes.
- **Deterministic Sequential Row Locking:** Target wallets are sorted by `Guid` and locked via two sequential `SELECT ... WITH (UPDLOCK, ROWLOCK) WHERE Id = @id` statements, preventing circular deadlocks (`SQL 1205`) regardless of SQL Server query plan selection.
- **Payload-Hash Idempotency & Race Protection:** Enforces `Idempotency-Key` headers backed by a unique database index and `SHA-256` payload hashing. Identical concurrent retries return the original `TransferResponse`; reusing a key with an altered payload returns `409 Conflict`.
- **Append-Only Ledger & Compensating Reversals:** A SQL Server `INSTEAD OF UPDATE, DELETE` trigger (`TR_LedgerEntries_AppendOnly`) rejects any attempt to mutate or delete historical ledger rows (`SQL Error 50001`). Mistakes are corrected exclusively via compensating double-entry reversals (`POST /api/transfers/{id}/reverse`).
- **Transactional Outbox & Fraud Screening Engine:** Committed transfers atomically persist `TransferCompletedEvent` records into `OutboxMessages`. A background relay (`WITH (UPDLOCK, READPAST, ROWLOCK)`) dispatches events with retry/dead-letter tracking to an idempotent fraud consumer screening for velocity spikes and high-value anomalies.
- **Observability & Demo Protection:** Propagates `X-Correlation-ID` across HTTP headers, structured logger scopes, and outbox events, protected by fixed-window rate limiting and optional `X-Api-Key` enforcement.

---

## Verification Benchmarks (`Testcontainers` + SQL Server 2022)

All 7 integration suites run automatically against an ephemeral SQL Server 2022 Docker container in CI/CD:

| Test Suite | Scenario Verified | Outcome |
| :--- | :--- | :--- |
| **Bidirectional High-Contention Load** | **200 opposing `A -> B` and `B -> A` transfers** fired in parallel | **0 deadlocks (`SQL 1205`)**, `SUM(Debits) == SUM(Credits)`, 0 negative customer balances |
| **Concurrent Overdraft Prevention** | 20 simultaneous `R100.00` transfers against a `R1,000.00` balance | Exactly 10 `200 OK`, 10 `422 Unprocessable Entity`, final balance `R0.00` |
| **Idempotency Race & Payload Mismatch** | 15 simultaneous identical requests + replay with altered amount | Single `TransactionId` returned to all 15 callers; altered payload rejected with `409 Conflict` |
| **Append-Only Trigger Enforcement** | Direct SQL `UPDATE` and `DELETE` against `LedgerEntries` | Blocked by `TR_LedgerEntries_AppendOnly` with SQL error `50001` |
| **Compensating Reversal & Pagination** | Reverse transfer, block duplicate reversal, and page statements | Balanced reversal entries created; second reversal rejected with `409 Conflict` |
| **Outbox & Fraud Screening Pipeline** | High-value (`R12,500.00`) + 3 rapid transfers within 60s | Relayed asynchronously and flagged as `HIGH_VALUE_TRANSFER` and `VELOCITY_SPIKE` |
| **Domain Validation Edge Cases** | Insufficient funds, zero/negative amounts, self-transfer, unknown wallet, FX mismatch | Clean `400` / `404` / `422` responses with zero ledger corruption |

---

## Known Trade-Offs & Production Roadmap

- **Treasury Hot-Row Contention:** Initial wallet deposits debit a per-currency `System Settlement Treasury` account (`00000000-0000-0000-0000-000000000001` for ZAR), which serializes concurrent initial deposits globally. In a high-throughput production deployment, this would be mitigated by sharding treasury accounts across `N` partitions or batching settlement top-ups.
- **External Broker Scaling:** The `IOutboxEventBus` abstraction currently runs an in-process `Channel<OutboxEnvelope>` broker on Azure App Service and swaps cleanly to **Azure Service Bus** or **RabbitMQ** for multi-service fan-out.

---

## API Endpoints

| Method | Route | Description |
| :--- | :--- | :--- |
| `GET` | `/api/wallets` | Lists active customer wallets and balances |
| `POST` | `/api/wallets` | Opens a new wallet and settles any initial deposit from treasury |
| `GET` | `/api/wallets/{id}` | Retrieves a single wallet by ID |
| `GET` | `/api/wallets/{id}/transactions?page=1&pageSize=50` | Returns paginated double-entry ledger entries for a wallet |
| `POST` | `/api/transfers` | Executes an atomic, idempotent peer-to-peer transfer (`Idempotency-Key` header required) |
| `POST` | `/api/transfers/{id}/reverse` | Issues a compensating double-entry reversal for a committed transfer |
| `GET` | `/api/fraud-alerts` | Lists transactions flagged by the asynchronous fraud screening consumer |
| `GET` | `/api/outbox/stats` | Returns transactional outbox delivery and dead-letter metrics |
| `GET` | `/health` | Database and application health check |

---

## Running Locally

```bash
# Run all 7 Testcontainers integration suites (requires Docker Desktop)
dotnet test

# Run the API and browser dashboard locally
dotnet run --project src/FinTechWallet.Api
```
