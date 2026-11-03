using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Users;

[Route("blocks"), Authorize]
public class BlocksController(ContactService contacts) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await contacts.ListBlocksAsync(UserId, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Block(Guid id, CancellationToken ct) => NoContentOr(await contacts.BlockAsync(UserId, id, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        await contacts.UnblockAsync(UserId, id, ct);
        return NoContent();
    }
}
