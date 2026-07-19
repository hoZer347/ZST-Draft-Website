using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

/// <summary>
/// A fixed debug identity either client can claim instead of signing in with
/// Discord. The set is small and shared: it's the seeded teams, so claiming a
/// slot drops you into the draft as a real, draftable coach.
/// </summary>
public record DebugSlotDef(int Index, string DiscordId, string Username, string TeamName, bool IsAdmin);

public record SlotClaim(string Client, DateTimeOffset At);

/// <summary>
/// Tracks which debug slots are currently claimed, shared across every client
/// hitting this dev server — that's what lets the web app and the phone see the
/// same slot as taken. In-memory on purpose: the state is the debug session and
/// resets on restart. Registered as a singleton in Development only.
/// </summary>
public class DebugSlots
{
    /// <summary>
    /// The one source of truth for who the debug players are. DevSeed builds the
    /// teams from this list too, so slots and teams can never drift apart.
    /// </summary>
    public static readonly IReadOnlyList<DebugSlotDef> Definitions =
    [
        // All debug coaches are plain players. Starting/aborting the draft is
        // reserved for the admin identity (see /dev/admin), so no slot is admin.
        new(1, "coach-1", "Alpha (debug)", "Team Alpha", false),
        new(2, "coach-2", "Beta (debug)",  "Team Beta",  false),
        new(3, "coach-3", "Gamma (debug)", "Team Gamma", false),
        new(4, "coach-4", "Delta (debug)", "Team Delta", false),
    ];

    private readonly object _gate = new();
    private readonly Dictionary<int, SlotClaim> _claims = new();

    public IReadOnlyDictionary<int, SlotClaim> Snapshot()
    {
        lock (_gate) return new Dictionary<int, SlotClaim>(_claims);
    }

    /// <summary>
    /// Records a new holder and returns whoever held it before (null if it was
    /// free). Last claim wins — a client that closed without releasing must
    /// never wedge a slot for the rest of the session.
    /// </summary>
    public SlotClaim? Claim(int index, string client)
    {
        lock (_gate)
        {
            _claims.TryGetValue(index, out var prev);
            _claims[index] = new SlotClaim(client, DateTimeOffset.UtcNow);
            return prev;
        }
    }

    public void Release(int index)
    {
        lock (_gate) _claims.Remove(index);
    }
}

/// <summary>
/// The claim/release surface behind the debug slots. Mapped in Program.cs behind
/// an IsDevelopment check — a deployed server never exposes it, so it can't be
/// used as an auth bypass in production.
/// </summary>
public static class DebugSlotsApi
{
    public record ClaimBody(string? Client);

    /// <summary>
    /// A reserved identity for signing in as an admin without being a coach.
    /// It's deliberately not a Discord snowflake (those are numeric), so a real
    /// account can never collide with it, and the roster filters it out — an
    /// admin signed in this way oversees the league without appearing as a
    /// player. Only mintable via the dev-only /dev/admin route below.
    /// </summary>
    public const string AdminDiscordId = "admin";

    public static void MapDebugSlots(this WebApplication app, string? corsPolicy = null)
    {
        var g = app.MapGroup("/dev/slots");
        if (corsPolicy is not null) g.RequireCors(corsPolicy);

        // Sign in as a bare admin — no slot, no team, not listed as a player.
        // Same response shape as a slot claim so the client stores it identically.
        var admin = app.MapPost("/dev/admin", async (AppDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == AdminDiscordId, ct);
            if (user is null)
            {
                user = new User { DiscordId = AdminDiscordId, Username = "Admin", IsAdmin = true };
                db.Users.Add(user);
            }
            else
            {
                user.IsAdmin = true;
            }
            await db.SaveChangesAsync(ct);

            var pair = await tokens.IssueAsync(user, "debug:admin", ct);
            return Results.Ok(new
            {
                accessToken = pair.AccessToken,
                refreshToken = pair.RefreshToken,
                accessExpiresAt = pair.AccessExpiresAt,
                user = await AuthApi.BuildMeAsync(db, user, ct),
            });
        });
        if (corsPolicy is not null) admin.RequireCors(corsPolicy);

        // The 4 slots and their live claim state, for both clients to render.
        g.MapGet("/", (DebugSlots slots) =>
        {
            var claims = slots.Snapshot();
            return Results.Ok(DebugSlots.Definitions.Select(d =>
            {
                claims.TryGetValue(d.Index, out var c);
                return new
                {
                    index = d.Index,
                    discordId = d.DiscordId,
                    username = d.Username,
                    teamName = d.TeamName,
                    isAdmin = d.IsAdmin,
                    claimedBy = c?.Client,
                    claimedAt = c?.At,
                };
            }));
        });

        // Claim a slot: ensure its user exists, mint a session, mark it taken.
        // Returns the same shape a real login does, plus who held it before.
        g.MapPost("/{index:int}/claim", async (
            int index, ClaimBody? body, DebugSlots slots, AppDbContext db, TokenService tokens,
            IDraftNotifier notifier, CancellationToken ct) =>
        {
            var def = DebugSlots.Definitions.FirstOrDefault(d => d.Index == index);
            if (def is null) return Results.NotFound();

            var client = string.IsNullOrWhiteSpace(body?.Client) ? "unknown" : body!.Client!.Trim();

            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == def.DiscordId, ct);
            var isNew = user is null;
            if (user is null)
            {
                user = new User { DiscordId = def.DiscordId, Username = def.Username, IsAdmin = def.IsAdmin };
                db.Users.Add(user);
            }
            else
            {
                user.Username = def.Username;
                user.IsAdmin = def.IsAdmin;
            }
            await db.SaveChangesAsync(ct);

            // A newly-claimed slot adds a player to the roster — refresh everyone.
            if (isNew) await notifier.PlayersChangedAsync(ct);

            var previous = slots.Claim(index, client);
            var pair = await tokens.IssueAsync(user, $"debug:{client}", ct);

            return Results.Ok(new
            {
                accessToken = pair.AccessToken,
                refreshToken = pair.RefreshToken,
                accessExpiresAt = pair.AccessExpiresAt,
                user = await AuthApi.BuildMeAsync(db, user, ct),
                previousHolder = previous?.Client,
            });
        });

        // Free a slot. Best-effort from the client on sign-out; a stale claim is
        // harmless anyway since claiming just overwrites it.
        g.MapPost("/{index:int}/release", (int index, DebugSlots slots) =>
        {
            slots.Release(index);
            return Results.Ok();
        });
    }
}
