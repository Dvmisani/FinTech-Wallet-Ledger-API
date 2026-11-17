using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MsSql;

namespace FinTechWallet.IntegrationTests;

public class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Inject the ephemeral Docker SQL Server connection string before Program.cs runs
        builder.UseSetting("ConnectionStrings:DefaultConnection", _sqlContainer.GetConnectionString());
    }

    public async Task InitializeAsync()
    {
        await _sqlContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _sqlContainer.DisposeAsync();
    }
}