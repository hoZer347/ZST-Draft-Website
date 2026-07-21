using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Fans every draft event out two ways: live over SignalR to browsers watching
/// the draft, and queued as a push notification for the mobile app.
///
/// Browsers get everything, because the user is already looking at it. Push is
/// rationed to things worth buzzing a pocket for, see ShouldPush.
/// </summary>
public class DraftNotifier(
    AppDbContext db,
    IHubContext<DraftHub> hub,
    INotificationQueue queue) : IDraftNotifier
{
    public async Task YourTurnAsync(int draftId, int teamId, CancellationToken ct = default)
    {
        await hub.Clients.Group(Group(draftId)).SendAsync("turnChanged", new { draftId, teamId }, ct);

        var team = await LoadTeamAsync(teamId, ct);
        if (team is null) return;

        await queue.EnqueueAsync(new NotificationRecord
        {
            UserId = team.CoachId,
            Kind = NotificationKind.YourTurn,
            Title = "You're on the clock",
            Body = $"{team.League.Name}, your pick is up.",
            DeepLink = $"draft/{draftId}",
            LeagueId = team.LeagueId,
        }, ct);
    }

    public async Task TurnWarningAsync(int draftId, int teamId, int secondsLeft, CancellationToken ct = default)
    {
        await hub.Clients.Group(Group(draftId)).SendAsync("turnWarning", new { draftId, teamId, secondsLeft }, ct);

        var team = await LoadTeamAsync(teamId, ct);
        if (team is null) return;

        var label = secondsLeft >= 60 ? $"{secondsLeft / 60} minute" : $"{secondsLeft} seconds";
        await queue.EnqueueAsync(new NotificationRecord
        {
            UserId = team.CoachId,
            Kind = NotificationKind.TurnWarning,
            Title = $"{label} left to pick",
            Body = $"{team.League.Name}, you'll be auto-picked when the clock runs out.",
            DeepLink = $"draft/{draftId}",
            LeagueId = team.LeagueId,
        }, ct);
    }

    public Task OptionsOfferedAsync(int draftId, int teamId, CancellationToken ct = default) =>
        // Only the coach on the clock cares, and they're looking at the page.
        hub.Clients.Group(Group(draftId)).SendAsync("optionsOffered", new { draftId, teamId }, ct);

    public async Task PickMadeAsync(Pick pick, CancellationToken ct = default)
    {
        var (team, mon) = await LoadPickContextAsync(pick, ct);
        if (team is null || mon is null) return;

        await hub.Clients.Group(Group(pick.DraftId)).SendAsync("pickMade", new
        {
            pick.DraftId, pick.PickNumber, pick.TeamId,
            team = team.Name, pokemon = mon.Name, tier = pick.Tier.ToString(),
            auto = pick.WasAutoPick,
        }, ct);

        // Tell the rest of the league someone took a mon off the board.
        await FanOutToLeagueAsync(team.LeagueId, exceptUserId: team.CoachId, new NotificationRecord
        {
            UserId = "",
            Kind = NotificationKind.PickMade,
            Title = $"{team.Name} drafted {mon.Name}",
            Body = $"{team.League.Name}, pick {pick.PickNumber}, {pick.Tier} tier.",
            DeepLink = $"draft/{pick.DraftId}",
            LeagueId = team.LeagueId,
        }, ct);
    }

    public async Task AutoPickedAsync(Pick pick, CancellationToken ct = default)
    {
        var (team, mon) = await LoadPickContextAsync(pick, ct);
        if (team is null || mon is null) return;

        await hub.Clients.Group(Group(pick.DraftId)).SendAsync("pickMade", new
        {
            pick.DraftId, pick.PickNumber, pick.TeamId,
            team = team.Name, pokemon = mon.Name, tier = pick.Tier.ToString(),
            auto = true,
        }, ct);

        // The coach who got auto-picked needs to know specifically.
        await queue.EnqueueAsync(new NotificationRecord
        {
            UserId = team.CoachId,
            Kind = NotificationKind.AutoPicked,
            Title = $"You were auto-picked {mon.Name}",
            Body = $"{team.League.Name}, your clock expired on pick {pick.PickNumber}.",
            DeepLink = $"draft/{pick.DraftId}",
            LeagueId = team.LeagueId,
        }, ct);

        await FanOutToLeagueAsync(team.LeagueId, exceptUserId: team.CoachId, new NotificationRecord
        {
            UserId = "",
            Kind = NotificationKind.PickMade,
            Title = $"{team.Name} auto-picked {mon.Name}",
            Body = $"{team.League.Name}, pick {pick.PickNumber}, {pick.Tier} tier.",
            DeepLink = $"draft/{pick.DraftId}",
            LeagueId = team.LeagueId,
        }, ct);
    }

    public Task PickSkippedAsync(int draftId, int teamId, CancellationToken ct = default) =>
        hub.Clients.Group(Group(draftId)).SendAsync("pickSkipped", new { draftId, teamId }, ct);

    public async Task PickRolledBackAsync(int draftId, int teamId, string pokemonName, CancellationToken ct = default)
    {
        await hub.Clients.Group(Group(draftId)).SendAsync("pickRolledBack", new { draftId, teamId, pokemonName }, ct);

        var team = await LoadTeamAsync(teamId, ct);
        if (team is null) return;

        // A rollback rewrites history under everyone's feet, tell the league.
        await FanOutToLeagueAsync(team.LeagueId, exceptUserId: null, new NotificationRecord
        {
            UserId = "",
            Kind = NotificationKind.PickRolledBack,
            Title = "A pick was rolled back",
            Body = $"{team.Name}'s {pokemonName} is back in the pool.",
            DeepLink = $"draft/{draftId}",
            LeagueId = team.LeagueId,
        }, ct);
    }

    public async Task DraftStateChangedAsync(int draftId, DraftState state, CancellationToken ct = default)
    {
        await hub.Clients.Group(Group(draftId)).SendAsync("draftStateChanged", new { draftId, state = state.ToString() }, ct);

        var leagueId = await db.Drafts.Where(d => d.Id == draftId).Select(d => d.LeagueId).FirstOrDefaultAsync(ct);
        if (leagueId == 0) return;

        var leagueName = await db.Leagues.Where(l => l.Id == leagueId).Select(l => l.Name).FirstAsync(ct);

        var (title, body) = state switch
        {
            DraftState.Running => ("Draft is live", $"{leagueName}, the draft has started."),
            DraftState.Paused => ("Draft paused", $"{leagueName}, the draft was paused."),
            DraftState.Complete => ("Draft complete", $"{leagueName}, every pick is in."),
            _ => (null, null),
        };
        if (title is null) return;

        await FanOutToLeagueAsync(leagueId, exceptUserId: null, new NotificationRecord
        {
            UserId = "",
            Kind = NotificationKind.DraftStateChanged,
            Title = title,
            Body = body!,
            DeepLink = $"draft/{draftId}",
            LeagueId = leagueId,
        }, ct);
    }

    public Task PlayersChangedAsync(CancellationToken ct = default) =>
        // Roster is league-wide, not draft-scoped, so every connected client
        // hears it and reloads the players list.
        hub.Clients.All.SendAsync("playersChanged", new { }, ct);

    public Task ReadyChangedAsync(int draftId, CancellationToken ct = default) =>
        // Someone readied up or left before the draft, refresh the ready button
        // and the roster's ready markers for everyone watching this draft.
        hub.Clients.Group(Group(draftId)).SendAsync("readyChanged", new { draftId }, ct);

    public Task ScheduleChangedAsync(int leagueId, CancellationToken ct = default) =>
        // The schedule/standings changed (generated, scored, or cleared on abort).
        hub.Clients.All.SendAsync("scheduleChanged", new { leagueId }, ct);

    // ── helpers ────────────────────────────────────────────────────────

    private static string Group(int draftId) => $"draft-{draftId}";

    private Task<Team?> LoadTeamAsync(int teamId, CancellationToken ct) =>
        db.Teams.Include(t => t.League).FirstOrDefaultAsync(t => t.Id == teamId, ct);

    private async Task<(Team?, PokemonEntry?)> LoadPickContextAsync(Pick pick, CancellationToken ct)
    {
        var team = await LoadTeamAsync(pick.TeamId, ct);
        var mon = await db.Pokemon.FirstOrDefaultAsync(p => p.Id == pick.PokemonEntryId, ct);
        return (team, mon);
    }

    /// <summary>Queues one notification per coach in a league, optionally skipping the actor.</summary>
    private async Task FanOutToLeagueAsync(int leagueId, string? exceptUserId, NotificationRecord template, CancellationToken ct)
    {
        var coachIds = await db.Teams
            .Where(t => t.LeagueId == leagueId && (exceptUserId == null || t.CoachId != exceptUserId))
            .Select(t => t.CoachId)
            .Distinct()
            .ToListAsync(ct);
        if (coachIds.Count == 0) return;

        // One batched insert for the whole league, not a query+insert+commit per
        // coach, that per-coach loop was the bulk of a pick's server time.
        var records = coachIds.Select(coachId => new NotificationRecord
        {
            UserId = coachId,
            Kind = template.Kind,
            Title = template.Title,
            Body = template.Body,
            DeepLink = template.DeepLink,
            LeagueId = template.LeagueId,
        }).ToList();
        await queue.EnqueueManyAsync(records, ct);
    }
}
