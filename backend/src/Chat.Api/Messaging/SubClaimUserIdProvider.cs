using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Messaging;

/// <summary>JWT claims are not remapped (MapInboundClaims is off), so the default provider would find no user id.</summary>
public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst("sub")?.Value;
}
