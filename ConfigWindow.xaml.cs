using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace rans0m;

/// <summary>A custom reward as shown in the two reward pickers.</summary>
/// <param name="Id">Twitch's reward id, which is what actually gets stored.</param>
/// <param name="Label">Human readable title and cost, e.g. "Send the entity (500)".</param>
public sealed record RewardChoice(string Id, string Label);

public partial class ConfigWindow : Window
{
    private readonly TwitchApi _twitchApi = new();

    private bool _loaded;

    public ConfigWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppConfig config = Config.Current;

        cb_spawnAutomatically.IsChecked = config.SpawnAutomatically;
        tb_minSpawnDelay.Text = config.MinSpawnDelay.ToString(CultureInfo.InvariantCulture);
        tb_maxSpawnDelay.Text = config.MaxSpawnDelay.ToString(CultureInfo.InvariantCulture);
        tb_duration.Text = config.InfectionDuration.ToString(CultureInfo.InvariantCulture);

        cb_crashOnDeath.IsChecked = config.CrashOnDeath;
        cb_cmdOnDeath.IsChecked = config.ExecCMDOnDeath;
        tb_cmd.Text = config.CMDOnDeath;

        rb_userFolderMode.IsChecked = !config.UseDrawerMode;
        rb_drawerMode.IsChecked = config.UseDrawerMode;
        tb_ransomAmount.Text = config.RansomAmount.ToString(CultureInfo.InvariantCulture);

        TwitchSettings twitch = config.Twitch;
        cb_twitchEnabled.IsChecked = twitch.Enabled;
        tb_twitchChannel.Text = twitch.Channel;
        tb_clientId.Text = twitch.ClientId;
        tb_clientSecret.Text = twitch.ClientSecret;
        tb_redirectUri.Text = string.IsNullOrWhiteSpace(twitch.RedirectUri)
            ? TwitchOAuth.RedirectUri
            : twitch.RedirectUri;

        // An empty choice means "not set", so the pickers start with a blank row selected.
        RewardChoice[] placeholder = [new RewardChoice(string.Empty, "(none)")];
        cb_triggerReward.ItemsSource = placeholder;
        cb_forceReward.ItemsSource = placeholder;
        cb_dismissReward.ItemsSource = placeholder;
        cb_triggerReward.SelectedIndex = 0;
        cb_forceReward.SelectedIndex = 0;
        cb_dismissReward.SelectedIndex = 0;

        UpdateEnabledStates();

        _loaded = true;

        _ = RefreshRewardsAsync(twitch);
    }

    /// <summary>
    /// Fills the reward pickers with the channel's custom rewards. Failures are reported in the
    /// status label rather than thrown, since the user may simply not have connected yet.
    /// </summary>
    private async Task RefreshRewardsAsync(TwitchSettings twitch)
    {
        if (string.IsNullOrWhiteSpace(twitch.Channel)
            || string.IsNullOrWhiteSpace(twitch.ClientId)
            || string.IsNullOrWhiteSpace(twitch.ClientSecret))
        {
            return;
        }

        try
        {
            (string UserId, string Login)? user = await _twitchApi.GetUserAsync(twitch, twitch.Channel, CancellationToken.None);
            if (user is null)
            {
                txt_twitchStatus.Text = $"No channel called {twitch.Channel}.";
                return;
            }

            // Reward listing needs the broadcaster's own token; the app token used for the user
            // lookup above cannot read custom rewards.
            if (string.IsNullOrWhiteSpace(twitch.UserAccessToken))
            {
                txt_twitchStatus.Text = "Press Connect to authorize, then rewards will load.";
                return;
            }

            IReadOnlyList<(string Id, string Title, int Cost)> rewards =
                await _twitchApi.GetCustomRewardsAsync(twitch, user.Value.UserId, CancellationToken.None);

            List<RewardChoice> choices = [new RewardChoice(string.Empty, "(none)")];
            choices.AddRange(rewards.Select(reward => new RewardChoice(
                reward.Id,
                string.IsNullOrEmpty(reward.Title) ? reward.Id : $"{reward.Title} ({reward.Cost})")));

            cb_triggerReward.ItemsSource = choices;
            cb_forceReward.ItemsSource = choices;
            cb_dismissReward.ItemsSource = choices;

            SelectReward(cb_triggerReward, twitch.TriggerRewardId);
            SelectReward(cb_forceReward, twitch.ForceRewardId);
            SelectReward(cb_dismissReward, twitch.DismissRewardId);

            txt_twitchStatus.Text = $"{rewards.Count} reward(s) on {user.Value.Login}.";
        }
        catch (Exception ex) when (ex is TwitchApiException or HttpRequestException)
        {
            txt_twitchStatus.Text = ex.Message;
        }
    }

    /// <summary>Selects the stored reward id, falling back to the blank entry when it no longer exists.</summary>
    private static void SelectReward(ComboBox box, string rewardId)
    {
        if (box.ItemsSource is not IEnumerable<RewardChoice> choices)
        {
            return;
        }

        foreach (RewardChoice choice in choices)
        {
            if (string.Equals(choice.Id, rewardId, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = choice;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    /// <summary>Starts the browser based Twitch authorization and reports the outcome.</summary>
    private async void btn_twitchConnect_Click(object sender, RoutedEventArgs e)
    {
        TwitchSettings twitch = Config.Current.Twitch;

        twitch.ClientId = tb_clientId.Text.Trim();
        twitch.ClientSecret = tb_clientSecret.Text.Trim();
        twitch.Channel = tb_twitchChannel.Text.Trim().TrimStart('@');
        twitch.RedirectUri = tb_redirectUri.Text.Trim();

        // Authorizing a different Twitch app invalidates any token from the previous one, so clear
        // it here rather than letting Twitch reject it with "Client ID and OAuth token do not match".
        twitch.DiscardTokensForOtherClient();

        btn_twitchConnect.IsEnabled = false;
        txt_twitchStatus.Text = "Waiting for Twitch...";

        try
        {
            string? error = await TwitchBridge.ConnectAsync(twitch, Console.WriteLine, CancellationToken.None);
            txt_twitchStatus.Text = error ?? "Connected.";
        }
        finally
        {
            btn_twitchConnect.IsEnabled = true;
        }

        await RefreshRewardsAsync(twitch);
    }

    private void cb_twitchEnabled_Click(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    /// <summary>
    /// Enables inputs only when the option that gates them is on.
    /// </summary>
    private void UpdateEnabledStates()
    {
        bool spawnEnabled = cb_spawnAutomatically.IsChecked == true;

        tb_cmd.IsEnabled = cb_cmdOnDeath.IsChecked == true;
        tb_minSpawnDelay.IsEnabled = spawnEnabled;
        tb_maxSpawnDelay.IsEnabled = spawnEnabled;

        bool twitchEnabled = cb_twitchEnabled.IsChecked == true;

        gb_twitch.Opacity = twitchEnabled ? 1.0 : 0.55;
        tb_twitchChannel.IsEnabled = twitchEnabled;
        tb_clientId.IsEnabled = twitchEnabled;
        tb_clientSecret.IsEnabled = twitchEnabled;
        tb_redirectUri.IsEnabled = twitchEnabled;
        txt_redirectUriNote.Opacity = twitchEnabled ? 1.0 : 0.55;
        btn_twitchConnect.IsEnabled = twitchEnabled;
        cb_triggerReward.IsEnabled = twitchEnabled;
        cb_forceReward.IsEnabled = twitchEnabled;
        cb_dismissReward.IsEnabled = twitchEnabled;

        // Twitch takes over spawning entirely, so the auto-spawn checkbox is disabled and un-ticked
        // while it is on. Config keeps whatever the user had; only the UI reflects the override.
        cb_spawnAutomatically.IsEnabled = !twitchEnabled;
        txt_twitchAutoSpawnNote.Visibility = twitchEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (twitchEnabled)
        {
            cb_spawnAutomatically.IsChecked = false;
        }
    }

    private void cb_cmdOnDeath_Click(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void cb_spawnAutomatically_Click(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        AppConfig config = Config.Current;

        // While Twitch is on, the checkbox is force-cleared for display only, so writing it back would
        // silently discard the user's preference for when they turn Twitch off again.
        bool twitchOn = cb_twitchEnabled.IsChecked == true;

        if (!twitchOn)
        {
            config.SpawnAutomatically = cb_spawnAutomatically.IsChecked == true;
        }

        config.MinSpawnDelay = ParseClamped(tb_minSpawnDelay.Text, config.MinSpawnDelay, 0, 86400);
        config.MaxSpawnDelay = ParseClamped(tb_maxSpawnDelay.Text, config.MaxSpawnDelay, 0, 86400);
        config.InfectionDuration = ParseClamped(tb_duration.Text, config.InfectionDuration, 5, 86400);

        config.CrashOnDeath = cb_crashOnDeath.IsChecked == true;
        config.ExecCMDOnDeath = cb_cmdOnDeath.IsChecked == true;
        config.CMDOnDeath = tb_cmd.Text;

        config.UseDrawerMode = rb_drawerMode.IsChecked == true;
        config.RansomAmount = ParseClamped(tb_ransomAmount.Text, config.RansomAmount, 1, 1_000_000);

        TwitchSettings twitch = config.Twitch;
        twitch.Enabled = cb_twitchEnabled.IsChecked == true;
        twitch.Channel = tb_twitchChannel.Text.Trim().TrimStart('@');
        twitch.ClientId = tb_clientId.Text.Trim();
        twitch.ClientSecret = tb_clientSecret.Text.Trim();
        twitch.RedirectUri = tb_redirectUri.Text.Trim();

        // Applies on save too, so a client ID edited by hand cannot leave a mismatched token behind.
        twitch.DiscardTokensForOtherClient();

        twitch.TriggerRewardId = SelectedRewardId(cb_triggerReward);
        twitch.ForceRewardId = SelectedRewardId(cb_forceReward);
        twitch.DismissRewardId = SelectedRewardId(cb_dismissReward);

        Config.SaveConfig();

        // Cuts the current spawn delay so the settings above apply immediately.
        Global.OverlayWindow?.RetriggerSpawnLoop();

        // Enabling or disabling Twitch here changes whether redemptions are watched at all, so the
        // bridge is started or stopped now rather than at the next launch.
        Global.OverlayWindow?.ApplyTwitchSettingsAsync();
    }

    private static int ParseClamped(string text, int fallback, int min, int max)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(fallback, min, max);
    }

    /// <summary>Reads the reward id out of a picker, treating the blank entry as unset.</summary>
    private static string SelectedRewardId(ComboBox box)
    {
        return box.SelectedItem is RewardChoice choice ? choice.Id : string.Empty;
    }

    private void btn_spawn_Click(object sender, RoutedEventArgs e)
    {
        Overlay? overlay = Global.OverlayWindow;
        if (overlay is null)
        {
            return;
        }

        _ = overlay.SpawnRansom();

        // Cuts the current spawn delay so the next one starts from now.
        overlay.RetriggerSpawnLoop();
    }
}