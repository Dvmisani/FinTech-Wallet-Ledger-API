using FinTechWallet.Api.DTOs;
using FinTechWallet.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinTechWallet.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WalletsController : ControllerBase
{
    private readonly TransferService _transferService;

    public WalletsController(TransferService transferService)
    {
        _transferService = transferService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WalletResponse>>> GetWallets(CancellationToken ct)
    {
        var wallets = await _transferService.GetWalletsAsync(ct);
        return Ok(wallets);
    }

    [HttpPost]
    public async Task<ActionResult<WalletResponse>> CreateWallet([FromBody] CreateWalletRequest request, CancellationToken ct)
    {
        try
        {
            var wallet = await _transferService.CreateWalletAsync(request, ct);
            return CreatedAtAction(nameof(GetWallet), new { id = wallet.Id }, wallet);
        }
        catch (TransferException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WalletResponse>> GetWallet(Guid id, CancellationToken ct)
    {
        var wallet = await _transferService.GetWalletAsync(id, ct);
        return wallet is null ? NotFound(new { error = "Wallet not found." }) : Ok(wallet);
    }

    [HttpGet("{id:guid}/transactions")]
    public async Task<ActionResult<IReadOnlyList<LedgerEntryResponse>>> GetWalletTransactions(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var entries = await _transferService.GetWalletTransactionsAsync(id, page, pageSize, ct);
        return entries is null ? NotFound(new { error = "Wallet not found." }) : Ok(entries);
    }
}
