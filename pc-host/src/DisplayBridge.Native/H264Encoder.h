// H264Encoder.h — M1.1 PoC encoder layer.
// Wraps a Media Foundation H.264/HEVC hardware encoder MFT (prefers NVENC,
// falls back to any HW encoder, then software) configured for low latency.
// Feeds D3D11 textures in, pulls NAL unit bytes out.
//
// Session 12 additions (see docs/TASK-v1-tablet-display-tracker.md session
// 12 for the "~20fps at 3000x1920" root-cause + fix write-up):
//   1) GPU BGRA->NV12 color conversion via ID3D11VideoDevice/VideoContext's
//      VideoProcessorBlt, replacing the naive CPU per-pixel loop that was
//      the actual bottleneck at native tablet resolution (kept as
//      ConvertBgraToNv12CpuFallback() for drivers too old to support it).
//   2) Optional HEVC output (VideoCodecType parameter to Init()) alongside
//      the existing H.264 path -- purely an additional codec CHOICE, not a
//      performance fix (HEVC encode is more CPU/GPU-expensive to produce
//      than H.264, even though the resulting bitstream is smaller -- see
//      Init()'s comment and StreamingCoordinator.cs wiring for why this
//      must never be presented to the user as "the lag fix").
#pragma once

#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <vector>
#include <cstdint>

using Microsoft::WRL::ComPtr;

enum class EncoderError : int
{
    Ok = 0,
    MFStartupFailed = 1,
    NoEncoderFound = 2,
    ActivateFailed = 3,
    SetInputTypeFailed = 4,
    SetOutputTypeFailed = 5,
    NotInitialized = 6,
    ConvertFailed = 7,
    ProcessInputFailed = 8,
    ProcessOutputFailed = 9,
    CreateSampleFailed = 10,
    AlreadyInitialized = 11,
};

// Which kind of encoder backend got selected — surfaced for logging /
// diagnostics.
enum class EncoderBackend : int
{
    Unknown = 0,
    HardwareNvenc = 1,
    HardwareOther = 2,
    Software = 3,
};

// Which output bitstream codec to negotiate. Numeric values match
// schema.yaml's Config.codec / ModeChange.codec wire values (0=H264,
// 1=HEVC) so callers (CaptureEncodeExports.cpp) can pass the wire byte
// straight through without a translation table.
enum class VideoCodecType : int
{
    H264 = 0,
    Hevc = 1,
};

class H264Encoder
{
public:
    H264Encoder() = default;
    ~H264Encoder();

    H264Encoder(const H264Encoder&) = delete;
    H264Encoder& operator=(const H264Encoder&) = delete;

    // width/height: frame dimensions. fps: target frame rate (numerator,
    // denominator=1). bitrateKbps: target average bitrate. codec: which
    // bitstream format to negotiate with the MFT (default H.264, unchanged
    // behavior for existing callers).
    EncoderError Init(ID3D11Device* device, UINT width, UINT height, UINT fps, UINT bitrateKbps, VideoCodecType codec = VideoCodecType::H264);

    // Submits one BGRA D3D11 texture for encoding. Converts to NV12 on the
    // GPU via VideoProcessorBlt when available (see InitGpuColorConversion),
    // falling back to a naive CPU per-pixel conversion only if the GPU path
    // could not be set up (old driver) or fails at runtime. Returns Ok if
    // the sample was accepted.
    EncoderError SubmitFrame(ID3D11Texture2D* texture, uint64_t ptsUs);

    // Attempts to pull one encoded NAL-unit buffer from the encoder.
    // Returns Ok + fills outData/outLen/outKeyframe if a sample was
    // available, or ProcessOutputFailed with outLen=0 if the encoder
    // needs more input first (MF_E_TRANSFORM_NEED_MORE_INPUT) — caller
    // should treat that as "try again after next SubmitFrame".
    EncoderError TryGetEncodedSample(std::vector<uint8_t>& outData, uint64_t& outPtsUs, bool& outKeyframe);

    void Shutdown();

    EncoderBackend Backend() const { return m_backend; }
    VideoCodecType Codec() const { return m_codec; }

    // True once GPU (VideoProcessorBlt) color conversion is actually being
    // used for SubmitFrame calls -- false means every frame is going
    // through the CPU per-pixel fallback (either the driver couldn't set
    // up the video processor, or a runtime Blt failure downgraded us
    // permanently for this session). Surfaced for logging/diagnostics so a
    // "why is this still slow" report can tell the two cases apart.
    bool UsingGpuColorConversion() const { return m_gpuColorConversionAvailable; }

    // True when the selected encoder MFT accepts D3D11/DXGI surfaces
    // directly (MF_SA_D3D11_AWARE), letting SubmitFrame skip the CPU
    // Map()/memcpy of the GPU-converted NV12 texture entirely (full
    // zero-copy GPU->GPU path). False means the GPU-converted NV12 texture
    // still gets mapped to CPU once (a couple of plain per-row memcpy's,
    // NOT the old per-pixel color-math loop) before ProcessInput.
    bool UsingZeroCopyD3D11Input() const { return m_mftD3D11Aware; }

private:
    EncoderError FindEncoderTransform();
    EncoderError ConfigureLowLatency();

    // --- Session 12: GPU color conversion (see header comment) ---
    // Best-effort setup, called once from Init() after the input type is
    // negotiated. Sets m_gpuColorConversionAvailable=true only if every
    // step (ID3D11VideoDevice/Context QI, CreateVideoProcessorEnumerator,
    // format support check, CreateVideoProcessor, NV12 output
    // texture+view) succeeds; any failure logs a clear Vietnamese warning
    // and leaves the CPU fallback path as the only option, per task
    // constraint "khong duoc bo code CPU fallback".
    void InitGpuColorConversion();

    // GPU path: VideoProcessorBlt(srcTexture BGRA) -> one of the ring's
    // NV12 textures, then either feeds that GPU texture directly to the
    // MFT (zero-copy, if m_mftD3D11Aware) or maps it to CPU with plain
    // memcpy's (no per-pixel math) and feeds it through the same
    // memory-buffer path as the CPU fallback. Returns
    // ConvertFailed/ProcessInputFailed-family errors on any step failure so
    // SubmitFrame can downgrade to ConvertBgraToNv12CpuFallback() for
    // subsequent frames.
    //
    // Ring-buffered NV12 output textures (kNv12RingSize of them, see .cpp):
    // found necessary by direct testing (session 12) -- a single shared
    // output texture reused every frame caused NativeSmokeTest to hang
    // after ~5s in zero-copy mode. Root cause: with m_mftD3D11Aware=true,
    // the raw D3D11 texture is handed directly into the async encoder MFT
    // (no CPU copy/synchronization point), so the NEXT frame's
    // VideoProcessorBlt can start overwriting that same texture while the
    // encoder's own GPU work on the PREVIOUS frame's contents is still
    // in flight -- a genuine write-while-in-use hazard, not just a
    // performance concern. Cycling through several distinct textures
    // (indexed by m_frameIndex) gives the encoder enough textures'
    // worth of headroom to finish consuming an older one before it comes
    // back around to be overwritten, the standard pattern for GPU
    // pipelines handing resources to an independently-scheduled consumer.
    EncoderError SubmitFrameGpu(ID3D11Texture2D* srcTexture, uint64_t ptsUs);
    EncoderError MapNv12TextureToCpu(ID3D11Texture2D* nv12Texture, std::vector<uint8_t>& nv12Out) const;

    // Naive CPU BGRA->NV12 conversion (original PoC implementation, kept as
    // the fallback per task constraint). A per-pixel loop -- this is the
    // actual root cause of the "~20fps at 3000x1920" lag report (12x the
    // pixel count of the 800x600 case this was originally validated at).
    EncoderError ConvertBgraToNv12CpuFallback(ID3D11Texture2D* texture, std::vector<uint8_t>& nv12Out, UINT& outStride);

    // Wraps an already-NV12 CPU buffer into an IMFSample and calls
    // ProcessInput -- shared tail end of both the CPU fallback path and the
    // GPU-converted-but-not-zero-copy path, so the sample/timestamp
    // plumbing isn't duplicated between them.
    EncoderError SubmitNv12MemoryBuffer(const std::vector<uint8_t>& nv12, uint64_t ptsUs);

    // --- R17 decision (2026-07-03, CONTEXT-BRIEF §"Risk register" R17) ---
    // SPS/PPS (H.264) or VPS+SPS+PPS (HEVC) are transmitted IN-BAND
    // (prepended as Annex-B NAL units directly inside the bitstream payload
    // sent over the video socket), NOT as out-of-band CSD via
    // MediaFormat/BUFFER_FLAG_CODEC_CONFIG. Rationale: simplest option that
    // needs zero extra protocol messages and matches the Android decoder
    // side (VideoDecoderActivity.kt configure() leaves csd-0/csd-1 unset
    // and lets MediaCodec parse the sequence headers out of the first
    // in-band NAL units it receives) for BOTH codecs.
    //
    // Most encoder MFTs (H.264 and HEVC alike) do NOT automatically re-emit
    // the sequence header before every IDR sample — typically only once at
    // stream start via MF_MT_MPEG_SEQUENCE_HEADER on the negotiated output
    // type. So this class captures that blob once after SetOutputType
    // succeeds (CaptureSequenceHeader()), and TryGetEncodedSample() prepends
    // it to the payload of every IDR/keyframe sample before returning it.
    // For HEVC, MF_MT_MPEG_SEQUENCE_HEADER is documented to contain the
    // full VPS+SPS+PPS set (not just SPS+PPS) -- verified by NAL-type
    // parsing in StartsWithKeyframeSequenceNal() below.
    EncoderError CaptureSequenceHeader();
    void PrependSequenceHeaderIfKeyframe(std::vector<uint8_t>& data, bool isKeyframe) const;

    // Returns true if `data` already begins (after an Annex-B start code)
    // with a sequence-header NAL for the ACTIVE codec: H.264 SPS (type 7)
    // or HEVC VPS (type 32, i.e. (byte0>>1)&0x3F == 32 per H.265 Annex-B NAL
    // header layout, which is 2 bytes wide unlike H.264's 1-byte header).
    // Used to avoid double-prepending when the MFT already emits the
    // sequence header in-band itself.
    bool StartsWithKeyframeSequenceNal(const std::vector<uint8_t>& data) const;

    std::vector<uint8_t> m_spsPps;   // captured once from MF_MT_MPEG_SEQUENCE_HEADER
    bool m_spsPpsCaptured = false;

    ComPtr<IMFTransform> m_transform;
    ComPtr<ID3D11Device> m_device;
    ComPtr<ID3D11DeviceContext> m_context;
    UINT m_width = 0;
    UINT m_height = 0;
    UINT m_fps = 60;
    UINT m_bitrateKbps = 8000;
    DWORD m_inputStreamId = 0;
    DWORD m_outputStreamId = 0;
    bool m_initialized = false;
    bool m_mfStarted = false;
    EncoderBackend m_backend = EncoderBackend::Unknown;
    VideoCodecType m_codec = VideoCodecType::H264;
    uint64_t m_frameIndex = 0;

    // --- Session 12 GPU color-conversion state ---
    bool m_gpuColorConversionAvailable = false;
    bool m_mftD3D11Aware = false;
    ComPtr<ID3D11VideoDevice> m_videoDevice;
    ComPtr<ID3D11VideoContext> m_videoContext;
    ComPtr<ID3D11VideoProcessorEnumerator> m_vpEnumerator;
    ComPtr<ID3D11VideoProcessor> m_videoProcessor;

    // Ring of NV12 output textures -- see SubmitFrameGpu declaration
    // comment above for why a single shared texture is unsafe once fed
    // directly (zero-copy) into an async encoder MFT.
    //
    // Bumped 4->8 (session 17): the session-16 fps-ceiling fix
    // (VideoStreamServer.cs) removed the artificial ~15ms per-null-frame
    // delay, so sustained throughput rose well past the ~55-64fps this
    // ring size was tuned at (session 12). Host started crashing with
    // AccessViolationException inside DisplayBridge_CaptureGetFrame at
    // the higher sustained rate -- consistent with the encoder falling
    // more than 4 frames behind under load and the ring wrapping onto a
    // texture GPU-side work was still reading (see the write-while-in-use
    // hazard explained above). 8 slots doubles the headroom before a
    // wraparound can catch an in-flight texture.
    static constexpr UINT kNv12RingSize = 8;
    ComPtr<ID3D11Texture2D> m_nv12GpuTextures[kNv12RingSize];
    ComPtr<ID3D11VideoProcessorOutputView> m_nv12OutputViews[kNv12RingSize];

    ComPtr<IMFDXGIDeviceManager> m_dxgiDeviceManager;
    UINT m_dxgiResetToken = 0;
};
