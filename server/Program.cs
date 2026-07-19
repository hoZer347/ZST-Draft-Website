using System.Security.Claims;
using System.Text;
using DraftLeague.Web.Api;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// The frontend is served by Netlify on another origin, so every call from the
// browser is cross-origin and blocked without this. AllowCredentials is what
// SignalR's WebSocket handshake needs — and it forbids AllowAnyOrigin, hence
// the explicit list.
const string CorsPolicy = "frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:8000", "https://zst-league.netlify.app"];

builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default")
                ?? "Data Source=draftleague.db"));

// ── auth ────────────────────────────────────────────────────────────────
// In Development a throwaway key is generated so the app runs with no setup.
// It changes every restart (invalidating tokens), and Production refuses to
// start without a real one rather than silently signing with something guessable.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Jwt:Key must be configured outside Development. See AUTH_SETUP.md.");

    jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    builder.Configuration["Jwt:Key"] = jwtKey;
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "draft-league",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "draft-league",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            // Default is 5 minutes of slack, which keeps expired tokens working
            // well past their stated lifetime.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Browsers can't set headers on a WebSocket handshake, so
                // SignalR passes the token as a query param instead. Only
                // honour it for hub paths.
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddScoped<TokenService>();
builder.Services.AddHttpClient<IDiscordAuth, DiscordAuth>();

// Development-only services: the debug-slot claim state, and the season
// simulator (a typed HttpClient, since it fetches Showdown replays).
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<DebugSlots>();
    builder.Services.AddHttpClient<SeasonSimulator>(c => c.Timeout = TimeSpan.FromSeconds(20));
}

builder.Services.AddScoped<DraftEngine>();
builder.Services.AddScoped<IDraftNotifier, DraftNotifier>();

// Pulls the draft pool from the source sheet when a draft starts. Typed client
// with a short timeout so a slow sheet can't hang the start request.
builder.Services.AddHttpClient<PokedexSync>(c => c.Timeout = TimeSpan.FromSeconds(15));

// Fetches Showdown replays to score reported matches. Short timeout so a slow
// replay host can't hang the submit request.
builder.Services.AddHttpClient<ReplayScorer>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<INotificationQueue, NotificationQueue>();

// Swap for an FCM-backed sender once credentials are configured — see PUSH_SETUP.md.
builder.Services.AddScoped<IPushSender, LoggingPushSender>();

builder.Services.AddHostedService<DraftClock>();
builder.Services.AddHostedService<PushDispatcher>();

// Fly (and any reverse proxy) terminates TLS and forwards plain HTTP. Without
// this, UseHttpsRedirection sees scheme=http and redirects every request into
// an infinite loop. KnownNetworks/Proxies are cleared because the proxy's
// address inside Fly's private network isn't known ahead of time.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// Dev convenience: create the schema on boot. Replace with migrations
// (dotnet ef database update) before this ever holds a real league.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    if (app.Environment.IsDevelopment()) await DevSeed.SeedAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Serve the web/ SPA (the real UI) from the site root, so one self-hosted server
// hosts BOTH the frontend and the API on a single origin — no separate static
// host, and no CORS between them. web/ sits next to server/ in the repo.
var webRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "web"));
if (Directory.Exists(webRoot))
{
    var webFiles = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });   // "/" -> index.html
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });
}

app.UseHttpsRedirection();
app.UseRouting();

// Must sit between UseRouting and the endpoints, or preflight OPTIONS
// requests never get the headers and the browser blocks everything.
app.UseCors(CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapHub<DraftHub>("/hubs/draft").RequireCors(CorsPolicy);
app.MapAuthApi(CorsPolicy);
app.MapMobileApi(CorsPolicy);
app.MapPlayersApi(CorsPolicy);
app.MapScheduleApi(CorsPolicy);

if (app.Environment.IsDevelopment())
{
    // Four shared, claimable dummy players (see DebugSlotsApi) — the web and
    // Android clients both drive these instead of a Discord login.
    app.MapDebugSlots(CorsPolicy);

    // Rebuild the league as a finished season from canned data + replays, so the
    // schedule/standings/team pages can be tested without drafting one by hand.
    // Admin-only and Development-only.
    app.MapPost("/dev/simulate-season", async (
        SeasonSimulator sim, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
    {
        if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
        var draft = await db.Drafts.OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        if (draft is null) return Results.NotFound();
        var result = await sim.SimulateAsync(draft.Id, ct);
        return Results.Ok(result);
    }).RequireAuthorization().RequireCors(CorsPolicy);

    // Shortens the clock so auto-pick and warnings can be exercised without
    // sitting through a real five-minute pick. Development only — this would
    // let anyone skip a coach's turn.
    app.MapPost("/dev/drafts/{id:int}/expire", async (int id, AppDbContext db) =>
    {
        var d = await db.Drafts.FindAsync(id);
        if (d is null) return Results.NotFound();
        d.PickDeadline = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        return Results.Ok();
    });

    // Mints a token for a seeded coach so the draft can be driven without
    // standing up Discord. Development only — it is an auth bypass.
    app.MapPost("/dev/token/{discordId}", async (string discordId, bool? admin, AppDbContext db, TokenService tokens) =>
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId);
        if (user is null)
        {
            user = new DraftLeague.Web.Models.User
            {
                DiscordId = discordId,
                Username = discordId,
                IsAdmin = admin ?? false,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        else if (admin is not null && user.IsAdmin != admin)
        {
            user.IsAdmin = admin.Value;
            await db.SaveChangesAsync();
        }
        var pair = await tokens.IssueAsync(user, "dev");
        return Results.Ok(new { pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAt, user.IsAdmin });
    });
}

app.Run();

// Top-level statements compile to an internal Program, which
// WebApplicationFactory<Program> can't reach. This exposes it so the tests can
// boot the real app rather than a stand-in.
public partial class Program;
