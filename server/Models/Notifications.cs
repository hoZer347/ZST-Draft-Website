namespace DraftLeague.Web.Models;

public enum DevicePlatform { Android, IOS, Desktop }

/// <summary>
/// What happened. The mobile app switches on this to pick an icon,
/// sound and deep link, so values are part of the app's contract,
/// add new ones, don't renumber or repurpose existing ones.
/// </summary>
public enum NotificationKind
{
    /// <summary>You are now on the clock.</summary>
    YourTurn = 0,

    /// <summary>Your pick clock is running low.</summary>
    TurnWarning = 1,

    /// <summary>Someone drafted a pokemon.</summary>
    PickMade = 2,

    /// <summary>The clock expired and the engine picked for you.</summary>
    AutoPicked = 3,

    /// <summary>A pick was rolled back by an admin.</summary>
    PickRolledBack = 4,

    /// <summary>The draft started, paused, resumed or completed.</summary>
    DraftStateChanged = 5,

    /// <summary>A new matchup was posted for you.</summary>
    MatchupPosted = 6,

    /// <summary>A match result was recorded.</summary>
    MatchResultPosted = 7,
}

/// <summary>
/// A push target registered by the Flutter app. One row per device per user,
/// a coach with a phone and a desktop gets two.
/// </summary>
public class DeviceRegistration
{
    public int Id { get; set; }

    /// <summary>Discord user id this device belongs to.</summary>
    public required string UserId { get; set; }

    /// <summary>FCM/APNs registration token. Rotates; upsert on UserId+Token.</summary>
    public required string PushToken { get; set; }

    public DevicePlatform Platform { get; set; }

    /// <summary>Human label for the settings screen, e.g. "Pixel 8".</summary>
    public string? DeviceName { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Cleared when the push provider reports the token is dead, so we stop
    /// spending sends on it without losing the row's history.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>Per-user opt-outs. Absent row means every kind is enabled.</summary>
public class NotificationPreference
{
    public int Id { get; set; }
    public required string UserId { get; set; }

    public NotificationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A notification queued for delivery. Rows are kept after sending so the
/// app can show history and so a failed send can be retried or diagnosed.
/// </summary>
public class NotificationRecord
{
    public int Id { get; set; }

    public required string UserId { get; set; }
    public NotificationKind Kind { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>
    /// Where tapping should land, e.g. "draft/12" or "match/48".
    /// Kept provider-agnostic; the app resolves it to a route.
    /// </summary>
    public string? DeepLink { get; set; }

    public int? LeagueId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Set when the provider rejected the send.</summary>
    public string? FailureReason { get; set; }
}
