using System.Collections.Generic;
using DisplayBridge.Core.Input;
using DisplayBridge.Core.Protocol.Generated;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers InputModeClassifier's Cursor-vs-Touch routing decision (M3.3, see
/// RESEARCH-v1-windows-touch-gesture-mechanism.md "Cập nhật thiết kế" and
/// CONTEXT-BRIEF-v2 §4.2). No real Win32 call is made — CursorInjector and
/// TouchInjector are exercised separately via their fakes below, since the
/// classifier itself only decides routing and never calls user32.dll.
/// </summary>
public class InputModeClassifierTests
{
    private static TouchEventMessage Sample(byte pointerId, byte action, ushort x, ushort y) =>
        new(pointerId, action, x, y, 30000, 0, 0);

    [Fact]
    public void SingleFinger_AwayFromEdge_RoutesToCursor()
    {
        var classifier = new InputModeClassifier();
        var route = classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        Assert.Equal(InputRoute.Cursor, route);
    }

    [Fact]
    public void SingleFinger_Move_StaysOnCursor()
    {
        var classifier = new InputModeClassifier();
        classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        var route = classifier.Classify(Sample(0, (byte)TouchAction.Move, 33000, 33000));
        Assert.Equal(InputRoute.Cursor, route);
    }

    [Fact]
    public void TwoFingers_Concurrent_RoutesToTouch()
    {
        var classifier = new InputModeClassifier();
        classifier.Classify(Sample(0, (byte)TouchAction.Down, 20000, 20000));
        var route = classifier.Classify(Sample(1, (byte)TouchAction.Down, 40000, 40000));
        Assert.Equal(InputRoute.Touch, route);
    }

    [Fact]
    public void SecondFinger_Up_ReturnsToCursorRoute()
    {
        var classifier = new InputModeClassifier();
        classifier.Classify(Sample(0, (byte)TouchAction.Down, 20000, 20000));
        classifier.Classify(Sample(1, (byte)TouchAction.Down, 40000, 40000));
        // second finger lifts -> back to 1 active pointer -> Cursor
        var route = classifier.Classify(Sample(1, (byte)TouchAction.Up, 40000, 40000));
        Assert.Equal(InputRoute.Cursor, route);
        Assert.Single(classifier.ActivePointers);
    }

    [Theory]
    [InlineData(0, 32768)]      // left edge
    [InlineData(65535, 32768)]  // right edge
    [InlineData(32768, 0)]      // top edge
    [InlineData(32768, 65535)]  // bottom edge
    public void SingleFinger_InEdgeZone_ForcesTouchRoute(ushort x, ushort y)
    {
        var classifier = new InputModeClassifier();
        var route = classifier.Classify(Sample(0, (byte)TouchAction.Down, x, y));
        Assert.Equal(InputRoute.Touch, route);
    }

    [Fact]
    public void SingleFinger_CenterOfScreen_NotInEdgeZone()
    {
        var classifier = new InputModeClassifier();
        var route = classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        Assert.Equal(InputRoute.Cursor, route);
    }

    [Fact]
    public void CustomEdgeZone_WidensForcedTouchRegion()
    {
        // Edge zone covering the whole left half of the screen.
        var classifier = new InputModeClassifier(new EdgeZoneConfig(32768));
        var route = classifier.Classify(Sample(0, (byte)TouchAction.Down, 10000, 32768));
        Assert.Equal(InputRoute.Touch, route);
    }

    [Fact]
    public void Cancel_RemovesPointerFromActiveSet()
    {
        var classifier = new InputModeClassifier();
        classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        classifier.Classify(Sample(1, (byte)TouchAction.Down, 40000, 40000));
        classifier.Classify(Sample(0, (byte)TouchAction.Cancel, 32768, 32768));
        Assert.Single(classifier.ActivePointers);
    }

    [Fact]
    public void Reset_ClearsAllActivePointers()
    {
        var classifier = new InputModeClassifier();
        classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        classifier.Reset();
        Assert.Empty(classifier.ActivePointers);
        Assert.Null(classifier.CurrentRoute);
    }

    [Fact]
    public void NeedsCursorReleaseBeforeSwitch_TrueOnlyGoingCursorToTouch()
    {
        var classifier = new InputModeClassifier();
        Assert.True(classifier.NeedsCursorReleaseBeforeSwitch(InputRoute.Cursor, InputRoute.Touch));
        Assert.False(classifier.NeedsCursorReleaseBeforeSwitch(InputRoute.Touch, InputRoute.Cursor));
        Assert.False(classifier.NeedsCursorReleaseBeforeSwitch(InputRoute.Cursor, InputRoute.Cursor));
        Assert.False(classifier.NeedsCursorReleaseBeforeSwitch(InputRoute.Touch, InputRoute.Touch));
    }

    [Fact]
    public void MidGestureSwitch_OneFingerThenAddSecond_RequiresCursorRelease()
    {
        // Regression scenario from RESEARCH-v1 "Rủi ro / lưu ý": start with
        // 1 finger (Cursor), add a 2nd finger mid-gesture for a pinch -> the
        // dispatcher must release any held mouse button before the Touch
        // injection begins, or the OS is left with a stuck virtual button.
        var classifier = new InputModeClassifier();
        var firstRoute = classifier.Classify(Sample(0, (byte)TouchAction.Down, 32768, 32768));
        var secondRoute = classifier.Classify(Sample(1, (byte)TouchAction.Down, 40000, 40000));

        Assert.Equal(InputRoute.Cursor, firstRoute);
        Assert.Equal(InputRoute.Touch, secondRoute);
        Assert.True(classifier.NeedsCursorReleaseBeforeSwitch(firstRoute, secondRoute));
    }
}

/// <summary>
/// Fakes that record calls instead of touching Win32, used to verify
/// CursorInjector/TouchInjector call sites without a real desktop. These
/// exercise the ICursorInjector/ITouchInjector seam that a future
/// dispatcher (wiring InputModeClassifier -> CursorInjector/TouchInjector)
/// would depend on.
/// </summary>
public class FakeInjectorTests
{
    private sealed class FakeCursorInjector : ICursorInjector
    {
        public readonly List<string> Calls = new();
        public void MoveTo(ushort x, ushort y) => Calls.Add($"MoveTo({x},{y})");
        public void ButtonDown() => Calls.Add("ButtonDown");
        public void ButtonUp() => Calls.Add("ButtonUp");
    }

    private sealed class FakeTouchInjector : ITouchInjector
    {
        public int InitializeCalls;
        public readonly List<IReadOnlyList<TouchEventMessage>> InjectedBatches = new();
        public void InitializeIfNeeded(uint maxPointCount = 10) => InitializeCalls++;
        public void Inject(IReadOnlyList<TouchEventMessage> contacts) => InjectedBatches.Add(contacts);
    }

    [Fact]
    public void Dispatcher_RoutesCursorSample_ToCursorInjectorOnly()
    {
        var classifier = new InputModeClassifier();
        var cursor = new FakeCursorInjector();
        var touch = new FakeTouchInjector();

        var sample = new TouchEventMessage(0, (byte)TouchAction.Down, 32768, 32768, 30000, 0, 0);
        var route = classifier.Classify(sample);
        if (route == InputRoute.Cursor)
        {
            cursor.MoveTo(sample.X, sample.Y);
        }
        else
        {
            touch.Inject(new[] { sample });
        }

        Assert.Equal(InputRoute.Cursor, route);
        Assert.Single(cursor.Calls);
        Assert.Empty(touch.InjectedBatches);
    }

    [Fact]
    public void Dispatcher_RoutesMultiTouchSample_ToTouchInjectorOnly()
    {
        var classifier = new InputModeClassifier();
        var cursor = new FakeCursorInjector();
        var touch = new FakeTouchInjector();

        classifier.Classify(new TouchEventMessage(0, (byte)TouchAction.Down, 20000, 20000, 30000, 0, 0));
        var second = new TouchEventMessage(1, (byte)TouchAction.Down, 40000, 40000, 30000, 0, 0);
        var route = classifier.Classify(second);
        if (route == InputRoute.Cursor)
        {
            cursor.MoveTo(second.X, second.Y);
        }
        else
        {
            touch.Inject(new[] { second });
        }

        Assert.Equal(InputRoute.Touch, route);
        Assert.Empty(cursor.Calls);
        Assert.Single(touch.InjectedBatches);
    }
}
