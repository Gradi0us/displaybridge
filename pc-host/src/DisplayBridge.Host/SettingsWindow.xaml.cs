using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DisplayBridge.Core.Settings;

namespace DisplayBridge.Host;

/// <summary>
/// Simple label+value wrapper so ComboBox can show a human string
/// while carrying a strongly-typed value.
/// </summary>
internal sealed record ComboOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// M4 settings UI. Loads current <see cref="AppSettings"/> via
/// <see cref="SettingsStore"/> on open, lets the user edit Streaming /
/// Input groups from catalog §3, and persists on Save.
///
/// 2026-07-03 decision (session 7, RESEARCH-v2): Resolution is NO LONGER
/// user-selectable here — removed the Max/75%/50%/Custom ComboBox entirely.
/// The only correct resolution is always 100% of the connected device's
/// native size (see <see cref="DeviceCaps"/>), so this window just shows it
/// read-only via <c>ResolutionValueText</c>.
///
/// NOTE: sending CONFIG_UPDATE / MODE_CHANGE to a connected device is not
/// wired up yet — there is no live connection at this stage (M1 handshake
/// is being built in parallel). On Save we only classify + log what
/// message *would* be sent; TODO(M4.2): route through the real transport
/// once M1's control socket lands.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly DeviceCaps _caps;
    private AppSettings _original;

    // 2026-07-03 RCA fix (RCA-v1-resolution-stuck-800x600.md, RC1): the
    // parameterless constructor used to default to DeviceCaps.Placeholder
    // (2560x1600, which happens to match the dev laptop's own resolution)
    // and was invoked silently by App.xaml.cs/MainWindow.xaml.cs instead of
    // the real connected device's CurrentDeviceCaps. That bug compiled fine
    // and never threw, so it went unnoticed for several sessions. Removed
    // entirely (not just deprecated) so any future callsite that forgets to
    // pass real caps fails at COMPILE time instead of silently showing the
    // wrong resolution at runtime -- see RCA "Preventive Actions".
    internal SettingsWindow(SettingsStore store, DeviceCaps caps)
    {
        InitializeComponent();
        _store = store;
        _caps = caps;
        _original = _store.Load(_caps);

        PopulateComboBoxes();
        BindValuesFromSettings(_original);
    }

    private void PopulateComboBoxes()
    {
        ResolutionValueText.Text = $"{_caps.NativeWidth}x{_caps.NativeHeight} (tự động theo thiết bị)";

        // 144Hz intentionally dropped (2026-07-03 decision) — 120Hz is the ceiling.
        RefreshRateComboBox.Items.Add(new ComboOption<int>("60 Hz", 60));
        RefreshRateComboBox.Items.Add(new ComboOption<int>("90 Hz", 90));
        RefreshRateComboBox.Items.Add(new ComboOption<int>("120 Hz", 120));

        QualityPresetComboBox.Items.Add(new ComboOption<QualityPreset>("Low", QualityPreset.Low));
        QualityPresetComboBox.Items.Add(new ComboOption<QualityPreset>("Balanced", QualityPreset.Balanced));
        QualityPresetComboBox.Items.Add(new ComboOption<QualityPreset>("High", QualityPreset.High));
        QualityPresetComboBox.Items.Add(new ComboOption<QualityPreset>("Ultra", QualityPreset.Ultra));
        QualityPresetComboBox.Items.Add(new ComboOption<QualityPreset>("Custom", QualityPreset.Custom));

        InputModeComboBox.Items.Add(new ComboOption<InputMode>("Cursor-only", InputMode.Cursor));
        InputModeComboBox.Items.Add(new ComboOption<InputMode>("Touch-only", InputMode.Touch));
        InputModeComboBox.Items.Add(new ComboOption<InputMode>("Hybrid (auto)", InputMode.Hybrid));
    }

    private void BindValuesFromSettings(AppSettings settings)
    {
        SelectComboValue(RefreshRateComboBox, settings.Display.RefreshRateHz);
        SelectComboValue(QualityPresetComboBox, settings.Streaming.QualityPreset);
        SelectComboValue(InputModeComboBox, settings.Input.InputMode);

        PenPressureGammaSlider.Value = settings.Input.PenPressureGamma;
        PenPressureGammaValueText.Text = settings.Input.PenPressureGamma.ToString("0.00");
    }

    private static void SelectComboValue<T>(ComboBox comboBox, T value)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboOption<T> option && Equals(option.Value, value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void PenPressureGammaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PenPressureGammaValueText != null)
        {
            PenPressureGammaValueText.Text = e.NewValue.ToString("0.00");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = string.Empty;

        var updated = BuildSettingsFromForm(out var error);
        if (error != null)
        {
            StatusTextBlock.Text = error;
            return;
        }

        try
        {
            _store.Save(updated!, _caps);
        }
        catch (SettingsValidationException ex)
        {
            StatusTextBlock.Text = ex.Message;
            return;
        }

        var changedFields = DiffFields(_original, updated!);
        var applyType = SettingsChangeClassifier.ClassifyBatch(changedFields);

        // TODO(M4.2): once a live device connection exists, route this
        // through the real control socket: ApplyType.ReMode -> MODE_CHANGE
        // (+await MODE_ACK), ApplyType.Live -> CONFIG_UPDATE,
        // ApplyType.Reconnect -> tear down + re-handshake transport.
        System.Diagnostics.Debug.WriteLine(
            $"[DisplayBridge] Settings saved. Changed fields: {string.Join(", ", changedFields)}. " +
            $"Would apply via: {applyType} (no live connection yet — M1 in progress).");

        _original = updated!;
        DialogResult = true;
        Close();
    }

    private AppSettings? BuildSettingsFromForm(out string? error)
    {
        error = null;
        var settings = new AppSettings
        {
            SchemaVersion = _original.SchemaVersion,
            Connection = _original.Connection,
            Diagnostics = _original.Diagnostics
        };

        settings.Display.RefreshRateHz = (RefreshRateComboBox.SelectedItem as ComboOption<int>)?.Value ?? 120;

        settings.Streaming.QualityPreset = (QualityPresetComboBox.SelectedItem as ComboOption<QualityPreset>)?.Value
            ?? QualityPreset.High;
        settings.Streaming.Codec = _original.Streaming.Codec;
        settings.Streaming.FpsCap = _original.Streaming.FpsCap;
        settings.Streaming.AdaptiveBitrate = _original.Streaming.AdaptiveBitrate;
        settings.Streaming.LatencyPriority = _original.Streaming.LatencyPriority;

        settings.Input.InputMode = (InputModeComboBox.SelectedItem as ComboOption<InputMode>)?.Value ?? InputMode.Hybrid;
        settings.Input.PenPressureGamma = PenPressureGammaSlider.Value;
        settings.Input.TouchEnabled = _original.Input.TouchEnabled;
        settings.Input.PenPressureEnabled = _original.Input.PenPressureEnabled;

        return settings;
    }

    private static IReadOnlyList<SettingField> DiffFields(AppSettings before, AppSettings after)
    {
        var changed = new List<SettingField>();

        if (before.Display.RefreshRateHz != after.Display.RefreshRateHz)
            changed.Add(SettingField.DisplayRefreshRateHz);

        if (before.Streaming.Codec != after.Streaming.Codec)
            changed.Add(SettingField.StreamingCodec);
        if (before.Streaming.QualityPreset != after.Streaming.QualityPreset)
            changed.Add(SettingField.StreamingQualityPreset);
        if (before.Streaming.FpsCap != after.Streaming.FpsCap)
            changed.Add(SettingField.StreamingFpsCap);

        if (before.Input.InputMode != after.Input.InputMode)
            changed.Add(SettingField.InputMode);
        if (before.Input.PenPressureGamma != after.Input.PenPressureGamma)
            changed.Add(SettingField.InputPenPressureGamma);

        return changed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
