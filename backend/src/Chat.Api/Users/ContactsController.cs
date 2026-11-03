using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Chat.Api.Users;

[Route("contacts"), Authorize]
public class ContactsController(ContactService contacts) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await contacts.ListContactsAsync(UserId, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Save(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ContactRequest? request, CancellationToken ct)
    {
        var errors = UserValidation.Validate(request);
        if (errors.Count > 0) return Invalid(errors);

        return OkOr(await contacts.SaveContactAsync(UserId, id, request, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await contacts.RemoveContactAsync(UserId, id, ct);
        return NoContent();
    }
}
