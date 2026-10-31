using Chat.Data;
using Microsoft.EntityFrameworkCore;
using static Chat.Api.Tests.TestHelpers;

namespace Chat.Api.Tests;

public static class ModerationTestHelpers
{
    /// <summary>A verified account promoted to admin in the database, signed in again so its token carries the role.</summary>
    public static async Task<Person> CreateAdminAsync(this ApiFixture api)
    {
        var person = await api.CreatePersonAsync();
        await api.WithDbAsync(db => db.Users.Where(u => u.Id == person.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin)));
        var login = await api.Client.LoginAsync(person.Username);
        login.EnsureSuccessStatusCode();
        return person with { Token = (await login.JsonAsync()).GetProperty("accessToken").GetString()! };
    }

    public static Task<HttpResponseMessage> ReportAsync(this ApiFixture api, Person who, object body) =>
        api.Client.PostAsync(who, "/reports", body);

    /// <summary>Files a report about a message and returns the new report's id.</summary>
    public static async Task<Guid> ReportedMessageAsync(this ApiFixture api, Person reporter, Guid messageId, string reason = "spam")
    {
        var response = await api.ReportAsync(reporter, new { messageId, reason });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static Task<List<Chat.Data.Entities.AuditLog>> AuditEntriesAsync(this ApiFixture api, string action, string targetId) =>
        api.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action && a.TargetId == targetId).OrderBy(a => a.Id).ToListAsync());
}
