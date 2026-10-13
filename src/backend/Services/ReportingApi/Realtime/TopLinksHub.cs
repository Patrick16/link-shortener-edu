using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ReportingApi.Realtime;

// Requires a valid token, same as every ReportingApi endpoint. Browsers can't set an Authorization
// header on a WebSocket, so the token arrives as ?access_token= - WebDefaults' JWT setup reads it
// for /hub paths only.
[Authorize]
public sealed class TopLinksHub(
    ITopLinksReader reader,
    TopLinksSubscriptions subscriptions,
    TopLinksPushScheduler scheduler) : Hub
{
    public const string TopLinksMethod = "topLinks";

    public static string GroupFor(Guid userId) => $"top-links:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId();
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId));
        subscriptions.Connected(userId);

        // Sent straight to the new connection so the dashboard isn't empty until the next click.
        var top = await reader.GetTopAsync(userId, DateTime.UtcNow, Context.ConnectionAborted);
        await Clients.Caller.SendAsync(TopLinksMethod, top);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId();
        subscriptions.Disconnected(userId);
        if (!subscriptions.IsWatched(userId))
        {
            scheduler.Forget(userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(Context.User!.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
