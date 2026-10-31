using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Messaging;

/// <summary>Process 4.0 over WebSocket: the entry point for message commands. Failures are HubExceptions carrying the error code.</summary>
[Authorize]
public class ChatHub : Hub;
