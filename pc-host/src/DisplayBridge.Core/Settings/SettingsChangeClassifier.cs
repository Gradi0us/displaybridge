namespace DisplayBridge.Core.Settings;

/// <summary>How a settings change should be propagated to the connected device — catalog §3.5.</summary>
public enum ApplyType
{
    /// <summary>⚡ Take effect immediately: send CONFIG_UPDATE, no decoder flush.</summary>
    Live,

    /// <summary>⟳ Requires decoder reconfigure: send MODE_CHANGE, await MODE_ACK.</summary>
    ReMode,

    /// <summary>PC-side only, no protocol message (e.g. monitor position / scale).</summary>
    WindowsApi,

    /// <summary>Requires transport reconnect (connection group).</summary>
    Reconnect,

    /// <summary>Tablet-only local setting, not synced to PC.</summary>
    Local
}

/// <summary>Identifies a single settings field for classification purposes.</summary>
public enum SettingField
{
    // Display (Resolution is no longer a settable field, see AppSettings.cs
    // 2026-07-03 decision — always 100% native from CAPS, not user-chosen).
    DisplayRefreshRateHz,

    // Streaming
    StreamingCodec,
    StreamingQualityPreset,
    StreamingFpsCap,
    StreamingAdaptiveBitrate,
    StreamingLatencyPriority,

    // Input
    InputTouchEnabled,
    InputMode,
    InputPenPressureEnabled,
    InputPenPressureGamma,

    // Connection
    ConnectionTransport,
    ConnectionAutoConnect,
    ConnectionPorts,

    // Diagnostics
    DiagnosticsStatsOverlay,
    DiagnosticsLatencyHud,
    DiagnosticsLogLevel
}

/// <summary>
/// Classifies whether a settings field change should be sent as a live
/// CONFIG_UPDATE, a re-mode MODE_CHANGE, applied via the Windows API only,
/// requires a transport reconnect, or stays purely local. Mapping mirrors
/// CONTEXT-BRIEF-v2 §3 catalog exactly.
/// </summary>
public static class SettingsChangeClassifier
{
    public static ApplyType Classify(SettingField field) => field switch
    {
        // Display — refresh rate requires decoder reconfigure.
        SettingField.DisplayRefreshRateHz => ApplyType.ReMode,

        // Streaming — codec swap needs re-mode; the rest is live-tunable.
        SettingField.StreamingCodec => ApplyType.ReMode,
        SettingField.StreamingQualityPreset => ApplyType.Live,
        SettingField.StreamingFpsCap => ApplyType.Live,
        SettingField.StreamingAdaptiveBitrate => ApplyType.Live,
        SettingField.StreamingLatencyPriority => ApplyType.Live,

        // Input — all live per catalog §3.3.
        SettingField.InputTouchEnabled => ApplyType.Live,
        SettingField.InputMode => ApplyType.Live,
        SettingField.InputPenPressureEnabled => ApplyType.Live,
        SettingField.InputPenPressureGamma => ApplyType.Live,

        // Connection — transport/ports need a fresh handshake; auto-connect is
        // a host-side behavioral toggle only (catalog §4.1 "✎ host", no reconnect).
        SettingField.ConnectionTransport => ApplyType.Reconnect,
        SettingField.ConnectionAutoConnect => ApplyType.WindowsApi,
        SettingField.ConnectionPorts => ApplyType.Reconnect,

        // Diagnostics — all live, telemetry/logging only.
        SettingField.DiagnosticsStatsOverlay => ApplyType.Live,
        SettingField.DiagnosticsLatencyHud => ApplyType.Live,
        SettingField.DiagnosticsLogLevel => ApplyType.Live,

        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unclassified setting field.")
    };

    /// <summary>
    /// Given the set of fields that changed in one Save() operation, returns
    /// the most disruptive ApplyType that must be honored (ReMode &gt;
    /// Reconnect &gt; Live/WindowsApi/Local — a single ReMode field forces
    /// the whole batch through MODE_CHANGE).
    /// </summary>
    public static ApplyType ClassifyBatch(IEnumerable<SettingField> changedFields)
    {
        var applyTypes = changedFields.Select(Classify).ToList();
        if (applyTypes.Count == 0)
        {
            return ApplyType.Live;
        }

        if (applyTypes.Contains(ApplyType.ReMode))
        {
            return ApplyType.ReMode;
        }

        if (applyTypes.Contains(ApplyType.Reconnect))
        {
            return ApplyType.Reconnect;
        }

        if (applyTypes.Contains(ApplyType.WindowsApi))
        {
            return ApplyType.WindowsApi;
        }

        if (applyTypes.Contains(ApplyType.Local))
        {
            return ApplyType.Local;
        }

        return ApplyType.Live;
    }
}
