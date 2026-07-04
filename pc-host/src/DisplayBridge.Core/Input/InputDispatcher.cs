using System.Collections.Generic;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.Core.Input;

/// <summary>
/// The actual InputModeClassifier -> CursorInjector/TouchInjector wiring
/// that was previously only exercised inline inside
/// InputModeClassifierTests.FakeInjectorTests (see that file's doc comment:
/// "a future dispatcher... would depend on [this] seam"). This is that
/// dispatcher, used by ControlSocketServer/StreamingCoordinator to turn
/// incoming TOUCH_EVENT/TOUCH_BATCH messages into real Win32 input.
/// </summary>
public sealed class InputDispatcher
{
    private readonly InputModeClassifier _classifier;
    private readonly ICursorInjector _cursor;
    private readonly ITouchInjector _touch;
    private InputRoute? _previousRoute;
    private bool _cursorButtonDown;

    public InputDispatcher(InputModeClassifier classifier, ICursorInjector cursor, ITouchInjector touch)
    {
        _classifier = classifier;
        _cursor = cursor;
        _touch = touch;
    }

    /// <summary>Dispatches one pointer sample, handling the Cursor<->Touch route switch and tap/drag button state.</summary>
    public void Dispatch(TouchEventMessage sample)
    {
        var route = _classifier.Classify(sample);

        // RESEARCH-v1 "Rủi ro / lưu ý": release any held mouse button before
        // switching Cursor -> Touch, or Windows is left with a stuck virtual
        // button (see InputModeClassifierTests.MidGestureSwitch_*).
        if (_previousRoute.HasValue &&
            _classifier.NeedsCursorReleaseBeforeSwitch(_previousRoute.Value, route) &&
            _cursorButtonDown)
        {
            _cursor.ButtonUp();
            _cursorButtonDown = false;
        }

        if (route == InputRoute.Cursor)
        {
            _cursor.MoveTo(sample.X, sample.Y);
            switch ((TouchAction)sample.Action)
            {
                case TouchAction.Down:
                    _cursor.ButtonDown();
                    _cursorButtonDown = true;
                    break;
                case TouchAction.Up:
                case TouchAction.Cancel:
                    if (_cursorButtonDown)
                    {
                        _cursor.ButtonUp();
                        _cursorButtonDown = false;
                    }
                    break;
                // Move: cursor already repositioned above, nothing else to do.
            }
        }
        else
        {
            _touch.Inject(new[] { sample });
        }

        _previousRoute = route;
    }

    /// <summary>Dispatches every sample in a TOUCH_BATCH message, in order.</summary>
    public void DispatchBatch(IReadOnlyList<TouchEventItem> events)
    {
        foreach (var e in events)
        {
            Dispatch(new TouchEventMessage(e.PointerId, e.Action, e.X, e.Y, e.Pressure, e.ToolType, e.TimestampUs));
        }
    }
}
