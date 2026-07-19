using System.Text.Json.Serialization;

namespace DraftLeague.Web.Services;

public class DiscordOptions
{
    public const string Section = "Discord";

    /// <summary>Public — safe to ship in the frontend.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// NEVER commit this and never send it to a client. Supply it via
    /// user-secrets locally or an env var in production
    /// (Discord__ClientSecret). See AUTH_SETUP.md.
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Redirect URIs a client is allowed to name in an exchange. The value the
    /// client sends must appear here AND be registered in the Discord portal —
    /// Discord checks it matches the one used at authorize time, and we check
    /// it's one we actually recognise, so a stolen code can't be redeemed
    /// against an attacker-chosen redirect.
    /// </summary>
    public string[] RedirectUris { get; set; } = [];
}

public record DiscordUser(string Id, string Username, string? Avatar);

/// <summary>Raw shape of Discord's token response.</summary>
internal record DiscordTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType);

/// <summary>Raw shape of Discord's /users/@me response.</summary>
internal record DiscordMeResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("avatar")] string? Avatar);

public interface IDiscordAuth
{
    /// <summary>
    /// Trades an authorization code for the Discord identity behind it.
    /// Returns null when Discord rejects the code — expired, replayed, wrong
    /// verifier, or simply forged.
    /// </summary>
    Task<DiscordUser?> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default);
}

public class DiscordAuth(HttpClient http, IConfiguration config, ILogger<DiscordAuth> log) : IDiscordAuth
{
    private const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
    private const string MeEndpoint = "https://discord.com/api/v10/users/@me";

    public async Task<DiscordUser?> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var opts = config.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();

        if (string.IsNullOrWhiteSpace(opts.ClientSecret))
        {
            log.LogError("Discord:ClientSecret is not configured — login cannot work. See AUTH_SETUP.md.");
            return null;
        }

        // Only redeem against a redirect we know. Discord also enforces this,
        // but relying solely on the caller's value would let a leaked code be
        // redeemed somewhere we never intended.
        if (!opts.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            log.LogWarning("Rejected exchange for unregistered redirect_uri {Redirect}", redirectUri);
            return null;
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            // Proves this caller started the flow. Without it a stolen code is
            // enough to impersonate someone.
            ["code_verifier"] = codeVerifier,
        });

        using var tokenRes = await http.PostAsync(TokenEndpoint, form, ct);
        if (!tokenRes.IsSuccessStatusCode)
        {
            // Body can echo the code; log status only.
            log.LogWarning("Discord token exchange failed: {Status}", tokenRes.StatusCode);
            return null;
        }

        var token = await tokenRes.Content.ReadFromJsonAsync<DiscordTokenResponse>(ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken)) return null;

        using var meReq = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
        meReq.Headers.Authorization = new("Bearer", token.AccessToken);

        using var meRes = await http.SendAsync(meReq, ct);
        if (!meRes.IsSuccessStatusCode)
        {
            log.LogWarning("Discord /users/@me failed: {Status}", meRes.StatusCode);
            return null;
        }

        var me = await meRes.Content.ReadFromJsonAsync<DiscordMeResponse>(ct);
        if (me is null || string.IsNullOrEmpty(me.Id)) return null;

        // Discord's access token isn't stored: we only needed it to learn who
        // this is. Keeping it would mean guarding a credential we never use.
        return new DiscordUser(me.Id, me.Username, me.Avatar);
    }
}
