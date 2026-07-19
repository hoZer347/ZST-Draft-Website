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

    /// <summary>Short by design — a JWT can't be revoked once issued.</summary>
    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 30;
}

public record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

/// <summary>A rotated pair plus the user it belongs to.</summary>
public record RefreshResult(TokenPair Tokens, User User);

public class TokenService(AppDbContext db, IConfiguration config)
{
    public const string DiscordIdClaim = "discord_id";

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

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)),
            SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: o.Issuer,
            audience: o.Audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        var access = new JwtSecurityTokenHandler().WriteToken(jwt);

        // 256 bits from a CSPRNG — this value is a bearer credential.
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
    /// Swaps a refresh token for a new pair, rotating it. Returns null if the
    /// token is unknown, expired or already revoked.
    /// </summary>
    public async Task<RefreshResult?> RefreshAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var hash = Hash(rawRefreshToken);

        var existing = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || !existing.IsActive) return null;

        // Rotate: a refresh token is single-use, so a stolen one is only good
        // until the real client next refreshes.
        existing.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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
