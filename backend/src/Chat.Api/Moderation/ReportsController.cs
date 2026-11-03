using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Moderation;

[Route("reports"), Authorize]
public class ReportsController(ReportService reports) : ApiControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Submit(SubmitReportRequest request, CancellationToken ct)
    {
        var errors = ReportService.Validate(request, reports.MaxDetails);
        if (errors.Count > 0) return Invalid(errors);

        var result = await reports.SubmitAsync(UserId, request, ct);
        if (result.Error is { } error) return Problem(error);
        return result.Value!.Created
            ? Created($"/reports/{result.Value.Report.Id}", result.Value.Report)
            : Ok(result.Value.Report);
    }
}
