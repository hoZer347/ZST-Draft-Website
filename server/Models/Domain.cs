namespace DraftLeague.Web.Models;

/// <summary>Draft tiers, most valuable first. Auto-pick walks these in reverse.</summary>
public enum Tier { S, A, B, C }

public enum DraftState { NotStarted, Running, Paused, Complete }

public enum MatchResult { Pending, HomeWin, AwayWin, Draw }

/// <summary>
/// A league is one season of a draft league: its own pokemon pool,
/// roster rules, teams and schedule.
/// </summary>
public class League
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Discord user id of the league owner.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Seconds each coach gets per pick before auto-pick fires. Default
    /// 24 hours — draft leagues usually pick asynchronously over days.</summary>
    public int PickTimerSeconds { get; set; } = 86400;

    /// <summary>How many weeks the season runs — the default the round-robin
    /// schedule is generated over. Set before the draft starts.</summary>
    public int SeasonWeeks { get; set; } = 8;

    public List<Team> Teams { get; set; } = [];
    public List<PokemonEntry> Pool { get; set; } = [];
    public List<TierRule> TierRules { get; set; } = [];
    public List<Match> Matches { get; set; } = [];
    public Draft? Draft { get; set; }
}

/// <summary>
/// Per-league tier configuration. Replaces the old hardcoded
/// TIER_SLOTS_PER_TEAM / TIER_SELECTION_COUNT dictionaries so each
/// league can run its own format.
/// </summary>
public class TierRule
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    public Tier Tier { get; set; }

    /// <summary>How many of this tier each team must draft.</summary>
    public int SlotsPerTeam { get; set; }

    /// <summary>How many random options are offered when a coach picks this tier.</summary>
    public int OptionsOffered { get; set; }
}

/// <summary>A coach's team within a league.</summary>
public class Team
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Discord user id of the coach who owns this team.</summary>
    public required string CoachId { get; set; }
    public required string CoachName { get; set; }

    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }

    /// <summary>
    /// Total battle turns across every game this team has played, scraped from
    /// the replays. The denominator for each of its mons' Presence.
    /// </summary>
    public int BattleTurns { get; set; }

    /// <summary>
    /// Skip tokens left this draft. A coach may skip their turn — deferring the
    /// pick to a later cycle — this many times. The engine resets it to
    /// DraftEngine.MaxSkipsPerTeam when the draft starts (or aborts); this
    /// literal default just covers teams created outside that path.
    /// </summary>
    public int SkipsRemaining { get; set; } = 5;

    public List<Pick> Picks { get; set; } = [];
}

/// <summary>One pokemon available in a league's draft pool.</summary>
public class PokemonEntry
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    public required string Name { get; set; }
    public Tier Tier { get; set; }

    /// <summary>National dex number. A fallback sprite key when Sprite is unset.</summary>
    public int DexNumber { get; set; }

    /// <summary>
    /// Pokémon Showdown sprite slug (e.g. "charizard-megay"), from the source
    /// sheet. Distinguishes mega/regional forms that share a dex number, which a
    /// bare dex lookup renders wrong. The client builds the sprite URL from it.
    /// </summary>
    public string? Sprite { get; set; }

    // Battle profile, from the source sheet — shown on the team page. Display
    // only; the draft never reads these.
    public int Hp { get; set; }
    public int Atk { get; set; }
    public int Def { get; set; }
    public int SpAtk { get; set; }
    public int SpDef { get; set; }
    public int Speed { get; set; }
    public string? Type1 { get; set; }
    public string? Type2 { get; set; }
    public string? Ability1 { get; set; }
    public string? Ability2 { get; set; }
    public string? HiddenAbility { get; set; }

    /// <summary>Base stat total — the usual at-a-glance power number.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int BaseStatTotal => Hp + Atk + Def + SpAtk + SpDef + Speed;

    /// <summary>Set once drafted. A null value means still in the pool.</summary>
    public int? DraftedByTeamId { get; set; }
    public Team? DraftedByTeam { get; set; }
}

/// <summary>The draft for a league. One per league.</summary>
public class Draft
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    public DraftState State { get; set; } = DraftState.NotStarted;

    /// <summary>
    /// Flat list of TeamIds in pick order — snake, linear or otherwise.
    /// The engine does not compute order; it just walks this list.
    /// </summary>
    public List<DraftSlot> Order { get; set; } = [];

    /// <summary>Index into Order of the pick currently on the clock.</summary>
    public int CurrentIndex { get; set; }

    /// <summary>When the current pick's clock expires. Null when not running.</summary>
    public DateTimeOffset? PickDeadline { get; set; }

    /// <summary>
    /// Options offered for the pick currently on the clock, cached so a
    /// refresh cannot reroll them. Cleared on every advance/rollback.
    /// </summary>
    public List<OfferedOption> Offered { get; set; } = [];

    public List<Pick> Picks { get; set; } = [];
}

/// <summary>
/// A coach who has readied up for a draft before it starts. The Start roster is
/// built from these rows — not merely everyone signed in — so participation is an
/// explicit opt-in, and a coach can Leave to opt back out while NotStarted.
/// </summary>
public class DraftParticipant
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public Draft Draft { get; set; } = null!;

    /// <summary>Discord id of the coach who readied up.</summary>
    public required string DiscordId { get; set; }

    /// <summary>When they readied — also the order they join the snake in.</summary>
    public DateTimeOffset ReadyAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One position in the pick order.</summary>
public class DraftSlot
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public Draft Draft { get; set; } = null!;

    public int Position { get; set; }
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;
}

/// <summary>A pokemon currently offered to the coach on the clock.</summary>
public class OfferedOption
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public Draft Draft { get; set; } = null!;

    public int PokemonEntryId { get; set; }
    public PokemonEntry PokemonEntry { get; set; } = null!;

    public Tier Tier { get; set; }

    /// <summary>
    /// The Tera type rolled for this option — C tier only, null otherwise. It's
    /// assigned when the option is offered (so a refresh can't reroll it) and
    /// carries onto the Pick when this option is chosen.
    /// </summary>
    public string? TeraType { get; set; }
}

/// <summary>A completed pick. The authoritative draft record.</summary>
public class Pick
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public Draft Draft { get; set; } = null!;

    public int PickNumber { get; set; }

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public int PokemonEntryId { get; set; }
    public PokemonEntry PokemonEntry { get; set; } = null!;

    public Tier Tier { get; set; }

    /// <summary>True when the clock expired and the engine picked.</summary>
    public bool WasAutoPick { get; set; }

    /// <summary>The Tera type this pick was drafted with — C tier only, else null.</summary>
    public string? TeraType { get; set; }

    /// <summary>
    /// JSON snapshot of the options that were offered this turn but NOT taken —
    /// the roads not travelled, shown in the pick feed. A denormalised
    /// [{name,sprite,dexNumber,tier}] captured at pick time; null for a fresh
    /// auto-pick that never opened a tier (no options to snapshot).
    /// </summary>
    public string? OtherOptions { get; set; }

    public DateTimeOffset MadeAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Battle stats scraped from the season's replays. Null until scored.</summary>
    public PokemonStat? Stat { get; set; }
}

/// <summary>
/// Battle stats for one drafted mon (one Pick), scraped from the season's
/// replay logs and accumulated across every game it appeared in. Damage/heal
/// figures are cumulative percentages of a full HP bar.
/// </summary>
public class PokemonStat
{
    public int Id { get; set; }
    public int PickId { get; set; }
    public Pick Pick { get; set; } = null!;

    public int GamesPlayed { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    /// <summary>Turns this mon was on the field, summed across its games.</summary>
    public int ActiveTurns { get; set; }

    /// <summary>Cumulative % of a full HP bar dealt to opponents.</summary>
    public double DamageDealt { get; set; }
    /// <summary>Cumulative % of a full HP bar taken.</summary>
    public double DamageTaken { get; set; }
    /// <summary>Self-healing (Recover/Roost/Leftovers/drain…), cumulative %.</summary>
    public double HpRecovered { get; set; }
    /// <summary>Healing given to allies, cumulative %.</summary>
    public double HpHealed { get; set; }
    public int Crits { get; set; }
}

/// <summary>A scheduled head-to-head between two teams.</summary>
public class Match
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    public int Week { get; set; }

    public int HomeTeamId { get; set; }
    public Team HomeTeam { get; set; } = null!;

    public int AwayTeamId { get; set; }
    public Team AwayTeam { get; set; } = null!;

    public MatchResult Result { get; set; } = MatchResult.Pending;
    public DateTimeOffset? ScheduledFor { get; set; }
    public string? ReplayUrl { get; set; }

    /// <summary>
    /// Pokémon left standing on each side, read off the submitted replay — the
    /// usual "4-0" style score. Null until a replay has been scored.
    /// </summary>
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }

    /// <summary>Discord id of the coach who submitted the replay, and when.</summary>
    public string? ReportedByCoachId { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
}
