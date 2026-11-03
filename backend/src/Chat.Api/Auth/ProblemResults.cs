using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Chat.Api.Auth;

/// <summary>The API's error shape: ProblemDetails with a <c>code</c>, for controllers, filters and the rate limiter.</summary>
public static class ProblemResults
{
    public const string ValidationFailed = "validation_failed";

    public static ProblemDetails Details(HttpContext http, AuthError e)
    {
        var details = Factory(http).CreateProblemDetails(http, e.Status, e.Title);
        details.Extensions["code"] = e.Code;
        return details;
    }

    public static ObjectResult Problem(HttpContext http, AuthError e) => new(Details(http, e)) { StatusCode = e.Status };

    public static ObjectResult Invalid(HttpContext http, Dictionary<string, string[]> errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (key, messages) in errors)
            foreach (var message in messages) modelState.AddModelError(key, message);
        return Invalid(http, modelState);
    }

    /// <summary>Also what MVC returns when a body or a parameter cannot be bound (see <c>InvalidModelStateResponseFactory</c>).</summary>
    public static ObjectResult Invalid(HttpContext http, ModelStateDictionary modelState)
    {
        var details = Factory(http).CreateValidationProblemDetails(http, modelState, StatusCodes.Status400BadRequest);
        details.Extensions["code"] = ValidationFailed;
        return new BadRequestObjectResult(details);
    }

    private static ProblemDetailsFactory Factory(HttpContext http) => http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
}
