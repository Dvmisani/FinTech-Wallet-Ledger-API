using FinTechWallet.Api.Data;
using FinTechWallet.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTechWallet.Api.Controllers;

[ApiController]
[Route("api")]
public class FraudAndOutboxController : ControllerBase
{
    private readonly LedgerDbContext _db;

    public FraudAndOutboxController(LedgerDbContext db)
    {
        _db = db;
    }

    [HttpGet("fraud-alerts")]
    public async Task<ActionResult<IReadOnlyList<FraudAlert>>> GetFraudAlerts(CancellationToken ct)
    {
        var alerts = await _db.FraudAlerts
            .AsNoTracking()
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return Ok(alerts);
    }

    [HttpGet("outbox/stats")]
    public async Task<ActionResult<object>> GetOutboxStats(CancellationToken ct)
    {
        var pending = await _db.OutboxMessages.CountAsync(o => o.PublishedAtUtc == null && o.DeadLetteredAtUtc == null, ct);
        var published = await _db.OutboxMessages.CountAsync(o => o.PublishedAtUtc != null, ct);
        var deadLettered = await _db.OutboxMessages.CountAsync(o => o.DeadLetteredAtUtc != null, ct);
        var alerts = await _db.FraudAlerts.CountAsync(ct);

        return Ok(new { pending, published, deadLettered, fraudAlerts = alerts });
    }
}
