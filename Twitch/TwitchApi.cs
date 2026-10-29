using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace rans0m;

/// <summary>Raised when Twitch rejects a request, carrying the message the API returned.</summary>
public sealed class TwitchApiException : Exception
{
    public TwitchApiException(string message)
        : base(message)
    {
    }

    public TwitchApiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The handful of Twitch endpoints this app needs: token exchange, identity lookup, custom
/// reward listing, and EventSub subscription management.
/// </summary>
public sealed class TwitchApi : IDisposable
{
    /// <summary>The only scope required to receive channel point redemptions.</summary>
    public const string RedemptionScope = "channel:read:redemptions";

    private const string IdBase = "https://id.twitch.tv/oauth2";
    private const string ApiBase = "https://api.twitch.tv/helix";
    private const string EventSubType = "channel.channel_points_custom_reward_redemption.add";
    private const string EventSubVersion = "1";

    private readonly HttpClient _http;

    // An app access token belongs to exactly one client ID. Twitch rejects a Helix call whose
    // Client-Id header does not match the token with "Client ID and OAuth token do not match", so
    // the cache records which client the token was minted for and is discarded when that changes.
    // Without this, swapping in a new Twitch app keeps serving the old app's token until restart.
    private string? _appAccessToken;
    private string? _appAccessTokenClientId;
    private DateTimeOffset _appAccessTokenExpiry;

    public TwitchApi()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("rans0m/2.0.0");
    }

    /// <summary>
    /// Returns a client-credentials app access token, cached until shortly before it expires.
    /// </summary>
    public async Task<string> GetAppAccessTokenAsync(TwitchSettings settings, CancellationToken cancellationToken)
    {
        if (_appAccessToken is not null
            && string.Equals(_appAccessTokenClientId, settings.ClientId, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow < _appAccessTokenExpiry)
        {
            return _appAccessToken;
        }

        using StringContent content = new(
            $"client_id={Uri.EscapeDataString(settings.ClientId)}"
                + $"&client_secret={Uri.EscapeDataString(settings.ClientSecret)}"
                + "&grant_type=client_credentials",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using HttpResponseMessage response = await _http.PostAsync($"{IdBase}/token", content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchApiException($"app token request failed: {DescribeError(body, response.StatusCode)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        _appAccessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        _appAccessTokenClientId = settings.ClientId;
        int expiresIn = root.TryGetProperty("expires_in", out JsonElement expires) ? expires.GetInt32() : 0;

        // Refresh early so a long-lived connection does not expire mid-use.
        _appAccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));

        return _appAccessToken;
    }

    /// <summary>
    /// Swaps a refresh token for a fresh user access token. Twitch returns a new refresh token too,
    /// so the caller must persist the returned one or the integration dies on the next expiry.
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken)> RefreshUserTokenAsync(
        TwitchSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.RefreshToken))
        {
            throw new TwitchApiException(
                "no refresh token stored - reconnect the app to Twitch to get a new one");
        }

        using StringContent content = new(
            $"client_id={Uri.EscapeDataString(settings.ClientId)}"
                + $"&client_secret={Uri.EscapeDataString(settings.ClientSecret)}"
                + $"&refresh_token={Uri.EscapeDataString(settings.RefreshToken)}"
                + $"&grant_type=refresh_token&scope={RedemptionScope}",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using HttpResponseMessage response = await _http.PostAsync($"{IdBase}/token", content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchApiException($"token refresh failed: {DescribeError(body, response.StatusCode)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        string refreshToken = root.TryGetProperty("refresh_token", out JsonElement refresh)
            ? refresh.GetString() ?? settings.RefreshToken
            : settings.RefreshToken;

        return (accessToken, refreshToken);
    }

    /// <summary>
    /// Exchanges an OAuth authorization code for a user access token and a refresh token.
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        TwitchSettings settings, string code, string redirectUri, CancellationToken cancellationToken)
    {
        using StringContent content = new(
            $"client_id={Uri.EscapeDataString(settings.ClientId)}"
                + $"&client_secret={Uri.EscapeDataString(settings.ClientSecret)}"
                + $"&code={Uri.EscapeDataString(code)}"
                + $"&grant_type=authorization_code&redirect_uri={Uri.EscapeDataString(redirectUri)}",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using HttpResponseMessage response = await _http.PostAsync($"{IdBase}/token", content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchApiException($"code exchange failed: {DescribeError(body, response.StatusCode)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        return (
            root.GetProperty("access_token").GetString() ?? string.Empty,
            root.TryGetProperty("refresh_token", out JsonElement refresh) ? refresh.GetString() ?? string.Empty : string.Empty);
    }

    /// <summary>
    /// Builds a Helix request. Twitch requires the <c>Client-ID</c> header on every Helix call,
    /// including the ones authenticated with an app access token, so it is set unconditionally
    /// rather than only for user-token requests.
    /// </summary>
    private HttpRequestMessage CreateHelixRequest(HttpMethod method, string url, TwitchSettings settings, string token)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", settings.ClientId);
        return request;
    }

    /// <summary>
    /// Returns the user ID and login for a channel name, or null when Twitch does not know it.
    /// </summary>
    public async Task<(string UserId, string Login)?> GetUserAsync(
        TwitchSettings settings, string login, CancellationToken cancellationToken)
    {
        string token = await GetAppAccessTokenAsync(settings, cancellationToken);
        using HttpRequestMessage request = CreateHelixRequest(
            HttpMethod.Get, $"{ApiBase}/users?login={Uri.EscapeDataString(login)}", settings, token);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchApiException($"user lookup failed: {DescribeError(body, response.StatusCode)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement user = data[0];
        return (
            user.GetProperty("id").GetString() ?? string.Empty,
            user.GetProperty("login").GetString() ?? string.Empty);
    }

    /// <summary>
    /// Verifies a user access token and reports whether it actually carries the redemption scope.
    /// </summary>
    public async Task<(bool Valid, bool HasScope, string? Login)> InspectUserTokenAsync(
        TwitchSettings settings, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{IdBase}/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.UserAccessToken);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, false, null);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        bool hasScope = root.TryGetProperty("scopes", out JsonElement scopes)
            && scopes.EnumerateArray().Any(scope => scope.GetString() == RedemptionScope);

        return (
            true,
            hasScope,
            root.TryGetProperty("login", out JsonElement login) ? login.GetString() : null);
    }

    /// <summary>
    /// Lists the channel's custom rewards so the configuration window can offer them as choices
    /// instead of asking the user to paste a raw ID.
    /// </summary>
    /// <remarks>
    /// Uses the broadcaster's user access token rather than the app token: this endpoint rejects
    /// app credentials outright.
    /// </remarks>
    public async Task<IReadOnlyList<(string Id, string Title, int Cost)>> GetCustomRewardsAsync(
        TwitchSettings settings, string broadcasterUserId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateHelixRequest(
            HttpMethod.Get,
            $"{ApiBase}/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(broadcasterUserId)}",
            settings,
            settings.UserAccessToken);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchApiException($"reward lookup failed: {DescribeError(body, response.StatusCode)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        List<(string, string, int)> rewards = [];

        if (document.RootElement.TryGetProperty("data", out JsonElement data))
        {
            foreach (JsonElement reward in data.EnumerateArray())
            {
                rewards.Add((
                    reward.GetProperty("id").GetString() ?? string.Empty,
                    reward.GetProperty("title").GetString() ?? string.Empty,
                    reward.TryGetProperty("cost", out JsonElement cost) ? cost.GetInt32() : 0));
            }
        }

        return rewards;
    }

    /// <summary>Creates the websocket subscription that delivers redemption notifications.</summary>
    public async Task EnsureSubscriptionAsync(
        TwitchSettings settings, string broadcasterUserId, string sessionId, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            type = EventSubType,
            version = EventSubVersion,
            condition = new { broadcaster_user_id = broadcasterUserId },
            transport = new { method = "websocket", session_id = sessionId },
        });

        using StringContent content = new(payload, Encoding.UTF8, "application/json");
        using HttpRequestMessage request = CreateHelixRequest(
            HttpMethod.Post, $"{ApiBase}/eventsub/subscriptions", settings, settings.UserAccessToken);
        request.Content = content;

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

        // Twitch answers 202 for a good create and 409 when the identical subscription already
        // exists, which happens after an unclean shutdown. Both are a success for our purposes.
        if (response.StatusCode is System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new TwitchApiException($"subscription failed: {DescribeError(body, response.StatusCode)}");
        }
    }

    /// <summary>
    /// Removes this app's EventSub subscriptions for the channel. Used on shutdown so a clean exit
    /// does not leave a subscription that fires into nothing.
    /// </summary>
    public async Task RemoveSubscriptionsAsync(
        TwitchSettings settings, string broadcasterUserId, CancellationToken cancellationToken)
    {
        string token = await GetAppAccessTokenAsync(settings, cancellationToken);

        using HttpRequestMessage listRequest = CreateHelixRequest(
            HttpMethod.Get,
            $"{ApiBase}/eventsub/subscriptions?type={Uri.EscapeDataString(EventSubType)}"
                + $"&version={EventSubVersion}&user_id={Uri.EscapeDataString(broadcasterUserId)}",
            settings,
            token);

        using HttpResponseMessage listResponse = await _http.SendAsync(listRequest, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            return;
        }

        string listBody = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(listBody);

        if (!document.RootElement.TryGetProperty("data", out JsonElement data))
        {
            return;
        }

        foreach (JsonElement subscription in data.EnumerateArray())
        {
            string? id = subscription.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString()
                : null;

            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            using HttpRequestMessage deleteRequest = CreateHelixRequest(
                HttpMethod.Delete, $"{ApiBase}/eventsub/subscriptions/{id}", settings, token);

            using HttpResponseMessage deleteResponse = await _http.SendAsync(deleteRequest, cancellationToken);
        }
    }

    /// <summary>Pulls a human-readable reason out of a Twitch error body.</summary>
    private static string DescribeError(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("message", out JsonElement message)
                && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return string.IsNullOrWhiteSpace(body) ? status.ToString() : body;
    }

    public void Dispose() => _http.Dispose();
}