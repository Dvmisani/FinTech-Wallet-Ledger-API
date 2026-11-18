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
            var correlationId = HttpContext.TraceIdentifier;
            var result = await _transferService.TransferAsync(request, idempotencyKey ?? string.Empty, correlationId, ct);
            return Ok(result);
        }
        catch (TransferException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<ActionResult<TransferResponse>> ReverseTransfer(Guid id, CancellationToken ct)
    {
        try
        {
            var correlationId = HttpContext.TraceIdentifier;
            var result = await _transferService.ReverseTransferAsync(id, correlationId, ct);
            return Ok(result);
        }
        catch (TransferException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }
}
