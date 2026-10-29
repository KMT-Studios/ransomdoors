namespace rans0m;

/// <summary>
/// Twitch integration settings, nested under <c>Twitch</c> in <c>config.json</c>.
/// </summary>
/// <remarks>
/// Stored in plaintext next to the executable, same as the rest of the config. That is fine for
/// the single-operator prank this app is, but it does mean the client secret and tokens are only
/// as protected as the user's home directory. Keep that in mind before reusing this on a shared
/// or streamed machine where other users or a live capture could read the file.
/// </remarks>
public sealed class TwitchSettings
{
    /// <summary>Whether to connect to Twitch at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Login name of the channel whose redemptions should be watched, without the @.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Client ID of the Twitch application used for EventSub.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret of that application. Used for token exchange only.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth redirect URI, which must match a registered URI character for character. Twitch
    /// rejects a mismatch with <c>redirect_mismatch</c>, so this is editable rather than hardcoded:
    /// whatever is listed under OAuth Redirect URLs in the developer console belongs here verbatim.
    /// </summary>
    public string RedirectUri { get; set; } = TwitchOAuth.RedirectUri;

    /// <summary>
    /// Client ID the stored tokens were issued for. Twitch ties every token to the app that minted
    /// it, so a token from an old app is rejected once <see cref="ClientId"/> changes. Tracking the
    /// issuer lets the app discard those tokens instead of reporting a confusing API error.
    /// </summary>
    public string TokenClientId { get; set; } = string.Empty;

    /// <summary>
    /// User access token for the broadcaster, with the <c>channel:read:redemptions</c> scope.
    /// Rotated automatically from <see cref="RefreshToken"/> when it expires.
    /// </summary>
    public string UserAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Long-lived refresh token. Twitch only issues one if the authorization request asked for
    /// <c>access_type=offline</c>, and it only returns it once, so losing it means reconnecting.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Custom reward that starts a ransom encounter. Leave empty to ignore redemptions entirely.
    /// This is the dodgeable version: holding the cursor still still gets you out.
    /// </summary>
    public string TriggerRewardId { get; set; } = string.Empty;

    /// <summary>
    /// Custom reward that starts a ransom the user cannot dodge. The normal warning phase lets a
    /// viewer who never touches the cursor walk away; this skips that check, so the attack lands
    /// even on someone who is completely still.
    /// </summary>
    public string ForceRewardId { get; set; } = string.Empty;

    /// <summary>
    /// Custom reward that clears an in-progress ransom. Set this to the same reward viewers use
    /// in the orb overlay so summoning an orb also dismisses RANS0M.
    /// </summary>
    public string DismissRewardId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the settings describe a complete, usable connection. Checked before attempting to
    /// start so a half-filled config produces a clear message instead of a confusing API error.
    /// </summary>
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(Channel)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(UserAccessToken);

    /// <summary>
    /// Drops any stored tokens when they were issued for a different client ID. Twitch rejects a
    /// token whose app no longer matches the <c>Client-Id</c> header, so keeping them after a client
    /// ID change produces "Client ID and OAuth token do not match" instead of a usable connection.
    /// Returns true when tokens were discarded.
    /// </summary>
    public bool DiscardTokensForOtherClient()
    {
        if (string.IsNullOrWhiteSpace(TokenClientId)
            || string.Equals(TokenClientId, ClientId, StringComparison.Ordinal))
        {
            return false;
        }

        UserAccessToken = string.Empty;
        RefreshToken = string.Empty;
        TokenClientId = string.Empty;
        return true;
    }

    /// <summary>Normalizes nulls so the rest of the code never has to null-check a setting.</summary>
    public void ClampToSaneRanges()
    {
        Channel ??= string.Empty;
        ClientId ??= string.Empty;
        ClientSecret ??= string.Empty;
        RedirectUri ??= string.Empty;
        UserAccessToken ??= string.Empty;
        RefreshToken ??= string.Empty;
        TokenClientId ??= string.Empty;
        TriggerRewardId ??= string.Empty;
        ForceRewardId ??= string.Empty;
        DismissRewardId ??= string.Empty;

        Channel = Channel.Trim().TrimStart('@').ToLowerInvariant();
        ClientId = ClientId.Trim();
        ClientSecret = ClientSecret.Trim();
        RedirectUri = RedirectUri.Trim();
        TriggerRewardId = TriggerRewardId.Trim();
        ForceRewardId = ForceRewardId.Trim();
        DismissRewardId = DismissRewardId.Trim();
    }
}