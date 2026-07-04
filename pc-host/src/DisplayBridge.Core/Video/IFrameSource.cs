// IFrameSource.cs — abstraction so VideoStreamServer can be driven by
// either NativeCaptureEncoder (real DXGI+MF pipeline) or a fake/test
// source without needing the native DLL. Keeps the server unit-testable.
namespace DisplayBridge.Core.Video;

/// <summary>
/// Produces encoded video frames for <see cref="VideoStreamServer"/> to
/// write to a connected client. Implementations may block briefly and
/// return null when no frame is currently available (analogous to a
/// poll-with-timeout capture loop).
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Prepares the source for frame production (e.g. starts native
    /// capture/encode). Returns true on success.
    /// </summary>
    bool Init();

    /// <summary>
    /// Returns the next available encoded frame, or null if none is
    /// ready yet (caller should retry).
    /// </summary>
    EncodedFrame? GetNextFrame();

    /// <summary>
    /// Releases any resources held by the source.
    /// </summary>
    void Shutdown();
}
