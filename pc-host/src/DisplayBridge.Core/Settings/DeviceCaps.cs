namespace DisplayBridge.Core.Settings;

/// <summary>
/// Native resolution of the currently connected device, as reported by the
/// CAPS handshake message (schema.yaml 0x01). Until a real CAPS message has
/// been received this session, callers should use <see cref="Placeholder"/>
/// so there is still *something* sane to resolve to. Replace with the real
/// value the moment the CAPS message is parsed (see StreamingCoordinator.
/// OnCapsReceived).
///
/// 2026-07-03 decision (session 7, RESEARCH-v2): resolution is always 100%
/// native — no more Max/75%/50%/Custom preset modes. <see cref="Resolve"/>
/// simply returns (NativeWidth, NativeHeight) unconditionally; kept as a
/// method (not inlined at call sites) so callers don't need to change when
/// reading the resolved size.
/// </summary>
public sealed record DeviceCaps(int NativeWidth, int NativeHeight)
{
    /// <summary>
    /// Placeholder native resolution used before any real device has
    /// connected. Replaced with the live value parsed from CapsMessage once
    /// the handshake path lands (see StreamingCoordinator.OnCapsReceived).
    /// </summary>
    public static DeviceCaps Placeholder { get; } = new(2560, 1600);

    /// <summary>Always 100% native resolution — no preset scaling anymore.</summary>
    public (int Width, int Height) Resolve() => (NativeWidth, NativeHeight);
}
