using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

/// <summary>
/// The roster the frontend renders down the left: every player known to the
/// league. These are the real user rows — a person created on their first
/// Discord login, or a debug coach once its slot has been claimed. Unclaimed
/// dummy slots are not listed here; they live in the debug login panel until
/// someone claims one.
/// </summary>
public static class PlayersApi
{
    public record PlayerDto(string DiscordId, string Username, string? AvatarUrl, bool IsAdmin, string Source);

    public static void MapPlayersApi(this WebApplication app, string? corsPolicy = null)
    {
        var g = app.MapGroup("/api/players").RequireAuthorization();
        if (corsPolicy is not null) g.RequireCors(corsPolicy);

        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var players = await db.Users
                // The reserved admin oversees the league but isn't a player.
                .Where(u => u.DiscordId != DebugSlotsApi.AdminDiscordId)
                .Select(u => new PlayerDto(
                    u.DiscordId,
                    u.Username,
                    u.AvatarHash == null
                        ? null
                        : $"https://cdn.discordapp.com/avatars/{u.DiscordId}/{u.AvatarHash}.png",
                    u.IsAdmin,
                    // Inlined rather than a helper call so EF can translate it.
                    u.DiscordId.StartsWith("coach-") ? "dummy" : "discord"))
                .ToListAsync(ct);

            return Results.Ok(players
                .OrderByDescending(p => p.Source == "discord")
                .ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase));
        });

        // Remove a player. Admin-only — this deletes a real account, same class
        // of destructive op as the admin draft controls. A removed dummy coach
        // simply reappears (unclaimed) from the seed list, and is recreated in
        // full the next time that slot is claimed.
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
            // cascade to them — clear them explicitly. Refresh tokens do cascade
            // (they hold the User's int id), so removing the user ends every
            // session. Teams keep their CoachId string; a reclaim re-links it.
            await db.DeviceRegistrations.Where(d => d.UserId == discordId).ExecuteDeleteAsync(ct);
            await db.NotificationPreferences.Where(n => n.UserId == discordId).ExecuteDeleteAsync(ct);
            await db.Notifications.Where(n => n.UserId == discordId).ExecuteDeleteAsync(ct);

            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            await notifier.PlayersChangedAsync(ct); // roster shrank — refresh everyone
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole("admin"));

        // A player's full drafted team, with each mon's battle profile — the
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
                        dealt = p.Stat.DamageDealt,
                        taken = p.Stat.DamageTaken,
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
        // Showdown handle). You can only edit your own — identity is the JWT.
        g.MapPost("/me/profile", async (
            UpdateProfileRequest req, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            var discordId = me.DiscordId();
            if (discordId is null) return Results.Unauthorized();
            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
            if (user is null) return Results.NotFound();

            user.TeamName = Clean(req.TeamName, 40);
            user.ShowdownName = Clean(req.ShowdownName, 40);
            // Only accept an http(s) URL as an icon — a bare string would render
            // as a broken image and could smuggle a javascript: URL.
            var icon = Clean(req.TeamIcon, 500);
            user.TeamIcon = icon is not null && (icon.StartsWith("https://") || icon.StartsWith("http://")) ? icon : null;

            await db.SaveChangesAsync(ct);
            // Nudge open team pages elsewhere to reload.
            await notifier.PlayersChangedAsync(ct);

            return Results.Ok(new { user.TeamName, user.TeamIcon, user.ShowdownName });
        });
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
