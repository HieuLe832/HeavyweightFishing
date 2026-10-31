using Chat.Data;

namespace Chat.Api.Conversations;

public record CreateDirectRequest(Guid? UserId);

public record CreateGroupRequest(string? Title, List<Guid>? MemberIds);

public record RenameRequest(string? Title);

public record AddMembersRequest(List<Guid>? UserIds);

public record SetRoleRequest(string? Role);

public record MuteRequest(DateTimeOffset? Until);

public record PersonBrief(Guid Id, string Username, string DisplayName);

/// <summary>One conversation as its member sees it. <c>Peer</c> is the other person in a direct chat.</summary>
public record ConversationSummary(
    Guid Id, string Type, string? Title, PersonBrief? Peer, long LastSeq, long Unread,
    DateTimeOffset? MutedUntil, string Role, DateTimeOffset UpdatedAt);

public record MemberSummary(Guid UserId, string Username, string DisplayName, string Role, DateTimeOffset JoinedAt, long LastDeliveredSeq, long LastReadSeq);

public record ConversationDetail(ConversationSummary Conversation, List<MemberSummary> Members);

public static class ConversationRules
{
    public const int MaxMembers = 256;
    public const int MaxTitleLength = 100;
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public static string Name(ConversationType t) => t.ToString().ToLowerInvariant();

    public static string Name(MemberRole r) => r.ToString().ToLowerInvariant();

    public static bool CanManage(MemberRole r) => r is MemberRole.Owner or MemberRole.Admin;

    public static Dictionary<string, string[]> Validate(CreateDirectRequest r) =>
        r.UserId is null || r.UserId == Guid.Empty ? new() { ["userId"] = ["Send the user to chat with."] } : [];

    public static Dictionary<string, string[]> Validate(CreateGroupRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        AddTitle(errors, r.Title);
        if (r.MemberIds is not { Count: > 0 } || r.MemberIds.Contains(Guid.Empty))
            errors["memberIds"] = ["Add at least one other member."];
        else if (r.MemberIds.Distinct().Count() > MaxMembers - 1)
            errors["memberIds"] = [$"A group holds at most {MaxMembers} members."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(RenameRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        AddTitle(errors, r.Title);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(AddMembersRequest r) =>
        r.UserIds is not { Count: > 0 } || r.UserIds.Contains(Guid.Empty) || r.UserIds.Distinct().Count() > MaxMembers
            ? new() { ["userIds"] = ["Send the users to add."] }
            : [];

    public static Dictionary<string, string[]> Validate(SetRoleRequest r) =>
        ParseRole(r.Role) is null ? new() { ["role"] = ["Use \"admin\" or \"member\"."] } : [];

    public static MemberRole? ParseRole(string? role) => role switch
    {
        "admin" => MemberRole.Admin,
        "member" => MemberRole.Member,
        _ => null,
    };

    public static Dictionary<string, string[]> Validate(MuteRequest r, DateTimeOffset now) =>
        r.Until is null || r.Until <= now ? new() { ["until"] = ["Send a time in the future."] } : [];

    public static Dictionary<string, string[]> ValidatePage(int? limit) =>
        limit is < 1 or > MaxLimit ? new() { ["limit"] = [$"Use 1-{MaxLimit}."] } : [];

    private static void AddTitle(Dictionary<string, string[]> errors, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
            errors["title"] = [$"Use 1-{MaxTitleLength} characters."];
    }
}
