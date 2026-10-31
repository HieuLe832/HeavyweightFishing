namespace Chat.Api.Auth;

public static class ProblemResults
{
    public static IResult Problem(AuthError e) =>
        Results.Problem(statusCode: e.Status, title: e.Title, extensions: new Dictionary<string, object?> { ["code"] = e.Code });

    public static IResult Invalid(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
}
