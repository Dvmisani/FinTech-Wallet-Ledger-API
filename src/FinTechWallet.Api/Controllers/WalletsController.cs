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
}
