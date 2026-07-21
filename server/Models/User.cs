namespace DraftLeague.Web.Models;

/// <summary>
/// A person, identified by Discord. Created on first successful login.
///
/// The Discord snowflake is the primary identity across the whole system,
/// Team.CoachId, DeviceRegistration.UserId and NotificationRecord.UserId all
/// hold this value. Discord usernames are mutable and reusable, so they are
/// cached for display only and never used to identify anyone.
/// </summary>
public class User
{
    public int Id { get; set; }

    /// <summary>Discord user snowflake. Stable and unique forever.</summary>
    public required string DiscordId { get; set; }

    /// <summary>Cached for display. Can change at any time.</summary>
    public required string Username { get; set; }

    /// <summary>Discord avatar hash; null means they use a default avatar.</summary>
    public string? AvatarHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastLoginAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Site-wide admin, can start drafts and roll picks back.</summary>
    public bool IsAdmin { get; set; }

    // ── team customisation (player-set, shown on the team page) ──────────
    /// <summary>Custom team name shown beside the coach's Discord name.</summary>
    public string? TeamName { get; set; }

    /// <summary>URL of a square team icon the player supplied.</summary>
    public string? TeamIcon { get; set; }

    /// <summary>The player's Pokémon Showdown username.</summary>
    public string? ShowdownName { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

/// <summary>
/// A long-lived token that buys new short-lived access tokens.
///
/// Access tokens are JWTs and can't be revoked once issued, so they're kept
/// short. Revocation happens here instead: delete or revoke the row and the
/// session dies at the next refresh.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the token. The raw value is only ever sent to the
    /// client, a database leak must not hand over live sessions.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Which device this session belongs to, for a sessions list.</summary>
    public string? DeviceLabel { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
