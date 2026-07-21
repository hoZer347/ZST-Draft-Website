using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DraftLeague.Web.Data;

/// <summary>
/// SQLite has no native date type and refuses to ORDER BY a DateTimeOffset,
/// which breaks the push queue drain and the notification list. Storing them
/// as UTC ticks keeps ordering and range filters running in SQL instead of
/// forcing every such query to buffer client-side.
///
/// Applied by convention to every DateTimeOffset property, nullable ones
/// included, which EF handles automatically.
/// </summary>
public class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<PokemonEntry> Pokemon => Set<PokemonEntry>();
    public DbSet<TierRule> TierRules => Set<TierRule>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<DraftSlot> DraftSlots => Set<DraftSlot>();
    public DbSet<DraftParticipant> DraftParticipants => Set<DraftParticipant>();
    public DbSet<OfferedOption> OfferedOptions => Set<OfferedOption>();
    public DbSet<Pick> Picks => Set<Pick>();
    public DbSet<DraftSkip> DraftSkips => Set<DraftSkip>();
    public DbSet<PokemonStat> PokemonStats => Set<PokemonStat>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        cfg.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Discord's snowflake is the identity for the whole system, one row
        // per Discord account, no duplicates.
        b.Entity<User>()
            .HasIndex(u => u.DiscordId)
            .IsUnique();

        // Refresh tokens are looked up by hash on every refresh.
        b.Entity<RefreshToken>()
            .HasIndex(t => t.TokenHash)
            .IsUnique();

        b.Entity<RefreshToken>()
            .HasOne(t => t.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // IsActive is computed from other columns; it isn't storage.
        b.Entity<RefreshToken>().Ignore(t => t.IsActive);

        // One draft per league.
        b.Entity<League>()
            .HasOne(l => l.Draft)
            .WithOne(d => d.League)
            .HasForeignKey<Draft>(d => d.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // A league can only configure each tier once.
        b.Entity<TierRule>()
            .HasIndex(t => new { t.LeagueId, t.Tier })
            .IsUnique();

        // A pokemon name appears at most once per league pool.
        b.Entity<PokemonEntry>()
            .HasIndex(p => new { p.LeagueId, p.Name })
            .IsUnique();

        // The core draft invariant: a pokemon cannot be drafted twice.
        // Filtered so the many undrafted rows (NULL) don't collide.
        b.Entity<Pick>()
            .HasIndex(p => p.PokemonEntryId)
            .IsUnique();

        b.Entity<Pick>()
            .HasIndex(p => new { p.DraftId, p.PickNumber })
            .IsUnique();

        // One stat row per pick; deleting the pick removes it (DB-level cascade,
        // so the sim's bulk pick delete clears stats too).
        b.Entity<PokemonStat>()
            .HasOne(s => s.Pick)
            .WithOne(p => p.Stat)
            .HasForeignKey<PokemonStat>(s => s.PickId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<PokemonEntry>()
            .HasOne(p => p.DraftedByTeam)
            .WithMany()
            .HasForeignKey(p => p.DraftedByTeamId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<DraftSlot>()
            .HasIndex(s => new { s.DraftId, s.Position })
            .IsUnique();

        // A coach readies up for a draft at most once.
        b.Entity<DraftParticipant>()
            .HasIndex(p => new { p.DraftId, p.DiscordId })
            .IsUnique();

        // Match has two FKs to Team; cascade would create multiple paths,
        // so both are restricted and teams must be detached before delete.
        b.Entity<Match>()
            .HasOne(m => m.HomeTeam)
            .WithMany()
            .HasForeignKey(m => m.HomeTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Match>()
            .HasOne(m => m.AwayTeam)
            .WithMany()
            .HasForeignKey(m => m.AwayTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        // Push tokens are unique per device; re-registering upserts.
        b.Entity<DeviceRegistration>()
            .HasIndex(d => d.PushToken)
            .IsUnique();

        b.Entity<DeviceRegistration>()
            .HasIndex(d => d.UserId);

        b.Entity<NotificationPreference>()
            .HasIndex(n => new { n.UserId, n.Kind })
            .IsUnique();

        // The app's main query: my unread notifications, newest first.
        b.Entity<NotificationRecord>()
            .HasIndex(n => new { n.UserId, n.CreatedAt });
    }
}
