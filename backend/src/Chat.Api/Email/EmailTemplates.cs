namespace Chat.Api.Email;

public static class EmailTemplates
{
    public static (string Subject, string Body) VerifyEmail(string code) =>
        ("Verify your email", $"Your verification code is {code}. It expires in 15 minutes.");

    public static (string Subject, string Body) ResetPassword(string code) =>
        ("Reset your password", $"Your password reset code is {code}. It expires in 15 minutes. If you did not ask for it, ignore this email.");
}
