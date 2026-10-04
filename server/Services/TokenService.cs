using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DraftLeague.Web.Services;

public class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>
    /// Signing key. Must be at least 32 bytes for HS256. Treat exactly like a
    /// password: never commit it. Anyone holding it can mint a token for any
    /// user, including an admin.
    /// </summary>
    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "draft-league";
    public string Audience { get; set; } = "draft-league";

    /// <summary>Short by design, a JWT can't be revoked once issued.</summary>
    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// How long a visitor (signed-out browsing) token lasts. Visitors have no
    /// refresh token, the page just mints a fresh one, so this is longer than an
    /// access token: it is only a read-only pass and carries no identity.
    /// </summary>
    public int VisitorTokenHours { get; set; } = 12;

    /// <summary>
    /// How long a just-rotated refresh token keeps working. Two browser tabs (or
    /// a tab and a phone) hold the same stored token and can refresh within
    /// milliseconds of each other; failing the loser logged the user out and sent
    /// them back through Discord. Short enough that a stolen token is still near
    /// worthless. 0 disables the grace, strict single use.
    /// </summary>
    public int RefreshGraceSeconds { get; set; } = 60;
}

public record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

/// <summary>A rotated pair plus the user it belongs to.</summary>
public record RefreshResult(TokenPair Tokens, User User);

public class TokenService(AppDbContext db, IConfiguration config)
{
    public const string DiscordIdClaim = "discord_id";

    /// <summary>Marks what a token is for, so an invite cannot be spent as an
    /// access token (or the reverse): both are signed with the same key.</summary>
    public const string PurposeClaim = "purpose";

    public const string InvitePurpose = "invite";

    /// <summary>Role carried by a visitor pass. Nothing authorises on it; it is
    /// there so a token can be recognised as anonymous at a glance.</summary>
    public const string VisitorRole = "visitor";

    private const string VisitorSubject = "visitor";
    private const string InviteSubject = "invite";

    private JwtOptions Options =>
        config.GetSection(JwtOptions.Section).Get<JwtOptions>()
        ?? throw new InvalidOperationException("Jwt options are not configured");

    public async Task<TokenPair> IssueAsync(User user, string? deviceLabel, CancellationToken ct = default)
    {
        var o = Options;
        var expires = DateTimeOffset.UtcNow.AddMinutes(o.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(DiscordIdClaim, user.DiscordId),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            // Lets a specific token be identified if one ever needs tracing.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "admin"));

        var access = Write(claims, expires);

        // 256 bits from a CSPRNG, this value is a bearer credential.
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(o.RefreshTokenDays),
            DeviceLabel = deviceLabel,
        });
        await db.SaveChangesAsync(ct);

        return new TokenPair(access, raw, expires);
    }

    /// <summary>
    /// A read-only pass for someone browsing without signing in. It carries no
    /// discord_id claim, which is exactly what makes it safe: every write path
    /// resolves the caller through <see cref="PrincipalExtensions.DiscordId"/>
    /// and refuses when it is null, and IsAdminAsync/OwnsTeamAsync are false for
    /// the same reason. It is authenticated only so the read endpoints (all of
    /// which sit behind RequireAuthorization) will answer.
    ///
    /// No refresh token: there is nothing to keep alive, the client just asks for
    /// another one when this expires.
    /// </summary>
    public TokenPair IssueVisitor()
    {
        var o = Options;
        var expires = DateTimeOffset.UtcNow.AddHours(Math.Max(1, o.VisitorTokenHours));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, VisitorSubject),
            new(JwtRegisteredClaimNames.UniqueName, "Visitor"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, VisitorRole),
        };
        return new TokenPair(Write(claims, expires), "", expires);
    }

    /// <summary>
    /// A player invite: the link an admin hands out so someone can sign in with
    /// Discord and join the league as a coach. Signed with the same key as the
    /// access tokens and carrying nothing but its purpose and expiry, so it needs
    /// no table of its own. Anyone holding one can create an account, that is the
    /// point, so treat a leaked link as a leaked invite and let it lapse.
    /// </summary>
    public string IssueInvite(int validDays)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, InviteSubject),
            new(PurposeClaim, InvitePurpose),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        return Write(claims, DateTimeOffset.UtcNow.AddDays(Math.Max(1, validDays)));
    }

    /// <summary>True when <paramref name="token"/> is a live, unexpired invite.</summary>
    public bool IsValidInvite(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var o = Options;
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = o.Issuer,
                ValidAudience = o.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)),
                ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);
            // An access token must never pass as an invite: the purpose claim is
            // what separates them, since both are signed with the same key.
            return principal.FindFirstValue(PurposeClaim) == InvitePurpose;
        }
        catch
        {
            return false;
        }
    }

    private string Write(IEnumerable<Claim> claims, DateTimeOffset expires)
    {
        var o = Options;
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)),
            SecurityAlgorithms.HmacSha256);

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: o.Issuer,
            audience: o.Audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds));
    }

    /// <summary>
    /// Swaps a refresh token for a new pair, rotating it. Returns null if the
    /// token is unknown, expired, or was revoked longer ago than the grace window.
    /// </summary>
    public async Task<RefreshResult?> RefreshAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var hash = Hash(rawRefreshToken);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || existing.ExpiresAt <= now) return null;
        // Revoked, but only just: treat it as the same client racing itself and
        // hand out another pair. Anything older is a dead token.
        var grace = TimeSpan.FromSeconds(Math.Max(0, Options.RefreshGraceSeconds));
        if (existing.RevokedAt is { } revoked && now - revoked > grace) return null;

        // Rotate: a refresh token is single-use, so a stolen one is only good
        // until the real client next refreshes.
        if (existing.RevokedAt is null)
        {
            existing.RevokedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var pair = await IssueAsync(existing.User, existing.DeviceLabel, ct);
        return new RefreshResult(pair, existing.User);
    }

    public async Task<bool> RevokeAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var hash = Hash(rawRefreshToken);
        var t = await db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (t is null || t.RevokedAt is not null) return false;
        t.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
