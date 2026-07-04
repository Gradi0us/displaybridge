using System.Text.Json;

namespace DisplayBridge.Core.Settings;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under
/// %APPDATA%\DisplayBridge\settings.json. Values outside their valid domain
/// are clamped on load/save (e.g. refreshRateHz &gt; 120 -> 120); values that
/// cannot be safely clamped (Custom resolution exceeding device Max) throw
/// <see cref="SettingsValidationException"/> so the caller (UI) can surface
/// an error instead of silently corrupting user intent.
///
/// Missing fields in an on-disk file (from an older schema) simply fall back
/// to the property initializer defaults in <see cref="AppSettings"/> —
/// System.Text.Json leaves absent members at their default-constructed
/// value, which is exactly the "migrate safely" behavior we want.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath();
    }

    public static string DefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "DisplayBridge", "settings.json");
    }

    /// <summary>
    /// Loads settings from disk. Returns fresh defaults if the file doesn't
    /// exist yet. Always returns a value that has passed <see cref="Clamp"/>.
    /// </summary>
    public AppSettings Load(DeviceCaps? caps = null)
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(_filePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        Clamp(settings);
        return settings;
    }

    /// <summary>
    /// Validates (throwing on unrecoverable violations), clamps recoverable
    /// out-of-range values, then persists to disk.
    /// </summary>
    public void Save(AppSettings settings, DeviceCaps? caps = null)
    {
        Validate(settings, caps ?? DeviceCaps.Placeholder);
        Clamp(settings);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>
    /// No resolution-related validation is needed anymore — resolution is
    /// always derived 100% from live <see cref="DeviceCaps"/>, never a
    /// user-entered value (2026-07-03 decision, preset picker removed).
    /// Kept as a method (no-op today) so callers/tests don't need to change
    /// if a future field needs validation against <paramref name="caps"/>.
    /// </summary>
    public static void Validate(AppSettings settings, DeviceCaps caps)
    {
    }

    /// <summary>
    /// Clamps recoverable out-of-range values in place (does not throw).
    /// </summary>
    public static void Clamp(AppSettings settings)
    {
        if (settings.Display.RefreshRateHz > AppSettings.MaxRefreshRateHz)
        {
            settings.Display.RefreshRateHz = AppSettings.MaxRefreshRateHz;
        }
        else if (settings.Display.RefreshRateHz <= 0)
        {
            settings.Display.RefreshRateHz = 60;
        }

        if (settings.Streaming.FpsCap > settings.Display.RefreshRateHz)
        {
            settings.Streaming.FpsCap = settings.Display.RefreshRateHz;
        }
        else if (settings.Streaming.FpsCap <= 0)
        {
            settings.Streaming.FpsCap = settings.Display.RefreshRateHz;
        }

        if (settings.Input.PenPressureGamma < 0.5)
        {
            settings.Input.PenPressureGamma = 0.5;
        }
        else if (settings.Input.PenPressureGamma > 2.0)
        {
            settings.Input.PenPressureGamma = 2.0;
        }
    }
}
