using FinTechWallet.Api.DTOs;
using FinTechWallet.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinTechWallet.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransfersController : ControllerBase
{
    private readonly TransferService _transferService;

    public TransfersController(TransferService transferService)
    {
        _transferService = transferService;
    }

    [HttpPost]
    public async Task<ActionResult<TransferResponse>> CreateTransfer(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] TransferRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _transferService.TransferAsync(request, idempotencyKey ?? string.Empty, ct);
            return Ok(result);
        }
        catch (TransferException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }
}
