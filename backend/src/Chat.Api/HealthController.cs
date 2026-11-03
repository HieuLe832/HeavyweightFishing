using Chat.Data;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api;

[Route("health")]
public class HealthController(ChatDbContext db) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Ok(new { status = "ok" }) : StatusCode(StatusCodes.Status503ServiceUnavailable);
}
