using System.Collections.Generic;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.Core.Input;

public enum InputRoute
{
    Cursor,
    Touch,
}

/// <summary>
/// Edge-zone width as a fraction of the virtual screen's shorter dimension.
/// Matches the "Edge-swipe zone" setting from CONTEXT-BRIEF §3.3 / RESEARCH-v1
/// (default 24px on the tablet's reported panel, expressed here as a fraction
/// of normalized 0-65535 coordinate space so it scales with any resolution).
/// </summary>
public sealed record EdgeZoneConfig(ushort WidthNormalized)
{
    public static EdgeZoneConfig Default => new(786); // ~24px / 2000px-equivalent virtual width, normalized to 65535
}

/// <summary>
/// PC-authoritative Hybrid Cursor/Touch classifier (RESEARCH-v1-windows-touch-gesture-mechanism.md §"Cập nhật thiết kế"):
///   - 1 active pointer, outside the edge-zone            -> Cursor route (SendInput MOUSEINPUT)
///   - 2+ concurrently active pointers                    -> Touch route (InjectTouchInput, multi-point)
///   - any pointer inside the edge-zone (even if 1 finger) -> Touch route (forced), so
///     Action Center / Task View edge-swipes are recognized by Windows Tầng 2-3.
///
/// Only the classification decision lives here; no Win32 calls are made in
/// this class, which keeps it unit-testable without a real desktop (see
/// InputModeClassifierTests.cs — CursorInjector/TouchInjector are mocked out
/// via the ICursorInjector/ITouchInjector interfaces).
/// </summary>
public sealed class InputModeClassifier
{
    private readonly EdgeZoneConfig _edgeZone;
    private readonly Dictionary<byte, TouchEventMessage> _activePointers = new();
    private InputRoute? _currentRoute;

    public InputModeClassifier(EdgeZoneConfig? edgeZone = null)
    {
        _edgeZone = edgeZone ?? EdgeZoneConfig.Default;
    }

    public IReadOnlyDictionary<byte, TouchEventMessage> ActivePointers => _activePointers;

    public InputRoute? CurrentRoute => _currentRoute;

    private bool IsInEdgeZone(ushort x, ushort y)
    {
        var w = _edgeZone.WidthNormalized;
        return x <= w || x >= 65535 - w || y <= w || y >= 65535 - w;
    }

    /// <summary>
    /// Feeds one TouchEvent sample into the classifier and returns the route
    /// it should be dispatched to. Tracks pointer up/down internally so
    /// "how many pointers are concurrently active" reflects state across
    /// calls, not just the current sample.
    /// </summary>
    public InputRoute Classify(TouchEventMessage sample)
    {
        switch ((TouchAction)sample.Action)
        {
            case TouchAction.Down:
                _activePointers[sample.PointerId] = sample;
                break;
            case TouchAction.Move:
                if (_activePointers.ContainsKey(sample.PointerId))
                {
                    _activePointers[sample.PointerId] = sample;
                }
                break;
            case TouchAction.Up:
            case TouchAction.Cancel:
                _activePointers.Remove(sample.PointerId);
                break;
        }

        var forcedByEdge = IsInEdgeZone(sample.X, sample.Y);
        var multiTouch = _activePointers.Count >= 2;
        var route = (forcedByEdge || multiTouch) ? InputRoute.Touch : InputRoute.Cursor;

        _currentRoute = route;
        return route;
    }

    /// <summary>
    /// True when the previous sample routed to Cursor and this one is about
    /// to route to Touch — the caller (dispatcher) MUST release any held
    /// cursor button (mouse-up) before switching, to avoid a stuck virtual
    /// mouse button (documented risk in RESEARCH-v1 §"Rủi ro / lưu ý").
    /// </summary>
    public bool NeedsCursorReleaseBeforeSwitch(InputRoute previousRoute, InputRoute nextRoute) =>
        previousRoute == InputRoute.Cursor && nextRoute == InputRoute.Touch;

    public void Reset()
    {
        _activePointers.Clear();
        _currentRoute = null;
    }
}

public enum TouchAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}
