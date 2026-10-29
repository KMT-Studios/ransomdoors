using System.IO;
using System.Text.Json;

namespace rans0m;

/// <summary>
/// Strongly typed view over the user's settings.
/// </summary>
/// <remarks>
/// The previous implementation reflected over the static properties of the <c>Config</c> class,
/// kept the raw JSON in a <c>Dictionary&lt;string, object&gt;</c>, and re-parsed a
/// <see cref="JsonElement"/> on every single property read. Because <c>Dictionary&lt;string,
/// object&gt;</c> does not round-trip cleanly, a value set in memory could serialize back out
/// as a different JSON type than it went in as. Binding straight to this record makes that
/// class of bug unrepresentable, and keeps <c>config.json</c> byte-compatible.
/// </remarks>
public sealed class AppConfig
{
    public bool SpawnAutomatically { get; set; } = true;

    public int MinSpawnDelay { get; set; } = 5;

    public int MaxSpawnDelay { get; set; } = 600;

    public int InfectionDuration { get; set; } = 90;

    public bool CrashOnDeath { get; set; }

    public bool ExecCMDOnDeath { get; set; }

    public string CMDOnDeath { get; set; } = "shutdown /s /t 0";

    public bool UseDrawerMode { get; set; }

    public int RansomAmount { get; set; } = 500;

    private TwitchSettings? _twitch;

    /// <summary>
    /// Twitch channel point integration. Never null, so callers can read it without checking even
    /// when an older <c>config.json</c> has no <c>Twitch</c> key at all.
    /// </summary>
    public TwitchSettings Twitch
    {
        get => _twitch ??= new TwitchSettings();
        set => _twitch = value ?? new TwitchSettings();
    }

    /// <summary>
    /// Clamps values that would otherwise produce an unplayable or unsafe state, such as a
    /// zero-length infection duration or a ransom of zero.
    /// </summary>
    public void ClampToSaneRanges()
    {
        MinSpawnDelay = Math.Clamp(MinSpawnDelay, 0, 86400);
        MaxSpawnDelay = Math.Clamp(MaxSpawnDelay, 0, 86400);
        InfectionDuration = Math.Clamp(InfectionDuration, 5, 86400);
        RansomAmount = Math.Clamp(RansomAmount, 1, 1_000_000);

        CMDOnDeath ??= string.Empty;

        Twitch.ClampToSaneRanges();
    }
}

/// <summary>
/// Loads and saves <see cref="AppConfig"/> to <c>config.json</c> next to the executable.
/// </summary>
public static class Config
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static AppConfig _current = new();

    /// <summary>The active configuration. Safe to read from any thread once loaded.</summary>
    public static AppConfig Current => _current;

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static bool ConfigExists() => File.Exists(ConfigPath);

    /// <summary>
    /// Loads the config file, returning false if it is missing or unreadable so the caller can
    /// fall back to first-run behavior.
    /// </summary>
    public static bool LoadConfig()
    {
        if (!ConfigExists())
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (loaded is null)
            {
                return false;
            }

            loaded.ClampToSaneRanges();
            _current = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Writes the active configuration back to disk.</summary>
    public static void SaveConfig()
    {
        _current.ClampToSaneRanges();

        // Write to a temp file first so a crash mid-write cannot leave a truncated config
        // that fails to parse on the next launch.
        string tempPath = ConfigPath + ".tmp";

        using (FileStream stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, _current, SerializerOptions);
        }

        File.Move(tempPath, ConfigPath, overwrite: true);
    }
}