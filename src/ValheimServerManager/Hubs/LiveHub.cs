using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ValheimServerManager.Hubs;

[Authorize]
public sealed class LiveHub : Hub { }

