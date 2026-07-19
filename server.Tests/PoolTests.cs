using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The browse pool endpoint the tier list reads, including the owner fields that
/// show who drafted each mon.
/// </summary>
public class PoolTests : DraftScenarioBase
{
    [Fact]
    public async Task Pool_requires_authentication()
    {
        var res = await Factory.CreateClient().GetAsync("/api/pool");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Every_mon_is_unowned_before_the_draft()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var pool = await client.GetFromJsonAsync<JsonElement>("/api/pool");

        Assert.NotEmpty(pool.EnumerateArray());
        foreach (var m in pool.EnumerateArray())
        {
            Assert.False(m.GetProperty("drafted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, m.GetProperty("owner").ValueKind);
            Assert.Equal(JsonValueKind.Null, m.GetProperty("ownerTeamId").ValueKind);
        }
    }

    [Fact]
    public async Task A_drafted_mon_reports_its_owning_coach()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");

        // Who's on the clock, and their coach name, then make one pick.
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var coachName = s.GetProperty("teams").EnumerateArray()
            .First(t => Int(t, "id") == onClock).GetProperty("coachName").GetString();
        var pickedId = await PickOnceAsync(admin, draftId, byTeam);

        var pool = await admin.GetFromJsonAsync<JsonElement>("/api/pool");
        var mon = pool.EnumerateArray().First(m => Int(m, "id") == pickedId);

        Assert.True(mon.GetProperty("drafted").GetBoolean());
        Assert.Equal(onClock, mon.GetProperty("ownerTeamId").GetInt32());
        Assert.Equal(coachName, mon.GetProperty("owner").GetString());

        // Everything else stays unowned.
        Assert.All(pool.EnumerateArray().Where(m => Int(m, "id") != pickedId),
            m => Assert.Equal(JsonValueKind.Null, m.GetProperty("owner").ValueKind));
    }
}
