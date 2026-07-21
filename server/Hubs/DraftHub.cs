using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DraftLeague.Web.Hubs;

/// <summary>
/// Live draft feed for browsers. Clients join a per-draft group and receive
/// turnChanged / pickMade / draftStateChanged and friends.
///
/// This is broadcast-only: nothing here mutates a draft. Picks go through the
/// HTTP endpoints so there is one authorised path into the engine.
///
/// Authenticated because the feed narrates a league's draft as it happens.
/// The token arrives as an access_token query param, see the JwtBearer
/// OnMessageReceived hook in Program.cs.
/// </summary>
[Authorize]
public class DraftHub : Hub
{
    public Task JoinDraft(int draftId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"draft-{draftId}");

    public Task LeaveDraft(int draftId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"draft-{draftId}");
}
