using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

/// <summary>
/// The roster the frontend renders down the left: every player known to the
/// league. These are the real user rows, one per person created on their first
/// Discord login (plus any synthetic coaches a simulation created).
/// </summary>
public static class PlayersApi
{
    public record PlayerDto(string DiscordId, string Username, string? AvatarUrl, bool IsAdmin, string Source, bool HasTeam);

    public static void MapPlayersApi(this WebApplication app, string? corsPolicy = null)
    {
        var g = app.MapGroup("/api/players").RequireAuthorization();
        if (corsPolicy is not null) g.RequireCors(corsPolicy);

        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            // The reserved admin oversees the league without appearing as a
            // player, UNLESS they've opted in by readying up or already hold a
            // team, in which case they're a coach like anyone else and belong in
            // the roster.
            var adminPlays =
                await db.DraftParticipants.AnyAsync(p => p.DiscordId == AuthApi.AdminDiscordId, ct)
                || await db.Teams.AnyAsync(t => t.CoachId == AuthApi.AdminDiscordId, ct);

            var players = await db.Users
                .Where(u => u.DiscordId != AuthApi.AdminDiscordId || adminPlays)
                .Select(u => new PlayerDto(
                    u.DiscordId,
                    u.Username,
                    u.AvatarHash == null
                        ? null
                        : $"https://cdn.discordapp.com/avatars/{u.DiscordId}/{u.AvatarHash}.png",
                    u.IsAdmin,
                    // Inlined rather than a helper call so EF can translate it.
                    u.DiscordId.StartsWith("coach-") ? "dummy" : "discord",
                    // Whether this player coaches a team in the current season, so the
                    // Teams page can offer a tab only for players who actually have one.
                    db.Teams.Any(t => t.CoachId == u.DiscordId)))
                .ToListAsync(ct);

            return Results.Ok(players
                .OrderByDescending(p => p.Source == "discord")
                .ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase));
        });

        // Remove a player. Admin-only, this deletes a real account, same class of
        // destructive op as the admin draft controls.
        g.MapDelete("/{discordId}", async (
            string discordId, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            // Deleting the account you're signed in as would strand this session
            // on a user row that no longer exists.
            if (discordId == me.DiscordId())
                return Results.BadRequest(new { error = "You can't remove yourself." });

            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
            if (user is null) return Results.NotFound();

            // Devices, notifications and preferences key on the Discord id as a
            // plain string, not a foreign key, so deleting the User doesn't
            // cascade to them, clear them explicitly. Refresh tokens do cascade
            // (they hold the User's int id), so removing the user ends every
            // session. Teams keep their CoachId string; a reclaim re-links it.
            await db.DeviceRegistrations.Where(d => d.UserId == discordId).ExecuteDeleteAsync(ct);
            await db.NotificationPreferences.Where(n => n.UserId == discordId).ExecuteDeleteAsync(ct);
            await db.Notifications.Where(n => n.UserId == discordId).ExecuteDeleteAsync(ct);

            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            await notifier.PlayersChangedAsync(ct); // roster shrank, refresh everyone
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("admin"));

        // A player's full drafted team, with each mon's battle profile, the
        // team page opens this when you click a name in the roster. Empty until
        // that player has a team (created at Start) and has drafted.
        g.MapGet("/{discordId}/team", async (string discordId, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
            var team = await db.Teams.FirstOrDefaultAsync(t => t.CoachId == discordId, ct);

            if (team is null)
                return Results.Ok(new
                {
                    discordId,
                    username = user?.Username ?? discordId,
                    teamId = (int?)null,
                    teamName = user?.TeamName,
                    teamIcon = user?.TeamIcon,
                    showdownName = user?.ShowdownName,
                    mons = Array.Empty<object>(),
                });

            var mons = await db.Picks
                .Where(p => p.TeamId == team.Id)
                .OrderBy(p => p.Tier).ThenBy(p => p.PickNumber)
                .Select(p => new
                {
                    p.PickNumber,
                    tier = p.Tier.ToString(),
                    p.TeraType,
                    p.PokemonEntry.Name,
                    p.PokemonEntry.DexNumber,
                    p.PokemonEntry.Sprite,
                    p.PokemonEntry.Hp,
                    p.PokemonEntry.Atk,
                    p.PokemonEntry.Def,
                    p.PokemonEntry.SpAtk,
                    p.PokemonEntry.SpDef,
                    p.PokemonEntry.Speed,
                    bst = p.PokemonEntry.Hp + p.PokemonEntry.Atk + p.PokemonEntry.Def
                        + p.PokemonEntry.SpAtk + p.PokemonEntry.SpDef + p.PokemonEntry.Speed,
                    p.PokemonEntry.Type1,
                    p.PokemonEntry.Type2,
                    p.PokemonEntry.Ability1,
                    p.PokemonEntry.Ability2,
                    p.PokemonEntry.HiddenAbility,
                    // Battle stats scraped from the season's replays (null until scored).
                    stats = p.Stat == null ? null : new
                    {
                        gp = p.Stat.GamesPlayed,
                        k = p.Stat.Kills,
                        d = p.Stat.Deaths,
                        w = p.Stat.Wins,
                        l = p.Stat.Losses,
                        dealt = p.Stat.DamageDealtDirect + p.Stat.DamageDealtIndirect,
                        dealtDirect = p.Stat.DamageDealtDirect, dealtIndirect = p.Stat.DamageDealtIndirect,
                        taken = p.Stat.DamageTakenDirect + p.Stat.DamageTakenIndirect,
                        takenDirect = p.Stat.DamageTakenDirect, takenIndirect = p.Stat.DamageTakenIndirect,
                        takenSelf = p.Stat.DamageTakenSelf,
                        recovered = p.Stat.HpRecovered,
                        healed = p.Stat.HpHealed,
                        crits = p.Stat.Crits,
                        activeTurns = p.Stat.ActiveTurns, // for Presence = active / team turns
                    },
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                discordId,
                username = team.CoachName,
                teamId = (int?)team.Id,
                teamName = user?.TeamName,
                teamIcon = user?.TeamIcon,
                showdownName = user?.ShowdownName,
                teamTurns = team.BattleTurns, // Presence denominator for every mon
                mons,
            });
        });

        // The signed-in player updates their own team customisation (name, icon,
        // Showdown handle). You can only edit your own, identity is the JWT.
        g.MapPost("/me/profile", async (
            UpdateProfileRequest req, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            var discordId = me.DiscordId();
            if (discordId is null) return Results.Unauthorized();
            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
            if (user is null) return Results.NotFound();

            user.TeamName = Clean(req.TeamName, 40);
            user.ShowdownName = Clean(req.ShowdownName, 40);
            // Only accept an http(s) URL as an icon, a bare string would render
            // as a broken image and could smuggle a javascript: URL.
            var icon = Clean(req.TeamIcon, 500);
            user.TeamIcon = icon is not null && (icon.StartsWith("https://") || icon.StartsWith("http://")) ? icon : null;

            await db.SaveChangesAsync(ct);
            // Nudge open team pages elsewhere to reload.
            await notifier.PlayersChangedAsync(ct);

            return Results.Ok(new { user.TeamName, user.TeamIcon, user.ShowdownName });
        });

        // Server-to-server: the Showdown battle server's team validator calls this at
        // validation time to enforce that a coach only battles with their drafted
        // roster (see battle-server/showdown-config/custom-formats.js). Keyed by the
        // Showdown user id (lowercase-alphanumeric of the coach's ShowdownName), so no
        // JWT, it isn't the browser calling. Returns only public draft data (the same
        // picks the anonymous team page shows), so it needs no secret. `found:false`
        // (not 404) means no coach claims that Showdown name, the validator rejects.
        var roster = app.MapGet("/api/showdown/roster/{user}", async (
            string user, AppDbContext db, CancellationToken ct) =>
        {
            var wanted = ShowdownId(user);
            if (wanted.Length == 0) return Results.Ok(new { found = false });

            // Identify the coach by their Showdown name if they've set one, otherwise
            // fall back to their site (Discord) username. Both sides are normalised to
            // a Showdown id (lowercased, punctuation stripped), so a site name like
            // ".hozer" matches the Showdown login "hozer" with no explicit ShowdownName.
            // Matched in memory (the roster is tiny, and toID has no SQL translation).
            var users = await db.Users
                .Select(u => new { u.DiscordId, u.Username, u.ShowdownName })
                .ToListAsync(ct);
            var coach = users.FirstOrDefault(u => u.ShowdownName != null && ShowdownId(u.ShowdownName) == wanted)
                     ?? users.FirstOrDefault(u => ShowdownId(u.Username) == wanted);
            if (coach is null) return Results.Ok(new { found = false });

            var team = await db.Teams.FirstOrDefaultAsync(t => t.CoachId == coach.DiscordId, ct);
            if (team is null) return Results.Ok(new { found = false });

            var mons = await db.Picks
                .Where(p => p.TeamId == team.Id)
                .Select(p => new
                {
                    tier = p.Tier.ToString(),
                    tera = p.TeraType,
                    name = p.PokemonEntry.Name,
                    // The sprite slug distinguishes mega / regional formes (e.g.
                    // "charizard-megay"); the validator resolves it against the dex.
                    slug = p.PokemonEntry.Sprite,
                })
                .ToListAsync(ct);

            return Results.Ok(new { found = true, showdownName = coach.ShowdownName ?? coach.Username, mons });
        });
        if (corsPolicy is not null) roster.RequireCors(corsPolicy);

        // Every mon owned by ANY coach this season, for the Scrims teambuilder picker
        // (scrims let you bring any drafted mon, not just your own). Anonymous + CORS
        // like the per-coach roster: only public draft data, no secret. Scoped to the
        // current draft's league so a past season's pool doesn't leak in.
        var seasonRoster = app.MapGet("/api/showdown/season-roster", async (AppDbContext db, CancellationToken ct) =>
        {
            var draft = await db.Drafts.OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
            if (draft is null) return Results.Ok(new { mons = Array.Empty<object>() });
            var mons = await db.Pokemon
                .Where(p => p.LeagueId == draft.LeagueId && p.DraftedByTeamId != null)
                .Select(p => new { tier = p.Tier.ToString(), name = p.Name, slug = p.Sprite })
                .ToListAsync(ct);
            return Results.Ok(new { mons });
        });
        if (corsPolicy is not null) seasonRoster.RequireCors(corsPolicy);
    }

    /// <summary>Showdown's user/species id form: lowercase, alphanumerics only.</summary>
    private static string ShowdownId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>Trim, null out blanks, and cap length.</summary>
    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max] : s;
    }

    public record UpdateProfileRequest(string? TeamName, string? TeamIcon, string? ShowdownName);
}
