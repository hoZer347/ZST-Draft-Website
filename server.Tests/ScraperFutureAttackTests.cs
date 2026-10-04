using DraftLeague.Web.Models;
using DraftLeague.Web.Services;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Delayed attacks (Future Sight, Doom Desire) for <see cref="DraftLeague.Web.Services.ReplayStatsScraper"/>:
/// they land two turns after use as a bare -damage that names no source.
/// </summary>
public class ScraperFutureAttackTests
{
    [Fact]
    public void Future_sight_damage_is_its_users_not_the_targets_own_move()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p2|Steelix, F|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-damage|p1a: Gothorita|70/100
            |upkeep
            |turn|2
            |move|p1a: Gothorita|Protect|p1a: Gothorita
            |-singleturn|p1a: Gothorita|Protect
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-activate|p1a: Gothorita|move: Protect
            |upkeep
            |turn|3
            |move|p1a: Gothorita|Protect|p1a: Gothorita
            |-fail|p1a: Gothorita
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-damage|p1a: Gothorita|40/100
            |-end|p2a: Steelix|move: Future Sight
            |-resisted|p2a: Steelix
            |-damage|p2a: Steelix|75/100
            |upkeep
            |win|A
            """);
        Assert.Equal(25, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(25, run.Of("Steelix").TakenDirect, 2);
        Assert.Equal(0, run.Of("Steelix").TakenSelf, 2);
        Assert.Equal(60, run.Of("Steelix").DealtDirect, 2);
    }

    [Fact]
    public void Future_sight_ko_is_its_users_kill_not_an_ally_ko_for_the_last_mover()
    {
        // Doubles: the victim's partner moved last, so an untracked hit books as its
        // friendly fire and the KO as its AlliesKoed.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Vanilluxe, M|item
            |poke|p2|Steelix, F|item
            |poke|p2|Pachirisu, M|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p1b: Vanilluxe|Vanilluxe, M|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |switch|p2b: Pachirisu|Pachirisu, M|100/100
            |turn|1
            |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
            |-singleturn|p2b: Pachirisu|move: Follow Me
            |move|p1a: Gothorita|Future Sight|p2b: Pachirisu
            |-start|p1a: Gothorita|move: Future Sight
            |move|p2a: Steelix|Body Press|p1b: Vanilluxe
            |-damage|p1b: Vanilluxe|50/100
            |upkeep
            |turn|2
            |move|p2a: Steelix|Body Press|p1b: Vanilluxe
            |-damage|p1b: Vanilluxe|10/100
            |upkeep
            |turn|3
            |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
            |-singleturn|p2b: Pachirisu|move: Follow Me
            |move|p2a: Steelix|High Horsepower|p1a: Gothorita
            |-damage|p1a: Gothorita|40/100
            |-end|p2b: Pachirisu|move: Future Sight
            |-damage|p2b: Pachirisu|0 fnt
            |faint|p2b: Pachirisu
            |upkeep
            |win|A
            """);
        Assert.Equal(1, run.Of("Gothorita").Kills);
        Assert.Equal(100, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(0, run.Of("Steelix").AlliesKoed);
        Assert.Equal(0, run.Of("Steelix").DealtAllyDirect, 2);
        Assert.Equal(0, run.Of("Pachirisu").SelfKos);
    }

    [Fact]
    public void Future_sight_still_credits_its_user_after_it_has_fainted()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Vanilluxe, M|item
            |poke|p2|Steelix, F|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p2a: Steelix|Steelix, F|30/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-damage|p1a: Gothorita|0 fnt
            |faint|p1a: Gothorita
            |upkeep
            |switch|p1a: Vanilluxe|Vanilluxe, M|100/100
            |turn|2
            |move|p1a: Vanilluxe|Protect|p1a: Vanilluxe
            |-singleturn|p1a: Vanilluxe|Protect
            |move|p2a: Steelix|Body Press|p1a: Vanilluxe
            |-activate|p1a: Vanilluxe|move: Protect
            |upkeep
            |turn|3
            |move|p1a: Vanilluxe|Protect|p1a: Vanilluxe
            |-fail|p1a: Vanilluxe
            |move|p2a: Steelix|Body Press|p1a: Vanilluxe
            |-damage|p1a: Vanilluxe|20/100
            |-end|p2a: Steelix|move: Future Sight
            |-damage|p2a: Steelix|0 fnt
            |faint|p2a: Steelix
            |upkeep
            |win|A
            """);
        Assert.Equal(1, run.Of("Gothorita").Kills);
        Assert.Equal(30, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(0, run.Of("Vanilluxe").Kills);
        Assert.Equal(0, run.Of("Vanilluxe").DealtDirect, 2);
    }

    [Fact]
    public void Future_sight_hits_whoever_holds_the_slot_and_credits_its_user()
    {
        // The target switched out; its replacement takes the hit.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p2|Steelix, F|item
            |poke|p2|Excadrill, F|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-damage|p1a: Gothorita|80/100
            |upkeep
            |turn|2
            |switch|p2a: Excadrill|Excadrill, F|100/100
            |move|p1a: Gothorita|Calm Mind|p1a: Gothorita
            |-boost|p1a: Gothorita|spa|1
            |-boost|p1a: Gothorita|spd|1
            |upkeep
            |turn|3
            |move|p2a: Excadrill|Iron Head|p1a: Gothorita
            |-damage|p1a: Gothorita|20/100
            |-end|p2a: Excadrill|move: Future Sight
            |-damage|p2a: Excadrill|90/100
            |upkeep
            |win|B
            """);
        Assert.Equal(10, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(10, run.Of("Excadrill").TakenDirect, 2);
        Assert.Equal(0, run.Of("Excadrill").TakenSelf, 2);
        Assert.Equal(0, run.Of("Steelix").TakenDirect, 2);
    }

    [Fact]
    public void A_pending_future_sight_stays_with_the_slot_through_ally_switch()
    {
        // Showdown keeps the future attack as a slot condition, which Ally Switch does
        // not move: the mon swapped INTO the targeted slot takes it.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Vanilluxe, M|item
            |poke|p2|Steelix, F|item
            |poke|p2|Pachirisu, M|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p1b: Vanilluxe|Vanilluxe, M|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |switch|p2b: Pachirisu|Pachirisu, M|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |upkeep
            |turn|2
            |move|p2b: Pachirisu|Ally Switch|p2b: Pachirisu
            |swap|p2b: Pachirisu|0|[from] move: Ally Switch
            |upkeep
            |turn|3
            |move|p2b: Steelix|Iron Head|p1b: Vanilluxe
            |-damage|p1b: Vanilluxe|60/100
            |-end|p2a: Pachirisu|move: Future Sight
            |-damage|p2a: Pachirisu|55/100
            |upkeep
            |win|A
            """);
        Assert.Equal(45, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(45, run.Of("Pachirisu").TakenDirect, 2);
        Assert.Equal(0, run.Of("Steelix").TakenDirect, 2);
        Assert.Equal(40, run.Of("Steelix").DealtDirect, 2);
    }

    [Fact]
    public void Doom_desire_damage_and_ko_are_its_users()
    {
        // Doom Desire's -start is bare "Doom Desire", its -end carries "move: ".
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Jirachi|item
            |poke|p2|Gardevoir, F|item
            |start
            |switch|p1a: Jirachi|Jirachi|100/100
            |switch|p2a: Gardevoir|Gardevoir, F|100/100
            |turn|1
            |move|p1a: Jirachi|Doom Desire|p2a: Gardevoir
            |-start|p1a: Jirachi|Doom Desire
            |move|p2a: Gardevoir|Moonblast|p1a: Jirachi
            |-resisted|p1a: Jirachi
            |-damage|p1a: Jirachi|85/100
            |upkeep
            |turn|2
            |move|p1a: Jirachi|Iron Head|p2a: Gardevoir
            |-supereffective|p2a: Gardevoir
            |-damage|p2a: Gardevoir|40/100
            |move|p2a: Gardevoir|Moonblast|p1a: Jirachi
            |-resisted|p1a: Jirachi
            |-damage|p1a: Jirachi|70/100
            |upkeep
            |turn|3
            |move|p2a: Gardevoir|Mystical Fire|p1a: Jirachi
            |-supereffective|p1a: Jirachi
            |-damage|p1a: Jirachi|30/100
            |-end|p2a: Gardevoir|move: Doom Desire
            |-supereffective|p2a: Gardevoir
            |-damage|p2a: Gardevoir|0 fnt
            |faint|p2a: Gardevoir
            |upkeep
            |win|A
            """);
        Assert.Equal(1, run.Of("Jirachi").Kills);
        Assert.Equal(100, run.Of("Jirachi").DealtDirect, 2);
        Assert.Equal(0, run.Of("Gardevoir").SelfKos);
        Assert.Equal(0, run.Of("Gardevoir").TakenSelf, 2);
    }

    [Fact]
    public void Two_future_attacks_landing_together_each_credit_their_own_user()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Jirachi|item
            |poke|p2|Steelix, F|item
            |poke|p2|Pachirisu, M|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p1b: Jirachi|Jirachi|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |switch|p2b: Pachirisu|Pachirisu, M|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2b: Pachirisu
            |-start|p1a: Gothorita|move: Future Sight
            |move|p1b: Jirachi|Doom Desire|p2a: Steelix
            |-start|p1b: Jirachi|Doom Desire
            |upkeep
            |turn|2
            |upkeep
            |turn|3
            |move|p2a: Steelix|Iron Head|p1a: Gothorita
            |-damage|p1a: Gothorita|50/100
            |-end|p2b: Pachirisu|move: Future Sight
            |-crit|p2b: Pachirisu
            |-damage|p2b: Pachirisu|30/100
            |-end|p2a: Steelix|move: Doom Desire
            |-resisted|p2a: Steelix
            |-damage|p2a: Steelix|85/100
            |upkeep
            |win|A
            """);
        Assert.Equal(70, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(1, run.Of("Gothorita").Crits);
        Assert.Equal(15, run.Of("Jirachi").DealtDirect, 2);
        Assert.Equal(0, run.Of("Jirachi").Crits);
        Assert.Equal(0, run.Of("Steelix").Crits);
        Assert.Equal(0, run.Of("Steelix").TakenSelf, 2);
        Assert.Equal(0, run.Of("Steelix").DealtAllyDirect, 2);
    }

    [Fact]
    public void Future_sight_on_an_ally_is_friendly_fire_not_a_kill()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Vanilluxe, M|item
            |poke|p2|Steelix, F|item
            |poke|p2|Pachirisu, M|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p1b: Vanilluxe|Vanilluxe, M|20/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |switch|p2b: Pachirisu|Pachirisu, M|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p1b: Vanilluxe
            |-start|p1a: Gothorita|move: Future Sight
            |upkeep
            |turn|2
            |upkeep
            |turn|3
            |move|p2a: Steelix|Iron Head|p1a: Gothorita
            |-damage|p1a: Gothorita|50/100
            |-end|p1b: Vanilluxe|move: Future Sight
            |-damage|p1b: Vanilluxe|0 fnt
            |faint|p1b: Vanilluxe
            |upkeep
            |win|B
            """);
        Assert.Equal(0, run.Of("Gothorita").Kills);
        Assert.Equal(1, run.Of("Gothorita").AlliesKoed);
        Assert.Equal(20, run.Of("Gothorita").DealtAllyDirect, 2);
        Assert.Equal(0, run.Of("Gothorita").DealtDirect, 2);
        Assert.Equal(0, run.Of("Steelix").Kills);
    }

    [Fact]
    public void A_future_attack_that_does_no_damage_leaves_nothing_armed_for_the_next_hit()
    {
        // Future Sight is aimed at Steelix, lands on the Dark-type Tyranitar that replaced
        // it and does nothing; the next turn's bare hit on that slot is Vanilluxe's.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p1|Vanilluxe, M|item
            |poke|p2|Steelix, F|item
            |poke|p2|Pachirisu, M|item
            |poke|p2|Tyranitar, F|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p1b: Vanilluxe|Vanilluxe, M|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |switch|p2b: Pachirisu|Pachirisu, M|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |upkeep
            |turn|2
            |switch|p2a: Tyranitar|Tyranitar, F|100/100
            |upkeep
            |turn|3
            |-end|p2a: Tyranitar|move: Future Sight
            |-immune|p2a: Tyranitar
            |upkeep
            |turn|4
            |move|p1b: Vanilluxe|Freeze-Dry|p2a: Tyranitar
            |-damage|p2a: Tyranitar|60/100
            |upkeep
            |win|A
            """);
        Assert.Equal(40, run.Of("Vanilluxe").DealtDirect, 2);
        Assert.Equal(0, run.Of("Gothorita").DealtDirect, 2);
    }

    [Fact]
    public void An_unresolved_future_sight_user_leaves_the_hit_uncredited_not_on_the_last_mover()
    {
        // The resolver finds no Pick for Gothorita (e.g. it has since been traded away).
        var steelix = new Pick { Id = 1 };
        var result = ReplayStatsScraper.Scrape("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gothorita, F|item
            |poke|p2|Steelix, F|item
            |start
            |switch|p1a: Gothorita|Gothorita, F|100/100
            |switch|p2a: Steelix|Steelix, F|100/100
            |turn|1
            |move|p1a: Gothorita|Future Sight|p2a: Steelix
            |-start|p1a: Gothorita|move: Future Sight
            |upkeep
            |turn|2
            |upkeep
            |turn|3
            |move|p2a: Steelix|Body Press|p1a: Gothorita
            |-damage|p1a: Gothorita|40/100
            |-end|p2a: Steelix|move: Future Sight
            |-crit|p2a: Steelix
            |-damage|p2a: Steelix|75/100
            |upkeep
            |win|A
            """, (side, _) => side == "p2" ? steelix : null);
        var s = result.Stats[steelix];
        Assert.Equal(25, s.TakenDirect, 2);
        Assert.Equal(0, s.TakenSelf, 2);
        Assert.Equal(0, s.Crits);
        Assert.Equal(25, result.Uncredited["(direct)"], 2);
    }

    [Fact]
    public void Week_2_hozer_vs_sparkzzie_credits_every_future_sight_to_gothorita()
    {
        // Match 974, chat and timestamps stripped. Three Future Sights land while Steelix
        // is the last mover: one on Steelix, two on Pachirisu, the second a KO.
        var run = ReplayLogRunner.Run(Match974);
        var gothorita = run.Of("Gothorita");
        Assert.Equal(1, gothorita.Kills);
        Assert.Equal((77 - 49) / 182.0 * 100 + 106 / 167.0 * 100, gothorita.DealtDirect, 2);
        Assert.Equal(0, gothorita.DealtAllyDirect, 2);
        var steelix = run.Of("Steelix");
        Assert.Equal(0, steelix.AlliesKoed);
        Assert.Equal(0, steelix.DealtAllyDirect, 2);
        Assert.Equal(0, steelix.TakenSelf, 2);
        Assert.Equal((182 - 49) / 182.0 * 100, steelix.TakenDirect, 2);
        Assert.Equal(0, run.Of("Pachirisu").SelfKos);
        Assert.Empty(run.Result.Uncredited);
    }

    private const string Match974 = """
        |player|p1|hozer|cynthia-gen4|1045
        |player|p2|sparkzzie|102|1000
        |poke|p1|Gouging Fire, L50|
        |poke|p1|Walking Wake, L50|
        |poke|p1|Raichu, L50, F|
        |poke|p1|Gothorita, L50, F|
        |poke|p1|Vanilluxe, L50, M|
        |poke|p1|Shedinja, L50|
        |poke|p2|Tapu Lele, L50|
        |poke|p2|Steelix, L50, F|
        |poke|p2|Tyranitar, L50, F|
        |poke|p2|Drifblim, L50, M|
        |poke|p2|Veluza, L50, M|
        |poke|p2|Pachirisu, L50, M|
        |start
        |switch|p1a: Wojtek Wake|Walking Wake, L50|179/179
        |switch|p1b: Slav Squatter|Raichu, L50, F|135/135
        |switch|p2a: Veluza|Veluza, L50, M|197/197
        |switch|p2b: Drifblim|Drifblim, L50, M|227/227
        |turn|1
        |detailschange|p1b: Slav Squatter|Raichu-Mega-X, L50, F
        |-mega|p1b: Slav Squatter|Raichu|Raichunite X
        |-fieldstart|move: Electric Terrain|[from] ability: Electric Surge|[of] p1b: Slav Squatter
        |move|p2a: Veluza|Final Gambit|p1b: Slav Squatter
        |-damage|p1b: Slav Squatter|0 fnt
        |faint|p2a: Veluza
        |faint|p1b: Slav Squatter
        |move|p1a: Wojtek Wake|Draco Meteor|p2b: Drifblim
        |-damage|p2b: Drifblim|37/227
        |-unboost|p1a: Wojtek Wake|spa|2
        |move|p2b: Drifblim|Calm Mind|p2b: Drifblim
        |-boost|p2b: Drifblim|spa|1
        |-boost|p2b: Drifblim|spd|1
        |upkeep
        |switch|p1b: Bjonk|Gouging Fire, L50|180/180
        |switch|p2a: Tapu Lele|Tapu Lele, L50|177/177
        |-fieldstart|move: Psychic Terrain|[from] ability: Psychic Surge|[of] p2a: Tapu Lele
        |-enditem|p2b: Drifblim|Psychic Seed
        |-boost|p2b: Drifblim|spd|1|[from] item: Psychic Seed
        |turn|2
        |-end|p1b: Bjonk|Protosynthesis|[silent]
        |switch|p1b: Kurwaaaa|Shedinja, L50|1/1
        |move|p2b: Drifblim|Protect|p2b: Drifblim
        |-singleturn|p2b: Drifblim|Protect
        |move|p1a: Wojtek Wake|Draco Meteor|p2b: Drifblim
        |-activate|p2b: Drifblim|move: Protect
        |move|p2a: Tapu Lele|Psych Up|p2b: Drifblim
        |-copyboost|p2a: Tapu Lele|p2b: Drifblim|[from] move: Psych Up
        |upkeep
        |turn|3
        |move|p1a: Wojtek Wake|Draco Meteor|p2b: Drifblim
        |-damage|p2b: Drifblim|0 fnt
        |-unboost|p1a: Wojtek Wake|spa|2
        |faint|p2b: Drifblim
        |move|p2a: Tapu Lele|Skill Swap||[still]
        |-fail|p2a: Tapu Lele
        |move|p1b: Kurwaaaa|Toxic|p2a: Tapu Lele
        |-status|p2a: Tapu Lele|tox
        |-damage|p2a: Tapu Lele|166/177 tox|[from] psn
        |upkeep
        |switch|p2b: Tyranitar|Tyranitar, L50, F|207/207
        |-weather|Sandstorm|[from] ability: Sand Stream|[of] p2b: Tyranitar
        |turn|4
        |-end|p1a: Wojtek Wake|Protosynthesis|[silent]
        |switch|p1a: Bjonk|Gouging Fire, L50|180/180
        |move|p1b: Kurwaaaa|Protect|p1b: Kurwaaaa
        |-singleturn|p1b: Kurwaaaa|Protect
        |move|p2a: Tapu Lele|Moonblast|p1a: Bjonk
        |-damage|p1a: Bjonk|71/180
        |move|p2b: Tyranitar|Rock Slide|p1a: Bjonk|[spread] p1a
        |-activate|p1b: Kurwaaaa|move: Protect
        |-supereffective|p1a: Bjonk|1
        |-damage|p1a: Bjonk|0 fnt
        |faint|p1a: Bjonk
        |-end|p1a: Bjonk|Protosynthesis|[silent]
        |-weather|Sandstorm|[upkeep]
        |-damage|p2a: Tapu Lele|155/177 tox|[from] Sandstorm
        |-heal|p2a: Tapu Lele|166/177 tox|[from] item: Leftovers
        |-damage|p2a: Tapu Lele|144/177 tox|[from] psn
        |upkeep
        |switch|p1a: Wojtek Wake|Walking Wake, L50|179/179
        |turn|5
        |move|p1b: Kurwaaaa|Ally Switch|p1b: Kurwaaaa
        |swap|p1b: Kurwaaaa|0|[from] move: Ally Switch
        |move|p1b: Wojtek Wake|Surf|p2a: Tapu Lele|[spread] p2a,p2b
        |-immune|p1a: Kurwaaaa|[from] ability: Wonder Guard
        |-crit|p2a: Tapu Lele
        |-supereffective|p2b: Tyranitar|1
        |-crit|p2b: Tyranitar
        |-damage|p2a: Tapu Lele|21/177 tox
        |-damage|p2b: Tyranitar|15/207
        |move|p2a: Tapu Lele|Moonblast|p1a: Kurwaaaa
        |-immune|p1a: Kurwaaaa|[from] ability: Wonder Guard
        |move|p2b: Tyranitar|Rock Slide|p1b: Wojtek Wake|[spread] p1a,p1b
        |-supereffective|p1a: Kurwaaaa|1
        |-damage|p1a: Kurwaaaa|0 fnt
        |-damage|p1b: Wojtek Wake|121/179
        |faint|p1a: Kurwaaaa
        |-weather|Sandstorm|[upkeep]
        |-damage|p1b: Wojtek Wake|110/179|[from] Sandstorm
        |-damage|p2a: Tapu Lele|10/177 tox|[from] Sandstorm
        |-heal|p2a: Tapu Lele|21/177 tox|[from] item: Leftovers
        |-damage|p2a: Tapu Lele|0 fnt|[from] psn
        |faint|p2a: Tapu Lele
        |upkeep
        |switch|p2a: Steelix|Steelix, L50, F|182/182
        |switch|p1a: Fent!|Gothorita, L50, F|167/167
        |turn|6
        |detailschange|p2a: Steelix|Steelix-Mega, L50, F
        |-mega|p2a: Steelix|Steelix|Steelixite
        |move|p1a: Fent!|Helping Hand|p1b: Wojtek Wake
        |-singleturn|p1b: Wojtek Wake|Helping Hand|[of] p1a: Fent!
        |move|p2a: Steelix|Wide Guard|p2a: Steelix
        |-singleturn|p2a: Steelix|Wide Guard
        |move|p1b: Wojtek Wake|Surf|p2b: Tyranitar|[spread] p1a
        |-activate|p2a: Steelix|move: Wide Guard
        |-activate|p2b: Tyranitar|move: Wide Guard
        |-damage|p1a: Fent!|68/167
        |move|p2b: Tyranitar|Knock Off|p1b: Wojtek Wake
        |-damage|p1b: Wojtek Wake|0 fnt
        |-enditem|p1b: Wojtek Wake|Choice Specs|[from] move: Knock Off|[of] p2b: Tyranitar
        |faint|p1b: Wojtek Wake
        |-end|p1b: Wojtek Wake|Protosynthesis|[silent]
        |-weather|Sandstorm|[upkeep]
        |-damage|p1a: Fent!|58/167|[from] Sandstorm
        |-fieldend|move: Psychic Terrain
        |upkeep
        |switch|p1b: Russian Blizzard|Vanilluxe, L50, M|165/165
        |-weather|Snowscape|[from] ability: Snow Warning|[of] p1b: Russian Blizzard
        |turn|7
        |move|p2a: Steelix|Wide Guard|p2a: Steelix
        |-singleturn|p2a: Steelix|Wide Guard
        |move|p1b: Russian Blizzard|Freeze-Dry|p2b: Tyranitar
        |-damage|p2b: Tyranitar|0 fnt
        |faint|p2b: Tyranitar
        |move|p1a: Fent!|Future Sight|p2a: Steelix
        |-start|p1a: Fent!|move: Future Sight
        |-weather|Snowscape|[upkeep]
        |upkeep
        |switch|p2b: Pachirisu|Pachirisu, L50, M|167/167
        |turn|8
        |move|p1a: Fent!|Helping Hand|p1b: Russian Blizzard
        |-singleturn|p1b: Russian Blizzard|Helping Hand|[of] p1a: Fent!
        |move|p1b: Russian Blizzard|Freeze-Dry|p2a: Steelix
        |-damage|p2a: Steelix|77/182
        |move|p2b: Pachirisu|Encore|p1b: Russian Blizzard
        |-start|p1b: Russian Blizzard|Encore
        |move|p2a: Steelix|Body Press|p1b: Russian Blizzard
        |-supereffective|p1b: Russian Blizzard|1
        |-damage|p1b: Russian Blizzard|31/165
        |-weather|Snowscape|[upkeep]
        |upkeep
        |turn|9
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p1b: Russian Blizzard|Freeze-Dry|p2b: Pachirisu
        |-damage|p2b: Pachirisu|106/167
        |move|p1a: Fent!|Future Sight|p2b: Pachirisu
        |-start|p1a: Fent!|move: Future Sight
        |move|p2a: Steelix|Body Press|p1b: Russian Blizzard
        |-supereffective|p1b: Russian Blizzard|1
        |-damage|p1b: Russian Blizzard|0 fnt
        |faint|p1b: Russian Blizzard
        |-weather|Snowscape|[upkeep]
        |-end|p2a: Steelix|move: Future Sight
        |-resisted|p2a: Steelix|1
        |-damage|p2a: Steelix|49/182
        |upkeep
        |turn|10
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p1a: Fent!|Future Sight||[still]
        |-fail|p1a: Fent!
        |move|p2a: Steelix|High Horsepower|p1a: Fent!|[miss]
        |-miss|p2a: Steelix|p1a: Fent!
        |-weather|Snowscape|[upkeep]
        |upkeep
        |turn|11
        |move|p1a: Fent!|Protect|p1a: Fent!
        |-singleturn|p1a: Fent!|Protect
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p2a: Steelix|High Horsepower|p1a: Fent!
        |-activate|p1a: Fent!|move: Protect
        |-weather|none
        |-end|p2b: Pachirisu|move: Future Sight
        |-damage|p2b: Pachirisu|51/167
        |upkeep
        |turn|12
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p1a: Fent!|Future Sight|p2b: Pachirisu
        |-start|p1a: Fent!|move: Future Sight
        |move|p2a: Steelix|High Horsepower|p1a: Fent!|[miss]
        |-miss|p2a: Steelix|p1a: Fent!
        |upkeep
        |turn|13
        |move|p1a: Fent!|Protect|p1a: Fent!
        |-singleturn|p1a: Fent!|Protect
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p2a: Steelix|High Horsepower|p1a: Fent!
        |-activate|p1a: Fent!|move: Protect
        |upkeep
        |turn|14
        |move|p2b: Pachirisu|Follow Me|p2b: Pachirisu
        |-singleturn|p2b: Pachirisu|move: Follow Me
        |move|p1a: Fent!|Future Sight||[still]
        |-fail|p1a: Fent!
        |move|p2a: Steelix|High Horsepower|p1a: Fent!
        |-damage|p1a: Fent!|16/167
        |-end|p2b: Pachirisu|move: Future Sight
        |-damage|p2b: Pachirisu|0 fnt
        |faint|p2b: Pachirisu
        |upkeep
        |turn|15
        |move|p1a: Fent!|Future Sight|p2a: Steelix
        |-start|p1a: Fent!|move: Future Sight
        |move|p2a: Steelix|Body Press|p1a: Fent!
        |-resisted|p1a: Fent!|1
        |-damage|p1a: Fent!|0 fnt
        |faint|p1a: Fent!
        |win|sparkzzie
        """;
}
