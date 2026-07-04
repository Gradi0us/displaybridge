// CaptureEncodeExports.cpp — M1.1 P/Invoke facade.
// Exposes flat C exports consumed by
// DisplayBridge.Core/Video/NativeCaptureEncoder.cs. Combines
// DesktopDuplicationCapture + H264Encoder into a single capture->encode
// pipeline pull loop. UNBUILT/UNVERIFIED (see H264Encoder.cpp header).
#include "DesktopDuplicationCapture.h"
#include "H264Encoder.h"
#include <memory>
#include <chrono>
#include <cstring>

namespace
{
    std::unique_ptr<DesktopDuplicationCapture> g_capture;
    std::unique_ptr<H264Encoder> g_encoder;
    bool g_pipelineInitialized = false;

    constexpr UINT kAcquireTimeoutMs = 500;
    constexpr UINT kDefaultFps = 60;
    constexpr UINT kDefaultBitrateKbps = 12000; // ~0.10 bpp @ 1920x1080x60 High preset

    uint64_t NowMicros()
    {
        using namespace std::chrono;
        return static_cast<uint64_t>(duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count());
    }
}

// NOTE: DisplayBridge_GetVersion() stays in DisplayBridgeNative.cpp (M0.1
// stub) unchanged per task constraints — not redefined here.

// codec: 0=H264, 1=HEVC (matches schema.yaml Config.codec / VideoCodecType
// in H264Encoder.h). Session 12: extracted from the old parameterless
// DisplayBridge_CaptureInit() so the PC side (StreamingCoordinator.cs) can
// pass through the codec actually negotiated with the connected tablet
// (previously ChooseCodec()'s result was only used for the wire CONFIG
// message and never reached the native encoder -- native always hardcoded
// H.264 regardless of what was "chosen").
// fps/bitrateKbps: session 14 bug fix. Until now this function silently
// discarded the ACTUAL negotiated Hz (60/90/120, from ChooseHz() on the C#
// side) and the resolution-appropriate bitrate (EstimateBitrateKbps()),
// always initializing the encoder with kDefaultFps=60/kDefaultBitrateKbps=
// 12000 regardless of what the tablet/user actually asked for. This meant:
//   (a) GOP size (CODECAPI_AVEncMPVGOPSize = m_fps*2, see H264Encoder.cpp
//       ConfigureLowLatency) and per-frame PTS duration were computed for
//       60fps even when the user picked 90/120Hz -- wrong GOP cadence and
//       incorrect presentation timestamps sent to the decoder.
//   (b) 12 Mbps is roughly 5-6x too low for 3000x1920 (PLAN-v2 §5 estimates
//       ~69 Mbps for native@120 HEVC) -- CBR rate control starved at this
//       bitrate has to either drop detail or drop frames to hit the target,
//       which is a direct, measurable contributor to the "very laggy" user
//       report independent of the GPU color-conversion fix (session 13).
// Callers now MUST pass the real negotiated values; 0 means "let the
// encoder pick its own default" (kept for the back-compat wrapper below).
extern "C" __declspec(dllexport) int DisplayBridge_CaptureInitWithCodecFpsBitrate(int codec, unsigned int fps, unsigned int bitrateKbps)
{
    if (g_pipelineInitialized)
    {
        return static_cast<int>(CaptureError::AlreadyInitialized);
    }

    g_capture = std::make_unique<DesktopDuplicationCapture>();
    CaptureError capErr = g_capture->Init();
    if (capErr != CaptureError::Ok)
    {
        g_capture.reset();
        return static_cast<int>(capErr);
    }

    // RESEARCH-v2 fix: use the ACTUAL duplicated output's resolution
    // (DesktopDuplicationCapture::Width/Height, populated from
    // DXGI_OUTPUT_DESC::DesktopCoordinates of the output we really
    // duplicated) instead of GetSystemMetrics(SM_CXSCREEN)/SM_CYSCREEN,
    // which ALWAYS reports the PRIMARY monitor's size regardless of which
    // output got duplicated. Using the primary's size here while duplicating
    // a different (virtual) output was the root cause of the encoder
    // producing frames squeezed to the wrong aspect ratio.
    UINT width = g_capture->Width();
    UINT height = g_capture->Height();
    if (width == 0 || height == 0)
    {
        width = static_cast<UINT>(GetSystemMetrics(SM_CXSCREEN));
        height = static_cast<UINT>(GetSystemMetrics(SM_CYSCREEN));
    }
    if (width == 0 || height == 0)
    {
        width = 1920;
        height = 1080;
    }

    const UINT effectiveFps = (fps > 0) ? fps : kDefaultFps;
    const UINT effectiveBitrateKbps = (bitrateKbps > 0) ? bitrateKbps : kDefaultBitrateKbps;

    const VideoCodecType codecType = (codec == static_cast<int>(VideoCodecType::Hevc)) ? VideoCodecType::Hevc : VideoCodecType::H264;

    g_encoder = std::make_unique<H264Encoder>();
    EncoderError encErr = g_encoder->Init(g_capture->Device(), width, height, effectiveFps, effectiveBitrateKbps, codecType);
    if (encErr != EncoderError::Ok)
    {
        g_encoder.reset();
        g_capture->Shutdown();
        g_capture.reset();
        // Offset encoder error codes so caller can distinguish (100+).
        return 100 + static_cast<int>(encErr);
    }

    g_pipelineInitialized = true;
    return 0;
}

// Back-compat: codec-only entry point (NativeSmokeTest, any caller not yet
// updated) -- keeps the old kDefaultFps/kDefaultBitrateKbps behavior.
extern "C" __declspec(dllexport) int DisplayBridge_CaptureInitWithCodec(int codec)
{
    return DisplayBridge_CaptureInitWithCodecFpsBitrate(codec, 0, 0);
}

// Back-compat entry point (NativeSmokeTest and any other caller that hasn't
// been updated to pass a codec) -- always negotiates H.264, identical to
// the pre-session-12 behavior.
extern "C" __declspec(dllexport) int DisplayBridge_CaptureInit()
{
    return DisplayBridge_CaptureInitWithCodec(static_cast<int>(VideoCodecType::H264));
}

extern "C" __declspec(dllexport) int DisplayBridge_CaptureGetFrame(
    uint8_t** outNalData,
    uint32_t* outNalLen,
    uint64_t* outPtsUs,
    uint8_t* outFlags)
{
    if (!g_pipelineInitialized || !g_capture || !g_encoder)
    {
        return static_cast<int>(CaptureError::NotInitialized);
    }
    if (!outNalData || !outNalLen || !outPtsUs || !outFlags)
    {
        return static_cast<int>(CaptureError::AcquireFrameFailed);
    }

    *outNalData = nullptr;
    *outNalLen = 0;
    *outPtsUs = 0;
    *outFlags = 0;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    DXGI_OUTDUPL_FRAME_INFO frameInfo{};
    CaptureError capErr = g_capture->AcquireFrame(kAcquireTimeoutMs, texture, frameInfo);
    if (capErr != CaptureError::Ok)
    {
        return static_cast<int>(capErr);
    }

    const uint64_t ptsUs = NowMicros();
    EncoderError submitErr = g_encoder->SubmitFrame(texture.Get(), ptsUs);
    g_capture->ReleaseFrame();

    if (submitErr != EncoderError::Ok)
    {
        return 100 + static_cast<int>(submitErr);
    }

    std::vector<uint8_t> nal;
    uint64_t samplePtsUs = 0;
    bool keyframe = false;
    EncoderError getErr = g_encoder->TryGetEncodedSample(nal, samplePtsUs, keyframe);
    if (getErr != EncoderError::Ok || nal.empty())
    {
        // Encoder needs more input (pipelined) — not a hard error for
        // the caller; report as timeout so the C# loop retries.
        return static_cast<int>(CaptureError::AcquireFrameTimeout);
    }

    uint8_t* buffer = static_cast<uint8_t*>(CoTaskMemAlloc(nal.size()));
    if (!buffer)
    {
        return static_cast<int>(CaptureError::AcquireFrameFailed);
    }
    memcpy(buffer, nal.data(), nal.size());

    *outNalData = buffer;
    *outNalLen = static_cast<uint32_t>(nal.size());
    *outPtsUs = samplePtsUs;
    *outFlags = keyframe ? 0x01 : 0x00;

    return 0;
}

// Returns 1 if Init() could not find the "VDD by MTT" virtual monitor and
// fell back to mirroring the primary (laptop) output -- i.e. the OLD,
// undesired behavior. Returns 0 if capturing the real virtual display, or
// if not yet initialized. C# side (NativeCaptureEncoder.cs) surfaces this
// as a loud log warning (see StreamingCoordinator.cs).
extern "C" __declspec(dllexport) int DisplayBridge_CaptureUsedFallbackPrimary()
{
    return (g_capture && g_capture->UsedFallbackPrimary()) ? 1 : 0;
}

// Returns the actual width/height being captured (0,0 if not initialized).
// Lets the C# side log/verify the real duplicated resolution instead of
// assuming it matches the primary monitor.
extern "C" __declspec(dllexport) void DisplayBridge_CaptureGetSize(uint32_t* outWidth, uint32_t* outHeight)
{
    if (outWidth) *outWidth = g_capture ? static_cast<uint32_t>(g_capture->Width()) : 0;
    if (outHeight) *outHeight = g_capture ? static_cast<uint32_t>(g_capture->Height()) : 0;
}

// Session 12 diagnostics: bit0=GPU color conversion active (VideoProcessorBlt
// instead of CPU per-pixel loop), bit1=zero-copy D3D11 input to the encoder
// MFT active (no CPU map at all), bit2=HEVC codec active (0=H264). Lets the
// C# side (StreamingCoordinator.cs) log real evidence of which fast path is
// actually running instead of just assuming Init() succeeded means GPU path
// is active.
extern "C" __declspec(dllexport) int DisplayBridge_CaptureGetEncoderDiagnostics()
{
    if (!g_encoder) return 0;
    int flags = 0;
    if (g_encoder->UsingGpuColorConversion()) flags |= 0x01;
    if (g_encoder->UsingZeroCopyD3D11Input()) flags |= 0x02;
    if (g_encoder->Codec() == VideoCodecType::Hevc) flags |= 0x04;
    return flags;
}

extern "C" __declspec(dllexport) void DisplayBridge_CaptureFreeFrame(uint8_t* nalData)
{
    if (nalData)
    {
        CoTaskMemFree(nalData);
    }
}

extern "C" __declspec(dllexport) void DisplayBridge_CaptureShutdown()
{
    if (g_encoder)
    {
        g_encoder->Shutdown();
        g_encoder.reset();
    }
    if (g_capture)
    {
        g_capture->Shutdown();
        g_capture.reset();
    }
    g_pipelineInitialized = false;
}
