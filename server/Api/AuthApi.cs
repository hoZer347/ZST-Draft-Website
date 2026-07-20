using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

public record ExchangeRequest(string Code, string CodeVerifier, string RedirectUri, string? DeviceLabel);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, MeResponse User);
public record MeResponse(string DiscordId, string Username, string? AvatarUrl, bool IsAdmin, IEnumerable<TeamSummary> Teams);
public record TeamSummary(int TeamId, string TeamName, int LeagueId, string LeagueName);

/// <summary>
/// Discord login for both the web frontend and the Flutter app.
///
/// Both are public clients and cannot hold the Discord client secret, so
/// neither talks to Discord's token endpoint. They obtain an authorization
/// code with PKCE and post it here; this server does the secret-bearing
/// exchange and issues its own tokens.
/// </summary>
public static class AuthApi
{
    /// <summary>
    /// A reserved identity for a dev/admin token that oversees the league without
    /// being a coach. Deliberately not a Discord snowflake (those are numeric), so a
    /// real account can never collide with it, and the roster filters it out. Only
    /// minted via the dev-only /dev/token route.
    /// </summary>
    public const string AdminDiscordId = "admin";

    public static void MapAuthApi(this WebApplication app, string? corsPolicy = null)
    {
        var auth = app.MapGroup("/api/auth");
        if (corsPolicy is not null) auth.RequireCors(corsPolicy);

        // Lets clients discover the client id and scopes instead of hardcoding
        // them in three places. Contains nothing secret.
        auth.MapGet("/config", (IConfiguration cfg) =>
        {
            var o = cfg.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new();
            return Results.Ok(new
            {
                clientId = o.ClientId,
                scopes = "identify",
                authorizeUrl = "https://discord.com/oauth2/authorize",
                configured = !string.IsNullOrWhiteSpace(o.ClientId)
                             && !string.IsNullOrWhiteSpace(o.ClientSecret),
            });
        });

        auth.MapPost("/discord", async (
            ExchangeRequest req, IDiscordAuth discord, TokenService tokens,
            AppDbContext db, IDraftNotifier notifier, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.CodeVerifier))
                return Results.BadRequest(new { error = "code and codeVerifier are required" });

            var identity = await discord.ExchangeCodeAsync(req.Code, req.CodeVerifier, req.RedirectUri, ct);
            if (identity is null)
                return Results.Unauthorized();

            // Configured Discord ids are admins. Applied on every sign-in so a
            // promotion takes effect on the next login without a DB edit; only
            // promotes — it never demotes anyone set admin by other means.
            var adminIds = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? [];
            var shouldBeAdmin = adminIds.Contains(identity.Id);

            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == identity.Id, ct);
            var isNew = user is null;
            if (user is null)
            {
                user = new User
                {
                    DiscordId = identity.Id,
                    Username = identity.Username,
                    AvatarHash = identity.Avatar,
                    IsAdmin = shouldBeAdmin,
                };
                db.Users.Add(user);
            }
            else
            {
                // Refresh the cached display fields — usernames change.
                user.Username = identity.Username;
                user.AvatarHash = identity.Avatar;
                user.LastLoginAt = DateTimeOffset.UtcNow;
                if (shouldBeAdmin) user.IsAdmin = true;
            }
            await db.SaveChangesAsync(ct);

            // A new player just joined the roster — refresh it for everyone.
            if (isNew) await notifier.PlayersChangedAsync(ct);

            var pair = await tokens.IssueAsync(user, req.DeviceLabel, ct);
            return Results.Ok(new AuthResponse(
                pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAt,
                await BuildMeAsync(db, user, ct)));
        });

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, AppDbContext db, CancellationToken ct) =>
        {
            var result = await tokens.RefreshAsync(req.RefreshToken, ct);
            if (result is null) return Results.Unauthorized();

            return Results.Ok(new AuthResponse(
                result.Tokens.AccessToken, result.Tokens.RefreshToken, result.Tokens.AccessExpiresAt,
                await BuildMeAsync(db, result.User, ct)));
        });

        auth.MapPost("/logout", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken, ct);
            // Always 200: revealing whether a token existed helps nobody but
            // someone probing for valid tokens.
            return Results.Ok();
        });

        auth.MapGet("/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var discordId = principal.FindFirstValue(TokenService.DiscordIdClaim);
            if (discordId is null) return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
            if (user is null) return Results.Unauthorized();

            return Results.Ok(await BuildMeAsync(db, user, ct));
        }).RequireAuthorization();
    }

    internal static async Task<MeResponse> BuildMeAsync(AppDbContext db, User user, CancellationToken ct)
    {
        var teams = await db.Teams
            .Include(t => t.League)
            .Where(t => t.CoachId == user.DiscordId)
            .Select(t => new TeamSummary(t.Id, t.Name, t.LeagueId, t.League.Name))
            .ToListAsync(ct);

        var avatarUrl = user.AvatarHash is null
            ? null
            : $"https://cdn.discordapp.com/avatars/{user.DiscordId}/{user.AvatarHash}.png";

        return new MeResponse(user.DiscordId, user.Username, avatarUrl, user.IsAdmin, teams);
    }
}
