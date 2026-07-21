using DraftLeague.Web.Models;

namespace DraftLeague.Web.Services;

/// <summary>
/// Everything the draft wants to tell the outside world. The engine depends
/// on this rather than on SignalR or a push provider directly, so the same
/// events can fan out to the web UI and the Flutter app independently.
/// </summary>
public interface IDraftNotifier
{
    Task YourTurnAsync(int draftId, int teamId, CancellationToken ct = default);
    Task TurnWarningAsync(int draftId, int teamId, int secondsLeft, CancellationToken ct = default);
    Task OptionsOfferedAsync(int draftId, int teamId, CancellationToken ct = default);
    Task PickMadeAsync(Pick pick, CancellationToken ct = default);
    Task AutoPickedAsync(Pick pick, CancellationToken ct = default);
    Task PickSkippedAsync(int draftId, int teamId, CancellationToken ct = default);
    Task PickRolledBackAsync(int draftId, int teamId, string pokemonName, CancellationToken ct = default);
    Task DraftStateChangedAsync(int draftId, DraftState state, CancellationToken ct = default);

    /// <summary>
    /// The league roster changed, someone signed in (claimed a slot / logged in
    /// with Discord) or was removed. Tells every connected client to reload the
    /// players list. Not tied to a draft, so it fans out to all clients.
    /// </summary>
    Task PlayersChangedAsync(CancellationToken ct = default);
    Task ReadyChangedAsync(int draftId, CancellationToken ct = default);
    Task ScheduleChangedAsync(int leagueId, CancellationToken ct = default);
}
