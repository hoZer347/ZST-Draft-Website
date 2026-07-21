using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Mega-evolution attribution: a mon drafted as its Mega form (name "M-Charizard-X",
/// sprite "charizard-megax") appears in the replay under its BASE species and, when
/// it Mega-evolves, switches species mid-battle. Every stat, before evolving, after
/// evolving, or if it never evolves or never leaves the bench, must land on the ONE
/// drafted Pick's PokemonStat row (the row the stats page reads), never split across
/// a phantom "base" and "mega" entry, and never leaking to a teammate.
///
/// This works because the recorder keys picks by BaseId(sprite) ("charizard-megax"
/// → "charizard"), so the base species in the log resolves to the Mega pick, and the
/// scraper ignores |-mega|/|detailschange| so the slot keeps its switch-in Pick.
/// </summary>
public class MegaStatsTests(DraftLeagueFactory factory) : IClassFixture<DraftLeagueFactory>
{
    private sealed record Roster(League League, Team Home, Team Away, Match Match, Dictionary<string, Pick> Picks);

    // Builds a fresh league with the given (side, name, sprite) picks and a match.
    private async Task<Roster> SeedAsync(AppDbContext db,
        (string name, string sprite)[] home, (string name, string sprite)[] away)
    {
        var league = new League { Name = "Mega Test", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();

        var homeTeam = new Team { LeagueId = league.Id, Name = "Alice", CoachName = "Alice", CoachId = "alice" };
        var awayTeam = new Team { LeagueId = league.Id, Name = "Bob", CoachName = "Bob", CoachId = "bob" };
        db.Teams.AddRange(homeTeam, awayTeam);
        await db.SaveChangesAsync();

        var draft = new Draft { LeagueId = league.Id };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync();

        var picks = new Dictionary<string, Pick>();
        var n = 0;
        void Add(Team team, (string name, string sprite)[] mons)
        {
            foreach (var (name, sprite) in mons)
            {
                var entry = new PokemonEntry { LeagueId = league.Id, Name = name, Tier = Tier.C, Sprite = sprite };
                db.Pokemon.Add(entry);
                var pick = new Pick { DraftId = draft.Id, PickNumber = ++n, TeamId = team.Id, PokemonEntry = entry, Tier = Tier.C };
                db.Picks.Add(pick);
                picks[name] = pick;
            }
        }
        Add(homeTeam, home);
        Add(awayTeam, away);
        await db.SaveChangesAsync();

        var match = new Match { LeagueId = league.Id, HomeTeamId = homeTeam.Id, AwayTeamId = awayTeam.Id, HomeTeam = homeTeam, AwayTeam = awayTeam };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        return new Roster(league, homeTeam, awayTeam, match, picks);
    }

    private static async Task<PokemonStat?> StatAsync(AppDbContext db, Pick pick) =>
        await db.PokemonStats.SingleOrDefaultAsync(s => s.PickId == pick.Id);

    // Home team is always Charizard (drafted as its Mega-X) + Pikachu as the bench mon;
    // away team is Blissey + Gengar. p1 = home.
    private static readonly (string, string)[] HomeRoster = [("M-Charizard-X", "charizard-megax"), ("Pikachu", "pikachu")];
    private static readonly (string, string)[] AwayRoster = [("Blissey", "blissey"), ("Gengar", "gengar")];

    // ── Mega evolves late ────────────────────────────────────────────────────

    // Charizard leads as its base form, fights a turn, THEN Mega-evolves and keeps
    // fighting. Damage from both before and after the evolution, plus the KO it
    // scores as a Mega, must all accrue to the single Charizard pick.
    private const string LateMegaLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Pikachu, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Charizard|Charizard, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|1
        |move|p1a: Charizard|Flamethrower|p2a: Blissey
        |-damage|p2a: Blissey|70/100
        |move|p2a: Blissey|Seismic Toss|p1a: Charizard
        |-damage|p1a: Charizard|80/100
        |turn|2
        |-mega|p1a: Charizard|Charizard-Mega-X|Charizardite X
        |detailschange|p1a: Charizard|Charizard-Mega-X, M
        |move|p1a: Charizard|Dragon Claw|p2a: Blissey
        |-damage|p2a: Blissey|0 fnt
        |faint|p2a: Blissey
        |switch|p2a: Gengar|Gengar, M|100/100
        |turn|3
        |move|p1a: Charizard|Dragon Claw|p2a: Gengar
        |-damage|p2a: Gengar|30/100
        |win|Alice
        """;

    [Fact]
    public async Task Mega_that_evolves_midgame_keeps_every_stat_on_the_one_drafted_pick()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db, HomeRoster, AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", LateMegaLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var char_ = await StatAsync(db, r.Picks["M-Charizard-X"]);
        Assert.NotNull(char_);
        Assert.Equal(1, char_!.GamesPlayed);
        // Damage: 30 (base, t1) + 70 (mega, t2) + 70 (mega, t3) = 170, all on this pick.
        Assert.Equal(170, char_.DamageDealtDirect, 2);
        Assert.Equal(1, char_.Kills);          // KO'd Blissey AS a Mega
        Assert.Equal(3, char_.ActiveTurns);    // on field all three turns, base and mega alike
        Assert.Equal(20, char_.DamageTakenDirect, 2); // Seismic Toss before evolving
        Assert.Equal(1, char_.Wins);

        // No phantom "Charizard-Mega" entry: exactly one stat row exists for the home
        // team's Charizard, and the mega's stats didn't leak to the benched Pikachu.
        var homeCharRows = await db.PokemonStats.CountAsync(s => s.PickId == r.Picks["M-Charizard-X"].Id);
        Assert.Equal(1, homeCharRows);
        var pika = await StatAsync(db, r.Picks["Pikachu"]);
        Assert.NotNull(pika);
        Assert.Equal(1, pika!.GamesPlayed);    // in team preview
        Assert.Equal(0, pika.Kills);
        Assert.Equal(0, pika.ActiveTurns);
        Assert.Equal(0, pika.DamageDealtDirect, 2);
    }

    // ── Never evolves ────────────────────────────────────────────────────────

    // A mon drafted as a Mega but who plays the whole game in base form (never spends
    // its evolution). Its stats still belong to the drafted Mega pick.
    private const string NeverMegaLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Pikachu, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Charizard|Charizard, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|1
        |move|p1a: Charizard|Flamethrower|p2a: Blissey
        |-damage|p2a: Blissey|60/100
        |turn|2
        |move|p1a: Charizard|Flamethrower|p2a: Blissey
        |-damage|p2a: Blissey|0 fnt
        |faint|p2a: Blissey
        |win|Alice
        """;

    [Fact]
    public async Task Mega_that_never_evolves_still_attributes_to_the_drafted_mega_pick()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db, HomeRoster, AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", NeverMegaLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var char_ = await StatAsync(db, r.Picks["M-Charizard-X"]);
        Assert.NotNull(char_);
        Assert.Equal(1, char_!.GamesPlayed);
        Assert.Equal(100, char_.DamageDealtDirect, 2); // 40 + 60, in base form
        Assert.Equal(1, char_.Kills);
        Assert.Equal(2, char_.ActiveTurns);
    }

    // ── Never brought in ─────────────────────────────────────────────────────

    // Charizard is on the team preview but stays on the bench the whole game; Pikachu
    // plays instead. The Mega pick is credited a game played and nothing else, while
    // Pikachu's stats are its own.
    private const string BenchedMegaLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Pikachu, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Pikachu|Pikachu, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|1
        |move|p1a: Pikachu|Thunderbolt|p2a: Blissey
        |-damage|p2a: Blissey|55/100
        |turn|2
        |move|p1a: Pikachu|Thunderbolt|p2a: Blissey
        |-damage|p2a: Blissey|0 fnt
        |faint|p2a: Blissey
        |win|Alice
        """;

    [Fact]
    public async Task A_mega_left_on_the_bench_is_counted_as_played_and_nothing_else()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db, HomeRoster, AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", BenchedMegaLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var char_ = await StatAsync(db, r.Picks["M-Charizard-X"]);
        Assert.NotNull(char_);
        Assert.Equal(1, char_!.GamesPlayed);   // brought to the match (team preview)
        Assert.Equal(0, char_.ActiveTurns);    // never on the field
        Assert.Equal(0, char_.Kills);
        Assert.Equal(0, char_.DamageDealtDirect, 2);
        Assert.Equal(0, char_.DamageTakenDirect, 2);

        var pika = await StatAsync(db, r.Picks["Pikachu"]);
        Assert.NotNull(pika);
        Assert.Equal(1, pika!.Kills);          // Pikachu actually played and KO'd Blissey
        Assert.Equal(2, pika.ActiveTurns);
        Assert.Equal(100, pika.DamageDealtDirect, 2); // 45 (100->55) + 55 (55->0)
    }

    // ── Re-entry already evolved ─────────────────────────────────────────────

    // A Mega that evolves, is switched out, and comes back in ALREADY in mega form
    // (the switch line names the mega species). Both stints resolve to the same pick.
    private const string ReentryAsMegaLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Pikachu, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Charizard|Charizard, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|1
        |-mega|p1a: Charizard|Charizard-Mega-X|Charizardite X
        |detailschange|p1a: Charizard|Charizard-Mega-X, M
        |move|p1a: Charizard|Dragon Claw|p2a: Blissey
        |-damage|p2a: Blissey|60/100
        |switch|p1a: Pikachu|Pikachu, M|100/100
        |turn|2
        |switch|p1a: Charizard|Charizard-Mega-X, M|100/100
        |move|p1a: Charizard|Dragon Claw|p2a: Blissey
        |-damage|p2a: Blissey|20/100
        |win|Alice
        """;

    [Fact]
    public async Task A_mega_that_switches_back_in_already_evolved_resolves_to_the_same_pick()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db, HomeRoster, AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", ReentryAsMegaLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var char_ = await StatAsync(db, r.Picks["M-Charizard-X"]);
        Assert.NotNull(char_);
        // 40 (first stint, as mega) + 40 (after coming back in as the mega species) = 80.
        Assert.Equal(80, char_!.DamageDealtDirect, 2);
        Assert.Equal(1, char_.GamesPlayed);
        // Exactly one row, the base and mega species names never spawn a second entry.
        Assert.Equal(1, await db.PokemonStats.CountAsync(s => s.PickId == r.Picks["M-Charizard-X"].Id));
    }

    // ── Doubles: mega evolves mid-game, then a spread move ───────────────────

    // The league format is doubles, so the real case is a Mega evolving with a
    // partner beside it and using a spread move afterward. The KO and enemy damage
    // must go to the Mega pick's Dealt, while the chip on its own ally is kept as
    // friendly fire (DealtAlly), all still on the one pick, after the evolution.
    private const string DoublesLateMegaLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Pikachu, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Charizard|Charizard, M|100/100
        |switch|p1b: Pikachu|Pikachu, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |switch|p2b: Gengar|Gengar, M|100/100
        |turn|1
        |move|p1a: Charizard|Flamethrower|p2a: Blissey
        |-damage|p2a: Blissey|60/100
        |turn|2
        |-mega|p1a: Charizard|Charizard-Mega-X|Charizardite X
        |detailschange|p1a: Charizard|Charizard-Mega-X, M
        |move|p1a: Charizard|Earthquake|p2a: Blissey|[spread] p2a,p2b,p1b
        |-damage|p2a: Blissey|0 fnt
        |-damage|p2b: Gengar|50/100
        |-damage|p1b: Pikachu|70/100
        |faint|p2a: Blissey
        |win|Alice
        """;

    [Fact]
    public async Task Doubles_mega_evolves_then_spread_move_credits_stay_on_the_pick()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db, HomeRoster, AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", DoublesLateMegaLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var char_ = await StatAsync(db, r.Picks["M-Charizard-X"]);
        Assert.NotNull(char_);
        // Enemy damage: 40 (Blissey 100->60, base t1) + 60 (Blissey 60->0, mega t2)
        // + 50 (Gengar 100->50, mega t2) = 150.
        Assert.Equal(150, char_!.DamageDealtDirect, 2);
        // Friendly fire on its own ally Pikachu is kept separate, not in Dealt.
        Assert.Equal(30, char_.DamageDealtAllyDirect, 2);
        Assert.Equal(1, char_.Kills);       // KO'd Blissey as a Mega via the spread move
        Assert.Equal(2, char_.ActiveTurns);

        // Pikachu took the friendly fire; it scores no KO from it.
        var pika = await StatAsync(db, r.Picks["Pikachu"]);
        Assert.Equal(30, pika!.DamageTakenDirect, 2);
        Assert.Equal(0, pika.Kills);
    }

    // ── Other mega suffixes (Y, and plain) resolve too ───────────────────────

    // BaseId strips -megax / -megay / -mega. Charizard-X exercised the X branch
    // above; here a Mega-Y (sprite "…-megay") and a plain Mega (sprite "…-mega")
    // must both resolve their base-species log lines to the drafted pick.
    private const string SuffixLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Charizard, M|item
        |poke|p1|Gyarados, M|item
        |poke|p2|Blissey, F|item
        |poke|p2|Gengar, M|item
        |start
        |switch|p1a: Charizard|Charizard, M|100/100
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|1
        |-mega|p1a: Charizard|Charizard-Mega-Y|Charizardite Y
        |detailschange|p1a: Charizard|Charizard-Mega-Y, M
        |move|p1a: Charizard|Air Slash|p2a: Blissey
        |-damage|p2a: Blissey|55/100
        |switch|p1a: Gyarados|Gyarados, M|100/100
        |turn|2
        |-mega|p1a: Gyarados|Gyarados-Mega|Gyaradosite
        |detailschange|p1a: Gyarados|Gyarados-Mega, M
        |move|p1a: Gyarados|Waterfall|p2a: Blissey
        |-damage|p2a: Blissey|20/100
        |win|Alice
        """;

    [Fact]
    public async Task Mega_Y_and_plain_mega_suffixes_resolve_to_their_drafted_picks()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var r = await SeedAsync(db,
            [("M-Charizard-Y", "charizard-megay"), ("M-Gyarados", "gyarados-mega")], AwayRoster);
        await recorder.ApplyAsync(r.Match, "p1", SuffixLog, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var zardY = await StatAsync(db, r.Picks["M-Charizard-Y"]);
        Assert.NotNull(zardY);
        Assert.Equal(45, zardY!.DamageDealtDirect, 2); // 100 -> 55
        Assert.Equal(1, zardY.GamesPlayed);

        var gyara = await StatAsync(db, r.Picks["M-Gyarados"]);
        Assert.NotNull(gyara);
        Assert.Equal(35, gyara!.DamageDealtDirect, 2); // 55 -> 20
        Assert.Equal(1, gyara.GamesPlayed);
    }
}
