using System.Net.Http;
using System.Text.Json;

namespace rans0m;

/// <summary>
/// Connects the app to Twitch channel point redemptions and turns them into ransom encounters.
/// </summary>
/// <remarks>
/// EventSub's websocket transport has an ordering requirement that drives the shape of this class:
/// a subscription is addressed to a <em>session</em>, and the session id only exists after the
/// socket says hello. So the socket connects first, and the subscription is created from the
/// <c>session_welcome</c> handler. Redemptions only start arriving after that second step, which
/// is why <see cref="IsReady"/> needs both.
/// </remarks>
public sealed class TwitchBridge : IAsyncDisposable
{
    /// <summary>The active bridge, so the configuration window and the overlay can reach it.</summary>
    public static TwitchBridge? Current { get; private set; }

    /// <summary>Raised with connection state for logging.</summary>
    public event Action<string>? Log;

    /// <summary>Raised when a redemption arrives, as (reward title, viewer name).</summary>
    public event Action<string, string>? RedemptionReceived;

    private readonly TwitchApi _api = new();
    private readonly EventSubClient _client = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private string? _broadcasterUserId;
    private string? _sessionId;

    /// <summary>True once a subscription is live and redemptions will actually be delivered.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Last error worth showing the user, or null when things are healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts listening, if the settings describe a usable connection.</summary>
    /// <returns>true if the integration was started.</returns>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        TwitchSettings settings = Config.Current.Twitch;

        Current = this;

        if (!settings.Enabled)
        {
            Log?.Invoke("twitch integration is disabled");
            return false;
        }

        // Tokens minted by a different Twitch app are rejected against the current Client-Id header, so
        // they have to go before anything else is attempted.
        if (settings.DiscardTokensForOtherClient())
        {
            // The stored token belongs to a different Twitch app, so Twitch would reject it against
            // the current client ID. Clearing it here turns an opaque API error into a prompt to
            // reconnect.
            Config.SaveConfig();
            LastError = "the Twitch client ID changed, so the old access token was discarded. "
                + "Press Connect in the configuration window to authorize the new app.";
            Log?.Invoke(LastError);
            return false;
        }

        if (!settings.IsUsable)
        {
            LastError = "Twitch is enabled but the channel, client ID, client secret or access token is missing.";
            Log?.Invoke(LastError);
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.TriggerRewardId)
            && string.IsNullOrWhiteSpace(settings.ForceRewardId)
            && string.IsNullOrWhiteSpace(settings.DismissRewardId))
        {
            LastError = "Twitch is enabled but no reward IDs are set, so redemptions would be ignored.";
            Log?.Invoke(LastError);
            return false;
        }

        try
        {
            await EnsureFreshTokenAsync(settings, cancellationToken);

            (string UserId, string Login)? user = await _api.GetUserAsync(settings, settings.Channel, cancellationToken);
            if (user is null)
            {
                LastError = $"Twitch has no channel called '{settings.Channel}'.";
                Log?.Invoke(LastError);
                return false;
            }

            _broadcasterUserId = user.Value.UserId;
            Log?.Invoke($"watching redemptions for {user.Value.Login} ({_broadcasterUserId})");

            _client.MessageReceived += OnMessage;
            _client.StatusChanged += OnClientStatus;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = _client.RunAsync(_cts.Token);

            return true;
        }
        catch (TwitchApiException ex)
        {
            LastError = ex.Message;
            Log?.Invoke($"twitch setup failed: {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex)
        {
            LastError = $"could not reach Twitch: {ex.Message}";
            Log?.Invoke(LastError);
            return false;
        }
    }

    /// <summary>
    /// Refreshes the stored user token if Twitch no longer accepts it. Twitch rotates the refresh
    /// token on every use, so the new pair is written straight back to disk.
    /// </summary>
    private async Task EnsureFreshTokenAsync(TwitchSettings settings, CancellationToken cancellationToken)
    {
        (bool valid, bool hasScope, _) = await _api.InspectUserTokenAsync(settings, cancellationToken);

        if (valid && hasScope)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.RefreshToken))
        {
            throw new TwitchApiException(
                "the stored Twitch access token is expired or lacks channel:read:redemptions, "
                    + "and there is no refresh token to renew it with. Reconnect Twitch in the configuration window.");
        }

        Log?.Invoke("access token needs renewing, refreshing");
        (string accessToken, string refreshToken) = await _api.RefreshUserTokenAsync(settings, cancellationToken);

        settings.UserAccessToken = accessToken;
        settings.RefreshToken = refreshToken;
        settings.TokenClientId = settings.ClientId;
        Config.SaveConfig();
    }

    private void OnClientStatus(string status) => Log?.Invoke($"eventsub: {status}");

    private async void OnMessage(string messageType, JsonElement payload)
    {
        try
        {
            if (messageType == "session_welcome")
            {
                await OnSessionWelcomeAsync(payload);
                return;
            }

            if (messageType == "notification")
            {
                OnRedemption(payload);
            }
        }
        catch (Exception ex)
        {
            // Handlers are async void so a failure here must not take the app down mid-stream.
            Log?.Invoke($"twitch message failed: {ex.Message}");
        }
    }

    /// <summary>Creates the subscription now that Twitch has told us which session to attach it to.</summary>
    private async Task OnSessionWelcomeAsync(JsonElement payload)
    {
        if (_broadcasterUserId is null
            || !payload.TryGetProperty("session", out JsonElement session)
            || !session.TryGetProperty("id", out JsonElement sessionIdElement))
        {
            return;
        }

        string sessionId = sessionIdElement.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        _sessionId = sessionId;

        // Clear anything a previous run left behind. A subscription pointing at a dead websocket
        // session never fires, and because Twitch treats the create as a conflict it would look
        // like we were connected while redemptions were quietly being dropped.
        await _api.RemoveSubscriptionsAsync(Config.Current.Twitch, _broadcasterUserId, CancellationToken.None);
        await _api.EnsureSubscriptionAsync(Config.Current.Twitch, _broadcasterUserId, sessionId, CancellationToken.None);

        IsReady = true;
        LastError = null;
        Log?.Invoke("subscribed to channel point redemptions");
    }

    /// <summary>
    /// Reads one redemption notification and runs the matching action on the UI thread.
    /// </summary>
    private void OnRedemption(JsonElement payload)
    {
        if (!payload.TryGetProperty("event", out JsonElement redemption)
            || !redemption.TryGetProperty("reward", out JsonElement reward)
            || !reward.TryGetProperty("id", out JsonElement rewardIdElement))
        {
            return;
        }

        TwitchSettings settings = Config.Current.Twitch;
        string rewardId = rewardIdElement.GetString() ?? string.Empty;
        string rewardTitle = reward.TryGetProperty("title", out JsonElement title)
            ? title.GetString() ?? "reward"
            : "reward";
        string viewer = redemption.TryGetProperty("user_name", out JsonElement userName)
            ? userName.GetString() ?? "someone"
            : "someone";

        // Unconfigured rewards default to empty, so an empty configured id must never match. Without
        // this, leaving a picker on "(none)" would make every redemption in chat trigger it.
        bool isTrigger = Matches(rewardId, settings.TriggerRewardId);
        bool isForce = Matches(rewardId, settings.ForceRewardId);
        bool isDismiss = Matches(rewardId, settings.DismissRewardId);

        if (!isTrigger && !isForce && !isDismiss)
        {
            return;
        }

        RedemptionReceived?.Invoke(rewardTitle, viewer);

        Overlay? overlay = Global.OverlayWindow;
        if (overlay is null)
        {
            Log?.Invoke($"{viewer} redeemed {rewardTitle} but the overlay is not ready yet");
            return;
        }

        if (isDismiss)
        {
            Log?.Invoke($"{viewer} redeemed {rewardTitle}, clearing the ransom");
            overlay.Dispatcher.InvokeAsync(() => _ = overlay.ResetRansom());
            return;
        }

        // Redemption storms are normal when a cheap reward goes live, and a second encounter on top
        // of an unresolved one would leave coins and timers behind. CanAttack already enforces this.
        if (!Global.State.CanAttack)
        {
            Log?.Invoke($"ignoring {viewer}'s {rewardTitle}: a ransom is already in progress");
            return;
        }

        Log?.Invoke($"{viewer} redeemed {rewardTitle}, starting a ransom");

        // A forced attack skips the flinch check, so it lands even on someone who never touched the
        // cursor. Everything after the warning phase is identical to a normal encounter.
        overlay.Dispatcher.InvokeAsync(() => _ = overlay.SpawnRansom(isForce));
    }

    /// <summary>
    /// Whether a redeemed reward id matches a configured one, ignoring case and never matching an
    /// unconfigured slot.
    /// </summary>
    private static bool Matches(string rewardId, string configuredRewardId) =>
        !string.IsNullOrWhiteSpace(configuredRewardId)
        && string.Equals(rewardId, configuredRewardId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tears the connection down and removes the subscription, so exiting does not leave Twitch
    /// delivering notifications to nothing.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            _cts.Cancel();
        }

        _client.Abort();

        Task? runTask = _runTask;

        try
        {
            if (runTask is not null)
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception)
        {
            // A read that does not unblock in time is not worth failing shutdown over.
        }

        _client.MessageReceived -= OnMessage;
        _client.StatusChanged -= OnClientStatus;

        IsReady = false;

        if (_broadcasterUserId is not null)
        {
            try
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                await _api.RemoveSubscriptionsAsync(Config.Current.Twitch, _broadcasterUserId, cleanup.Token);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"could not remove the twitch subscription: {ex.Message}");
            }
        }

        Log?.Invoke("twitch integration stopped");
    }

    /// <summary>
    /// Runs the interactive authorization flow and stores the resulting tokens on
    /// <paramref name="settings"/>. Bound to the configuration window's Connect button.
    /// </summary>
    /// <returns>null on success, or a message explaining what went wrong.</returns>
    public static async Task<string?> ConnectAsync(
        TwitchSettings settings, Action<string>? log, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return "Enter a client ID and client secret first.";
        }

        if (string.IsNullOrWhiteSpace(settings.RedirectUri))
        {
            return "Enter the OAuth Redirect URL from your Twitch app's console.";
        }

        System.Net.Sockets.TcpListener listener;
        string redirectUri;

        try
        {
            (listener, redirectUri) = TwitchOAuth.CreateListener(settings.RedirectUri);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        string state = Guid.NewGuid().ToString("N");

        // Spell the URI out exactly, because Twitch compares it as a literal string. Registering a
        // different port, or this one without the trailing slash, fails with redirect_mismatch.
        log?.Invoke($"open the browser to authorize; the redirect URI registered in the Twitch "
            + $"developer console must be exactly: {redirectUri}");

        TwitchOAuth.OpenBrowser(TwitchOAuth.BuildAuthorizeUrl(settings, redirectUri, state));

        string? code = await TwitchOAuth.WaitForCodeAsync(listener, state, cancellationToken);
        if (code is null)
        {
            return "Authorization was cancelled or timed out. If the page showed a redirect error, the "
                + "Redirect URI box must match the Twitch console's OAuth Redirect URL exactly.";
        }

        try
        {
            using TwitchApi api = new();
            (string accessToken, string refreshToken) = await api.ExchangeCodeAsync(
                settings, code, redirectUri, cancellationToken);

            settings.UserAccessToken = accessToken;
            settings.RefreshToken = refreshToken;
            settings.TokenClientId = settings.ClientId;
            Config.SaveConfig();
        }
        catch (TwitchApiException ex)
        {
            return ex.Message;
        }

        log?.Invoke("connected to twitch");
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _api.Dispose();

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}