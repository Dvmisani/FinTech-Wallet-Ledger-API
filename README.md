# Enterprise FinTech Wallet & Double-Entry Ledger API

[![Build, Test & Deploy to Azure](https://github.com/dvmisani/FinTech-Wallet-Ledger-API/actions/workflows/azure-deploy.yml/badge.svg)](https://github.com/dvmisani/FinTech-Wallet-Ledger-API/actions/workflows/azure-deploy.yml)

A financial wallet and double-entry accounting ledger API built with **C# / .NET 10** and **Microsoft SQL Server**, engineered to enforce zero-sum ledger integrity, idempotent retries, and race-condition safety under concurrent load.

## Live Cloud Demo (Azure)
- **Interactive Wallet Dashboard:** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net
- **Interactive Swagger UI:** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net/swagger
- **Health Check:** https://fintech-wallet-ledger-dvmisani-d6a6bjc4ctahbjda.brazilsouth-01.azurewebsites.net/health

## Engineering Architecture & Trade-Offs
- **Double-Entry Accounting Model:** Every transfer generates paired `Debit` and `Credit` `LedgerEntry` rows linked to a single `LedgerTransaction` inside an atomic database transaction. Initial wallet funding debits a dedicated system treasury account (`00000000-0000-0000-0000-000000000001`) so total debits always equal total credits across the entire ledger.
- **Pessimistic Row-Level Locking (`UPDLOCK`, `ROWLOCK`):** Under high contention on the same wallet, optimistic concurrency causes high retry failure rates. Locking target wallet rows with `WITH (UPDLOCK, ROWLOCK)` serializes conflicting balance mutations safely at the database row level.
- **Deterministic Lock Ordering:** Wallet IDs are sorted (`ORDER BY Id`) before acquiring row locks, preventing circular deadlocks when two wallets transfer funds to each other simultaneously.
- **Idempotency & Transient Retry Safety:** Enforces `Idempotency-Key` headers backed by a unique database index and an EF Core `ExecutionStrategy` retry wrapper (`ChangeTracker.Clear()`), guaranteeing duplicate network retries never double-debit an account.
- **Automated Integration Testing (`Testcontainers`):** CI/CD runs xUnit integration tests against an ephemeral SQL Server Docker container (`Testcontainers.MsSql`) before deploying to Azure.

## API Endpoints

| Method | Route | Description |
| :--- | :--- | :--- |
| `GET` | `/api/wallets` | Lists active customer wallets and balances |
| `POST` | `/api/wallets` | Opens a new wallet and settles any initial deposit from treasury |
| `GET` | `/api/wallets/{id}` | Retrieves a single wallet by ID |
| `GET` | `/api/wallets/{id}/transactions` | Returns persisted double-entry ledger entries for a wallet |
| `POST` | `/api/transfers` | Executes an atomic, idempotent peer-to-peer transfer (`Idempotency-Key` header required) |
| `GET` | `/health` | Database and application health check |

## Running Locally

```bash
# Run integration tests (requires Docker Desktop)
dotnet test

# Run the API and browser dashboard locally
dotnet run --project src/FinTechWallet.Api

