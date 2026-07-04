using System.Text.Json.Serialization;

namespace DisplayBridge.Core.Settings;

/// <summary>Input routing mode — see CONTEXT-BRIEF-v2 §3.3 / §4.2.</summary>
public enum InputMode
{
    Cursor,
    Touch,
    Hybrid
}

public enum Codec
{
    Auto,
    ForceHevc,
    ForceH264
}

public enum QualityPreset
{
    Low,
    Balanced,
    High,
    Ultra,
    Custom
}

public enum LatencyPriority
{
    LatencyFirst,
    SmoothFirst
}

public enum Transport
{
    Auto,
    Adb,
    Ncm
}

public enum StatsOverlayMode
{
    Off,
    Mini,
    Full
}

public enum LogLevel
{
    Error,
    Warn,
    Info,
    Debug
}

/// <summary>
/// Display group settings — catalog §3.1.
///
/// 2026-07-03 decision (session 7, RESEARCH-v2): resolution is NO LONGER a
/// user-selectable preset (Max/75%/50%/Custom all removed). There is
/// exactly one correct resolution: 100% of the connected device's native
/// resolution, read live from the CAPS handshake (see
/// <see cref="DeviceCaps"/>). This class only still owns RefreshRateHz,
/// which remains a real user preference (capped at 120Hz).
/// </summary>
public sealed class DisplaySettings
{
    /// <summary>Refresh rate in Hz. Hard cap 120 — 144Hz dropped 2026-07-03.</summary>
    public int RefreshRateHz { get; set; } = 120;
}

/// <summary>Streaming group settings — catalog §3.2.</summary>
public sealed class StreamingSettings
{
    public Codec Codec { get; set; } = Codec.Auto;
    public QualityPreset QualityPreset { get; set; } = QualityPreset.High;

    /// <summary>FPS cap; must be ≤ chosen refresh rate.</summary>
    public int FpsCap { get; set; } = 120;

    public bool AdaptiveBitrate { get; set; } = true;
    public LatencyPriority LatencyPriority { get; set; } = LatencyPriority.LatencyFirst;
}

/// <summary>Input group settings — catalog §3.3 (implemented fully by M3).</summary>
public sealed class InputSettings
{
    public bool TouchEnabled { get; set; } = true;
    public InputMode InputMode { get; set; } = InputMode.Hybrid;
    public bool PenPressureEnabled { get; set; } = true;

    /// <summary>Pen pressure curve gamma, valid range 0.5–2.0.</summary>
    public double PenPressureGamma { get; set; } = 1.0;
}

/// <summary>Connection group settings — catalog §3.4.</summary>
public sealed class ConnectionSettings
{
    public Transport Transport { get; set; } = Transport.Auto;
    public bool AutoConnect { get; set; } = true;
    public int VideoPort { get; set; } = 29500;
    public int ControlPort { get; set; } = 29501;
}

/// <summary>Diagnostics group settings — catalog §3.4.</summary>
public sealed class DiagnosticsSettings
{
    public StatsOverlayMode StatsOverlay { get; set; } = StatsOverlayMode.Off;
    public bool LatencyHud { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}

/// <summary>
/// Root settings model persisted to %APPDATA%\DisplayBridge\settings.json.
/// Adding new fields is safe — <see cref="SettingsStore"/> migrates missing
/// fields to their defaults on load (System.Text.Json leaves them at the
/// property initializer value when absent from the JSON payload).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Schema version, bumped whenever a breaking field change occurs.</summary>
    public int SchemaVersion { get; set; } = 1;

    public DisplaySettings Display { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    [JsonIgnore]
    public const int MaxRefreshRateHz = 120;
}
