using DisplayBridge.Core.Input;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers the fix for "touch on tablet -> cursor lands on the primary
/// screen" (docs/TASK-v1-tablet-display-tracker.md session 10). The root
/// cause was CursorInjector/TouchInjector mapping normalized coordinates
/// using GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN), which ALWAYS returns
/// the primary monitor's size regardless of which monitor is being touched.
/// A real desktop enumeration (EnumDisplayMonitors/EnumDisplayDevices) can't
/// be driven deterministically in CI without a real "VDD by MTT" device
/// attached, so this suite focuses on the part that IS fully testable
/// without a real desktop -- the RECT -> pixel coordinate mapping math
/// (<see cref="CoordinateMapper"/>) -- plus a light smoke test that the real
/// locator's enumeration path runs without throwing and degrades to a
/// sane (non-empty) fallback rect when no virtual display is present.
/// </summary>
public class VirtualMonitorLocatorTests
{
    [Fact]
    public void ToScreenPixels_ZeroNormalized_ReturnsRectTopLeft()
    {
        // Rect deliberately NOT at the virtual-desktop origin -- this is the
        // exact shape of the bug: a monitor positioned to the right of the
        // primary (e.g. after EnsureExtendTopology()), same as VDD by MTT's
        // real observed rect (2560,0)-(3360,600) from session 7.
        var rect = new Win32Interop.Rect { Left = 2560, Top = 0, Right = 3360, Bottom = 600 };

        var (x, y) = CoordinateMapper.ToScreenPixels(0, 0, rect);

        Assert.Equal(2560, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void ToScreenPixels_MaxNormalized_ReturnsNearRectBottomRight()
    {
        var rect = new Win32Interop.Rect { Left = 2560, Top = 0, Right = 3360, Bottom = 600 };

        var (x, y) = CoordinateMapper.ToScreenPixels(65535, 65535, rect);

        // 65535/65535.0 == 1.0 exactly, so this lands exactly on the far
        // edge; assert the tight bound rather than "close to" to catch an
        // off-by-one regression in the (int) truncation.
        Assert.Equal(3360, x);
        Assert.Equal(600, y);
    }

    [Fact]
    public void ToScreenPixels_MidpointNormalized_ReturnsRectCenter()
    {
        var rect = new Win32Interop.Rect { Left = 2560, Top = 0, Right = 3360, Bottom = 600 };

        var (x, y) = CoordinateMapper.ToScreenPixels(32768, 32768, rect);

        // Center of the touched tablet must land inside the tablet's own
        // rect, NOT inside [0, primaryWidth) x [0, primaryHeight) -- the bug
        // this fix addresses. A primary monitor at (0,0)-(2560,1600) would
        // never satisfy x >= 2560.
        Assert.InRange(x, rect.Left, rect.Right);
        Assert.InRange(y, rect.Top, rect.Bottom);
        Assert.True(x >= 2560, "regression: coordinate fell back inside the primary monitor's span");
    }

    [Fact]
    public void ToScreenPixels_DoesNotClampIntoPrimaryOnlySize_EvenWhenPrimaryIsLarger()
    {
        // Reproduces the reported bug shape directly: primary is LARGER than
        // the virtual display (a common real layout -- laptop panel
        // 2560x1600 vs. tablet-sized virtual display), so the old
        // GetSystemMetrics(SM_CXSCREEN)-based formula would always produce
        // coordinates that happen to fall inside the tablet's rect BY
        // COINCIDENCE only if the tablet rect is at the origin -- assert the
        // fix behaves correctly even when the virtual display sits to the
        // right of a larger primary.
        var primaryLikeSize = new Win32Interop.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1600 };
        var tabletRect = new Win32Interop.Rect { Left = 2560, Top = 0, Right = 5560, Bottom = 1920 };

        var (x, y) = CoordinateMapper.ToScreenPixels(16384, 16384, tabletRect); // ~25% into the tablet

        Assert.True(x > primaryLikeSize.Right, "regression: tapping 25% into the tablet must not resolve to a coordinate still inside the primary monitor");
        Assert.InRange(x, tabletRect.Left, tabletRect.Right);
        Assert.InRange(y, tabletRect.Top, tabletRect.Bottom);
    }

    [Fact]
    public void GetVirtualDisplayRect_RunsAgainstRealDesktop_ReturnsNonDegenerateRect()
    {
        // No fake VDD driver assumed in CI/dev machine -- this exercises the
        // real EnumDisplayMonitors/GetMonitorInfo/EnumDisplayDevices path and
        // must, at minimum, fall back to a real primary-monitor rect (never
        // throw, never return an empty/inverted rect).
        var locator = new VirtualMonitorLocator();

        var rect = locator.GetVirtualDisplayRect();

        Assert.True(rect.Right > rect.Left, "fallback rect must have positive width");
        Assert.True(rect.Bottom > rect.Top, "fallback rect must have positive height");
    }

    [Fact]
    public void GetVirtualDisplayRect_CachesResult_InvalidateForcesReEnumeration()
    {
        var locator = new VirtualMonitorLocator();

        var first = locator.GetVirtualDisplayRect();
        var second = locator.GetVirtualDisplayRect();
        Assert.Equal(first, second); // served from cache, no re-enumeration

        locator.Invalidate();
        var third = locator.GetVirtualDisplayRect(); // re-enumerated for real

        // On an unchanged desktop the re-enumerated result must match --
        // this proves Invalidate() doesn't corrupt state, only forces a
        // fresh lookup.
        Assert.Equal(first, third);
    }

    /// <summary>
    /// Minimal fake for CursorInjector/TouchInjector-level tests: proves the
    /// injectors consult the injected <see cref="IVirtualMonitorLocator"/>
    /// (constructor seam) rather than any hardcoded/primary-only source.
    /// </summary>
    private sealed class FakeVirtualMonitorLocator : IVirtualMonitorLocator
    {
        public Win32Interop.Rect Rect { get; set; }
        public int InvalidateCallCount { get; private set; }

        public Win32Interop.Rect GetVirtualDisplayRect() => Rect;

        public void Invalidate() => InvalidateCallCount++;
    }

    [Fact]
    public void CursorInjector_MoveTo_UsesInjectedLocatorRect_NotPrimaryOnlySize()
    {
        // This is the actual regression test for the reported bug: it calls
        // the real Win32Interop.SetCursorPos (same call path
        // CursorInjector.MoveTo uses in production) through a FAKE locator
        // whose rect is NOT at the virtual-desktop origin, then reads back
        // GetCursorPos and asserts the real cursor landed inside that rect
        // -- proving the fix end-to-end at the Win32 layer, not just the
        // pure-math CoordinateMapper layer above. Restores the original
        // cursor position afterward so running this test doesn't leave the
        // developer's mouse somewhere unexpected.
        Win32Interop.GetCursorPos(out var originalPos);
        try
        {
            var fakeRect = new Win32Interop.Rect { Left = 2560, Top = 0, Right = 3360, Bottom = 600 };
            var fake = new FakeVirtualMonitorLocator { Rect = fakeRect };
            var injector = new CursorInjector(monitorLocator: fake);

            injector.MoveTo(32768, 32768); // center of the fake tablet rect

            Win32Interop.GetCursorPos(out var afterMove);
            Assert.InRange(afterMove.X, fakeRect.Left, fakeRect.Right);
            Assert.InRange(afterMove.Y, fakeRect.Top, fakeRect.Bottom);
        }
        finally
        {
            Win32Interop.SetCursorPos(originalPos.X, originalPos.Y);
        }
    }
}
