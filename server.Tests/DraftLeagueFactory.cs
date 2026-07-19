using System.Net.Http.Headers;
using System.Net.Http.Json;
using DraftLeague.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Boots the real app against a throwaway SQLite file.
///
/// Development is deliberate: it's what enables DevSeed, the /dev endpoints the
/// tests authenticate through, and the generated Jwt:Key. Running these against
/// Production config would fail at startup for want of a key.
/// </summary>
public class DraftLeagueFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"draftleague-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Tests must not reach out to the live Google Sheet on every draft start
        // (slow, flaky, offline in CI). Blank disables the pool sync.
        builder.ConfigureAppConfiguration(cfg =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Pokedex:SheetCsvUrl"] = "" }));

        builder.ConfigureServices(services =>
        {
            // The app registers AppDbContext against the real draftleague.db.
            // Tests must never touch it — a stray write would corrupt the dev
            // data the app is running on.
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        // SQLite pools connections; without this the file is still locked.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>
    /// A client authenticated as a seeded coach, via the Development-only
    /// /dev/token endpoint — no Discord round trip.
    /// </summary>
    public async Task<HttpClient> SignedInAsAsync(string discordId, bool admin = false)
    {
        var client = CreateClient();
        var res = await client.PostAsync($"/dev/token/{discordId}?admin={admin}", null);
        res.EnsureSuccessStatusCode();

        var token = await res.Content.ReadFromJsonAsync<DevToken>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    public sealed record DevToken(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt, bool IsAdmin);
}
