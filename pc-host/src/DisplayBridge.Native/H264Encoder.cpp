// H264Encoder.cpp — M1.1 PoC encoder layer implementation.
//
// Session 12: GPU color conversion (VideoProcessorBlt) + optional HEVC
// output. See H264Encoder.h header comment + docs/TASK-v1-tablet-display-
// tracker.md session 12 for the full root-cause writeup ("~20fps at
// 3000x1920" was the CPU per-pixel BGRA->NV12 loop, not the encoder).
#include "H264Encoder.h"
#include <mferror.h>
#include <wmcodecdsp.h>
#include <string>
#include <algorithm>
#include <cstdio>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

H264Encoder::~H264Encoder()
{
    Shutdown();
}

EncoderError H264Encoder::FindEncoderTransform()
{
    IMFActivate** activateArray = nullptr;
    UINT32 activateCount = 0;

    MFT_REGISTER_TYPE_INFO outputType{};
    outputType.guidMajorType = MFMediaType_Video;
    outputType.guidSubtype = (m_codec == VideoCodecType::Hevc) ? MFVideoFormat_HEVC : MFVideoFormat_H264;

    // Ask for hardware-accelerated (D3D11-aware) transforms first.
    UINT32 flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;
    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        flags,
        nullptr,       // any input type
        &outputType,
        &activateArray,
        &activateCount);

    ComPtr<IMFActivate> chosen;
    EncoderBackend chosenBackend = EncoderBackend::Unknown;

    if (SUCCEEDED(hr) && activateCount > 0)
    {
        // Prefer an activate whose friendly name mentions NVIDIA/NVENC.
        for (UINT32 i = 0; i < activateCount; ++i)
        {
            WCHAR name[256] = {};
            UINT32 nameLen = 0;
            activateArray[i]->GetString(MFT_FRIENDLY_NAME_Attribute, name, ARRAYSIZE(name), &nameLen);
            std::wstring wname(name);
            std::transform(wname.begin(), wname.end(), wname.begin(), ::towlower);
            if (wname.find(L"nvidia") != std::wstring::npos || wname.find(L"nvenc") != std::wstring::npos)
            {
                chosen = activateArray[i];
                chosenBackend = EncoderBackend::HardwareNvenc;
                break;
            }
        }
        // Else: take the first hardware encoder found.
        if (!chosen)
        {
            chosen = activateArray[0];
            chosenBackend = EncoderBackend::HardwareOther;
        }
    }

    if (activateArray)
    {
        for (UINT32 i = 0; i < activateCount; ++i)
        {
            if (activateArray[i]) activateArray[i]->Release();
        }
        CoTaskMemFree(activateArray);
        activateArray = nullptr;
    }

    if (!chosen)
    {
        // Fall back to software encoder (drop the HARDWARE flag).
        activateCount = 0;
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            nullptr,
            &outputType,
            &activateArray,
            &activateCount);

        if (FAILED(hr) || activateCount == 0)
        {
            if (activateArray) CoTaskMemFree(activateArray);
            fprintf(stderr, "[DisplayBridge.Native] Khong tim thay encoder MFT nao cho codec=%d (0=H264,1=HEVC) -- may/driver co the khong ho tro HEVC encode.\n", static_cast<int>(m_codec));
            return EncoderError::NoEncoderFound;
        }

        chosen = activateArray[0];
        chosenBackend = EncoderBackend::Software;

        // Release every enumeration reference unconditionally — `chosen`
        // already holds its own AddRef from the ComPtr assignment above, so
        // releasing index 0 here too is correct (matches the hardware path).
        for (UINT32 i = 0; i < activateCount; ++i)
        {
            if (activateArray[i]) activateArray[i]->Release();
        }
        CoTaskMemFree(activateArray);
    }

    hr = chosen->ActivateObject(IID_PPV_ARGS(m_transform.GetAddressOf()));
    if (FAILED(hr) || !m_transform)
    {
        return EncoderError::ActivateFailed;
    }

    // Hardware encoder MFTs enumerated with MFT_ENUM_FLAG_HARDWARE are
    // typically implemented as "async MFTs" (IMFTransform + attribute
    // MF_TRANSFORM_ASYNC = TRUE). Per MSDN, an async MFT starts locked and
    // returns errors (or refuses to negotiate types) on every method until
    // the client acknowledges it understands the async contract by setting
    // MF_TRANSFORM_ASYNC_UNLOCK on the transform's attribute store. Found
    // by direct testing (session 6): without this, GetInputAvailableType /
    // SetInputType on the real NVENC MFT both fail with
    // MF_E_INVALIDMEDIATYPE (0xC00D6D77) even though the type is valid.
    ComPtr<IMFAttributes> transformAttrs;
    if (SUCCEEDED(m_transform->GetAttributes(transformAttrs.GetAddressOf())) && transformAttrs)
    {
        UINT32 isAsync = 0;
        if (SUCCEEDED(transformAttrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync)
        {
            transformAttrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
            fprintf(stderr, "[DisplayBridge.Native] MFT is async, unlocked via MF_TRANSFORM_ASYNC_UNLOCK\n");
        }
    }

    m_backend = chosenBackend;
    fprintf(stderr, "[DisplayBridge.Native] Encoder transform selected, backend=%d, codec=%d (0=H264,1=HEVC)\n", static_cast<int>(m_backend), static_cast<int>(m_codec));
    return EncoderError::Ok;
}

EncoderError H264Encoder::ConfigureLowLatency()
{
    ComPtr<ICodecAPI> codecApi;
    HRESULT hr = m_transform.As(&codecApi);
    if (SUCCEEDED(hr) && codecApi)
    {
        VARIANT v;
        VariantInit(&v);

        v.vt = VT_BOOL;
        v.boolVal = VARIANT_TRUE;
        codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &v);

        // Disable B-frames for low latency (0 B-frames between refs).
        v.vt = VT_UI4;
        v.ulVal = 0;
        codecApi->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

        // CBR rate control tends to behave better for live low-latency
        // streaming than the default VBR.
        v.vt = VT_UI4;
        v.ulVal = eAVEncCommonRateControlMode_CBR;
        codecApi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);

        // Long-ish GOP is fine since we can force IDR via MODE_CHANGE;
        // for PoC use ~2s GOP at target fps.
        v.vt = VT_UI4;
        v.ulVal = m_fps * 2;
        codecApi->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);

        VariantClear(&v);
    }

    return EncoderError::Ok;
}

EncoderError H264Encoder::Init(ID3D11Device* device, UINT width, UINT height, UINT fps, UINT bitrateKbps, VideoCodecType codec)
{
    if (m_initialized)
    {
        return EncoderError::AlreadyInitialized;
    }

    m_device = device;
    if (m_device)
    {
        m_device->GetImmediateContext(m_context.GetAddressOf());
    }
    m_width = width;
    m_height = height;
    m_fps = fps > 0 ? fps : 60;
    m_bitrateKbps = bitrateKbps > 0 ? bitrateKbps : 8000;
    m_codec = codec;

    if (m_codec == VideoCodecType::Hevc)
    {
        // Feature note (NOT a performance fix): HEVC typically costs MORE
        // encoder-side compute than H.264 for the same resolution/fps to
        // achieve a smaller output bitstream -- see H264Encoder.h header
        // comment. This is purely an additional codec CHOICE for the user;
        // the ~20fps lag fix is the GPU color conversion below, independent
        // of which codec is chosen here.
        fprintf(stderr, "[DisplayBridge.Native] HEVC duoc chon lam codec dau ra -- LUU Y: HEVC encode thuong TON CPU/GPU hon H.264 de nen (du anh nhe hon khi truyen), day la lua chon THEM, KHONG PHAI cach fix lag chinh (fix lag la GPU color conversion, doc lap voi codec).\n");
    }

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr))
    {
        return EncoderError::MFStartupFailed;
    }
    m_mfStarted = true;

    EncoderError err = FindEncoderTransform();
    if (err != EncoderError::Ok)
    {
        return err;
    }

    // --- Configure output type (H.264/HEVC) first; many encoder MFTs
    // require the output type to be set before the input type. ---
    ComPtr<IMFMediaType> outputType;
    hr = MFCreateMediaType(outputType.GetAddressOf());
    if (FAILED(hr)) return EncoderError::SetOutputTypeFailed;

    outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outputType->SetGUID(MF_MT_SUBTYPE, m_codec == VideoCodecType::Hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264);
    outputType->SetUINT32(MF_MT_AVG_BITRATE, m_bitrateKbps * 1000);
    outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(outputType.Get(), MF_MT_FRAME_SIZE, m_width, m_height);
    MFSetAttributeRatio(outputType.Get(), MF_MT_FRAME_RATE, m_fps, 1);
    MFSetAttributeRatio(outputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    outputType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, FALSE);
    if (m_codec == VideoCodecType::H264)
    {
        // HEVC MFTs generally don't expose this H.264-specific CODECAPI key;
        // setting it unconditionally on an HEVC transform risks a spurious
        // failure on drivers that validate unknown keys strictly.
        outputType->SetUINT32(CODECAPI_AVEncH264CABACEnable, TRUE);
    }

    hr = m_transform->SetOutputType(m_outputStreamId, outputType.Get(), 0);
    if (FAILED(hr))
    {
        fprintf(stderr, "[DisplayBridge.Native] SetOutputType failed, hr=0x%08lX, backend=%d, codec=%d\n", hr, static_cast<int>(m_backend), static_cast<int>(m_codec));
        return EncoderError::SetOutputTypeFailed;
    }

    // --- Configure input type (NV12, uncompressed). ---
    // NVENC's MFT (and several other HW encoder MFTs) reject an input type
    // built from scratch with MF_E_INVALIDMEDIATYPE (0xC00D6D77) unless it
    // is derived from one of the transform's own GetInputAvailableType()
    // candidates -- constructing a fresh IMFMediaType with only the
    // "obviously required" attributes set is not enough; the MFT expects
    // its full default attribute set (extra internal/vendor attributes)
    // with only the negotiable ones (subtype/size/frame rate) overridden.
    // Discovered by direct testing against the real NVENC MFT (session 6);
    // this is the first time this code path has ever executed for real.
    ComPtr<IMFMediaType> inputType;
    ComPtr<IMFMediaType> candidate;
    DWORD candidateCount = 0;
    for (DWORD typeIndex = 0; ; ++typeIndex)
    {
        candidate.Reset();
        hr = m_transform->GetInputAvailableType(m_inputStreamId, typeIndex, candidate.GetAddressOf());
        if (hr == MF_E_NO_MORE_TYPES || FAILED(hr))
        {
            fprintf(stderr, "[DisplayBridge.Native] GetInputAvailableType enumeration ended at index=%lu, hr=0x%08lX\n", typeIndex, hr);
            break;
        }
        ++candidateCount;

        GUID subtype{};
        HRESULT subHr = candidate->GetGUID(MF_MT_SUBTYPE, &subtype);
        if (SUCCEEDED(subHr))
        {
            fprintf(stderr, "[DisplayBridge.Native] input candidate[%lu] subtype={%08lX-...}\n", typeIndex, subtype.Data1);
        }
        if (SUCCEEDED(subHr) && subtype == MFVideoFormat_NV12)
        {
            inputType = candidate;
            break;
        }
    }
    fprintf(stderr, "[DisplayBridge.Native] input candidates enumerated=%lu, NV12 found=%d\n", candidateCount, inputType != nullptr);

    if (!inputType)
    {
        // No enumerated NV12 candidate (unexpected for a real encoder MFT,
        // but keep the old from-scratch construction as a last resort so
        // software/other backends that DO accept it still work).
        hr = MFCreateMediaType(inputType.GetAddressOf());
        if (FAILED(hr)) return EncoderError::SetInputTypeFailed;
        inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    }

    inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, m_width, m_height);
    MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, m_fps, 1);
    MFSetAttributeRatio(inputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    hr = m_transform->SetInputType(m_inputStreamId, inputType.Get(), 0);
    if (FAILED(hr))
    {
        fprintf(stderr, "[DisplayBridge.Native] SetInputType failed, hr=0x%08lX, backend=%d, %ux%u@%u\n", hr, static_cast<int>(m_backend), m_width, m_height, m_fps);
        return EncoderError::SetInputTypeFailed;
    }

    EncoderError cfgErr = ConfigureLowLatency();
    if (cfgErr != EncoderError::Ok)
    {
        return cfgErr;
    }

    // Session 12: set up GPU color conversion now that the transform (and
    // its attributes, needed for the MF_SA_D3D11_AWARE check) exists.
    // Best-effort -- InitGpuColorConversion() never fails Init() itself,
    // it only sets m_gpuColorConversionAvailable and logs clearly.
    InitGpuColorConversion();

    // Notify the MFT that streaming is about to begin.
    m_transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    m_transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    // R17: grab SPS/PPS (or VPS+SPS+PPS for HEVC) once now (see
    // H264Encoder.h comment) so it's ready to prepend to the very first IDR
    // sample. Non-fatal if it's not available yet at this point for a given
    // driver — TryGetEncodedSample will fall back to
    // StartsWithKeyframeSequenceNal() and simply skip prepending if the MFT
    // already put it in-band itself.
    CaptureSequenceHeader();

    m_initialized = true;
    return EncoderError::Ok;
}

void H264Encoder::InitGpuColorConversion()
{
    m_gpuColorConversionAvailable = false;
    m_mftD3D11Aware = false;

    if (!m_device || !m_context)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: khong co D3D11 device -- dung CPU fallback (cham hon).\n");
        return;
    }

    HRESULT hr = m_device.As(&m_videoDevice);
    if (FAILED(hr) || !m_videoDevice)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion KHONG kha dung (ID3D11VideoDevice QueryInterface hr=0x%08lX) -- driver GPU co the qua cu, dung CPU fallback (cham hon).\n", hr);
        return;
    }
    hr = m_context.As(&m_videoContext);
    if (FAILED(hr) || !m_videoContext)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion KHONG kha dung (ID3D11VideoContext QueryInterface hr=0x%08lX) -- dung CPU fallback (cham hon).\n", hr);
        return;
    }

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC contentDesc{};
    contentDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    contentDesc.InputFrameRate.Numerator = m_fps;
    contentDesc.InputFrameRate.Denominator = 1;
    contentDesc.InputWidth = m_width;
    contentDesc.InputHeight = m_height;
    contentDesc.OutputFrameRate = contentDesc.InputFrameRate;
    contentDesc.OutputWidth = m_width;
    contentDesc.OutputHeight = m_height;
    contentDesc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    hr = m_videoDevice->CreateVideoProcessorEnumerator(&contentDesc, m_vpEnumerator.GetAddressOf());
    if (FAILED(hr) || !m_vpEnumerator)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: CreateVideoProcessorEnumerator that bai hr=0x%08lX -- dung CPU fallback (cham hon).\n", hr);
        return;
    }

    // Sanity-check the driver actually supports this exact conversion
    // before committing to the GPU path -- not guaranteed on every driver
    // per D3D11 Video Processor docs.
    UINT bgraSupport = 0, nv12Support = 0;
    m_vpEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_B8G8R8A8_UNORM, &bgraSupport);
    m_vpEnumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &nv12Support);
    if (!(bgraSupport & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) ||
        !(nv12Support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT))
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: driver KHONG ho tro BGRA8->NV12 qua VideoProcessorBlt (bgraSupport=0x%x nv12Support=0x%x) -- dung CPU fallback (cham hon).\n", bgraSupport, nv12Support);
        return;
    }

    hr = m_videoDevice->CreateVideoProcessor(m_vpEnumerator.Get(), 0, m_videoProcessor.GetAddressOf());
    if (FAILED(hr) || !m_videoProcessor)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: CreateVideoProcessor that bai hr=0x%08lX -- dung CPU fallback (cham hon).\n", hr);
        return;
    }

    // Ring of NV12 output textures (see H264Encoder.h SubmitFrameGpu
    // comment): each gets its own texture + output view up front so
    // SubmitFrameGpu can just index into them per frame with no further
    // allocation on the hot path.
    for (UINT i = 0; i < kNv12RingSize; ++i)
    {
        D3D11_TEXTURE2D_DESC nv12Desc{};
        nv12Desc.Width = m_width;
        nv12Desc.Height = m_height;
        nv12Desc.MipLevels = 1;
        nv12Desc.ArraySize = 1;
        nv12Desc.Format = DXGI_FORMAT_NV12;
        nv12Desc.SampleDesc.Count = 1;
        nv12Desc.Usage = D3D11_USAGE_DEFAULT;
        nv12Desc.BindFlags = D3D11_BIND_RENDER_TARGET; // required for use as a VideoProcessorOutputView target
        hr = m_device->CreateTexture2D(&nv12Desc, nullptr, m_nv12GpuTextures[i].GetAddressOf());
        if (FAILED(hr) || !m_nv12GpuTextures[i])
        {
            fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: tao NV12 output texture #%u that bai hr=0x%08lX -- dung CPU fallback (cham hon).\n", i, hr);
            return;
        }

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc{};
        outDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        outDesc.Texture2D.MipSlice = 0;
        hr = m_videoDevice->CreateVideoProcessorOutputView(m_nv12GpuTextures[i].Get(), m_vpEnumerator.Get(), &outDesc, m_nv12OutputViews[i].GetAddressOf());
        if (FAILED(hr) || !m_nv12OutputViews[i])
        {
            fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: CreateVideoProcessorOutputView #%u that bai hr=0x%08lX -- dung CPU fallback (cham hon).\n", i, hr);
            return;
        }
    }

    m_gpuColorConversionAvailable = true;
    fprintf(stderr, "[DisplayBridge.Native] GPU color conversion (ID3D11VideoDevice/VideoProcessorBlt) SAN SANG -- BGRA->NV12 chay tren GPU, khong con vong lap CPU per-pixel (day la fix chinh cho lag o resolution cao).\n");

    // Best-effort: check whether the chosen encoder MFT can accept D3D11
    // surfaces directly (zero-copy), so SubmitFrame can skip the CPU
    // Map()/memcpy of the GPU-converted NV12 texture entirely. Per the task
    // requirement: check the transform's real attribute store, don't guess
    // from vendor name.
    if (m_transform)
    {
        ComPtr<IMFAttributes> transformAttrs;
        if (SUCCEEDED(m_transform->GetAttributes(transformAttrs.GetAddressOf())) && transformAttrs)
        {
            UINT32 d3d11Aware = 0;
            if (SUCCEEDED(transformAttrs->GetUINT32(MF_SA_D3D11_AWARE, &d3d11Aware)) && d3d11Aware)
            {
                HRESULT dmHr = MFCreateDXGIDeviceManager(&m_dxgiResetToken, m_dxgiDeviceManager.GetAddressOf());
                if (SUCCEEDED(dmHr) && m_dxgiDeviceManager)
                {
                    dmHr = m_dxgiDeviceManager->ResetDevice(m_device.Get(), m_dxgiResetToken);
                }
                if (SUCCEEDED(dmHr) && m_dxgiDeviceManager)
                {
                    dmHr = m_transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(m_dxgiDeviceManager.Get()));
                }
                if (SUCCEEDED(dmHr))
                {
                    m_mftD3D11Aware = true;
                    fprintf(stderr, "[DisplayBridge.Native] Encoder MFT ho tro D3D11 surface truc tiep (MF_SA_D3D11_AWARE) -- dung duong zero-copy GPU->GPU (khong Map() CPU).\n");
                }
                else
                {
                    fprintf(stderr, "[DisplayBridge.Native] Encoder MFT khai bao MF_SA_D3D11_AWARE nhung SET_D3D_MANAGER that bai hr=0x%08lX -- dung duong GPU-convert + 1 lan map CPU (van nhanh hon nhieu so voi CPU thuan).\n", dmHr);
                }
            }
            else
            {
                fprintf(stderr, "[DisplayBridge.Native] Encoder MFT khong khai bao MF_SA_D3D11_AWARE -- dung duong GPU-convert + 1 lan map CPU (chi memcpy theo hang, khong tinh toan mau per-pixel nua).\n");
            }
        }
    }
}

EncoderError H264Encoder::CaptureSequenceHeader()
{
    if (m_spsPpsCaptured || !m_transform) return EncoderError::Ok;

    ComPtr<IMFMediaType> currentOutputType;
    HRESULT hr = m_transform->GetOutputCurrentType(m_outputStreamId, currentOutputType.GetAddressOf());
    if (FAILED(hr) || !currentOutputType) return EncoderError::Ok; // best-effort, not fatal

    UINT32 blobSize = 0;
    hr = currentOutputType->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &blobSize);
    if (FAILED(hr) || blobSize == 0) return EncoderError::Ok;

    m_spsPps.resize(blobSize);
    hr = currentOutputType->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, m_spsPps.data(), blobSize, nullptr);
    if (FAILED(hr))
    {
        m_spsPps.clear();
        return EncoderError::Ok;
    }

    m_spsPpsCaptured = true;
    return EncoderError::Ok;
}

bool H264Encoder::StartsWithKeyframeSequenceNal(const std::vector<uint8_t>& data) const
{
    size_t offset = 0;
    if (data.size() >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) offset = 4;
    else if (data.size() >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1) offset = 3;
    else return false;

    if (offset >= data.size()) return false;

    if (m_codec == VideoCodecType::Hevc)
    {
        // H.265 Annex-B NAL header is 2 bytes: forbidden_zero_bit(1) +
        // nal_unit_type(6) + layer_id(6) + tid(3), packed into 16 bits.
        // nal_unit_type = (byte0 >> 1) & 0x3F. VPS=32 -- if a keyframe
        // sequence starts with VPS, the MFT already put VPS+SPS+PPS
        // in-band itself.
        const uint8_t nalType = (data[offset] >> 1) & 0x3F;
        return nalType == 32; // VPS
    }

    // H.264: 1-byte NAL header, nal_unit_type = low 5 bits. SPS=7.
    const uint8_t nalType = data[offset] & 0x1F;
    return nalType == 7; // SPS
}

void H264Encoder::PrependSequenceHeaderIfKeyframe(std::vector<uint8_t>& data, bool isKeyframe) const
{
    if (!isKeyframe || m_spsPps.empty()) return;
    if (StartsWithKeyframeSequenceNal(data)) return; // MFT already put it in-band, avoid duplicate

    std::vector<uint8_t> merged;
    merged.reserve(m_spsPps.size() + data.size());
    merged.insert(merged.end(), m_spsPps.begin(), m_spsPps.end());
    merged.insert(merged.end(), data.begin(), data.end());
    data.swap(merged);
}

// Naive CPU BGRA->NV12 conversion -- the ORIGINAL PoC implementation, kept
// verbatim as the fallback path when GPU color conversion isn't available
// (old driver / VideoProcessorBlt setup failed). This per-pixel loop is the
// confirmed root cause of the "~20fps at 3000x1920" lag report (12x the
// pixel count of the 800x600 case this was validated at in session 6) --
// see InitGpuColorConversion()/SubmitFrameGpu() above for the real fix.
EncoderError H264Encoder::ConvertBgraToNv12CpuFallback(ID3D11Texture2D* texture, std::vector<uint8_t>& nv12Out, UINT& outStride)
{
    if (!m_device || !m_context) return EncoderError::ConvertFailed;

    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);

    D3D11_TEXTURE2D_DESC stagingDesc = desc;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;

    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = m_device->CreateTexture2D(&stagingDesc, nullptr, staging.GetAddressOf());
    if (FAILED(hr)) return EncoderError::ConvertFailed;

    m_context->CopyResource(staging.Get(), texture);

    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = m_context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) return EncoderError::ConvertFailed;

    // Session 17 bug fix: desc.Width/Height come straight from the
    // captured D3D11 texture (DXGI Desktop Duplication's actual output
    // rectangle), which is NOT guaranteed even -- reproduced via
    // NativeSmokeTest with a 1013x1011 capture (odd on both axes). NV12's
    // 4:2:0 chroma subsampling assumes even dimensions; the UV-plane
    // indexing below (`(y/2)*width + x`, using the FULL odd width as row
    // stride) walks past the end of nv12Out's width*height/2-sized
    // allocation once width or height is odd, corrupting the heap --
    // manifests later as an unrelated-looking AccessViolationException on
    // a subsequent native call. Round down to even here so the buffer
    // size and the UV loop bounds agree; losing the last row/column of a
    // capture is imperceptible.
    const UINT width = desc.Width & ~1u;
    const UINT height = desc.Height & ~1u;
    nv12Out.resize(static_cast<size_t>(width) * height * 3 / 2);
    outStride = width;

    uint8_t* yPlane = nv12Out.data();
    uint8_t* uvPlane = nv12Out.data() + static_cast<size_t>(width) * height;

    const uint8_t* src = static_cast<const uint8_t*>(mapped.pData);

    // BGRA8 -> Y plane (BT.601 full-range approximation) + subsampled UV.
    for (UINT y = 0; y < height; ++y)
    {
        const uint8_t* row = src + static_cast<size_t>(y) * mapped.RowPitch;
        for (UINT x = 0; x < width; ++x)
        {
            const uint8_t b = row[x * 4 + 0];
            const uint8_t g = row[x * 4 + 1];
            const uint8_t r = row[x * 4 + 2];
            const int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            yPlane[static_cast<size_t>(y) * width + x] = static_cast<uint8_t>(std::clamp(yVal, 0, 255));

            if ((y % 2 == 0) && (x % 2 == 0))
            {
                const int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                const int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                const size_t uvIndex = (static_cast<size_t>(y) / 2) * width + x;
                uvPlane[uvIndex] = static_cast<uint8_t>(std::clamp(uVal, 0, 255));
                uvPlane[uvIndex + 1] = static_cast<uint8_t>(std::clamp(vVal, 0, 255));
            }
        }
    }

    m_context->Unmap(staging.Get(), 0);
    return EncoderError::Ok;
}

EncoderError H264Encoder::MapNv12TextureToCpu(ID3D11Texture2D* nv12Texture, std::vector<uint8_t>& nv12Out) const
{
    D3D11_TEXTURE2D_DESC stagingDesc{};
    stagingDesc.Width = m_width;
    stagingDesc.Height = m_height;
    stagingDesc.MipLevels = 1;
    stagingDesc.ArraySize = 1;
    stagingDesc.Format = DXGI_FORMAT_NV12;
    stagingDesc.SampleDesc.Count = 1;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = m_device->CreateTexture2D(&stagingDesc, nullptr, staging.GetAddressOf());
    if (FAILED(hr)) return EncoderError::ConvertFailed;

    m_context->CopyResource(staging.Get(), nv12Texture);

    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = m_context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) return EncoderError::ConvertFailed;

    nv12Out.resize(static_cast<size_t>(m_width) * m_height * 3 / 2);
    uint8_t* yPlane = nv12Out.data();
    uint8_t* uvPlane = nv12Out.data() + static_cast<size_t>(m_width) * m_height;
    const uint8_t* src = static_cast<const uint8_t*>(mapped.pData);

    // Y plane: m_height rows of m_width bytes each -- plain memcpy per row
    // respecting the driver's RowPitch, NOT a per-pixel color-math loop
    // (the GPU already did the BGRA->NV12 conversion via VideoProcessorBlt).
    for (UINT y = 0; y < m_height; ++y)
    {
        memcpy(yPlane + static_cast<size_t>(y) * m_width, src + static_cast<size_t>(y) * mapped.RowPitch, m_width);
    }
    // UV plane (interleaved, subsampled 2x2): NV12's documented planar
    // layout places the UV plane immediately after all Y rows within the
    // SAME mapped resource, using the same RowPitch.
    const uint8_t* uvSrc = src + static_cast<size_t>(mapped.RowPitch) * m_height;
    for (UINT y = 0; y < m_height / 2; ++y)
    {
        memcpy(uvPlane + static_cast<size_t>(y) * m_width, uvSrc + static_cast<size_t>(y) * mapped.RowPitch, m_width);
    }

    m_context->Unmap(staging.Get(), 0);
    return EncoderError::Ok;
}

EncoderError H264Encoder::SubmitNv12MemoryBuffer(const std::vector<uint8_t>& nv12, uint64_t ptsUs)
{
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(static_cast<DWORD>(nv12.size()), buffer.GetAddressOf());
    if (FAILED(hr)) return EncoderError::CreateSampleFailed;

    BYTE* bufPtr = nullptr;
    hr = buffer->Lock(&bufPtr, nullptr, nullptr);
    if (FAILED(hr)) return EncoderError::CreateSampleFailed;
    memcpy(bufPtr, nv12.data(), nv12.size());
    buffer->Unlock();
    buffer->SetCurrentLength(static_cast<DWORD>(nv12.size()));

    ComPtr<IMFSample> sample;
    hr = MFCreateSample(sample.GetAddressOf());
    if (FAILED(hr)) return EncoderError::CreateSampleFailed;
    sample->AddBuffer(buffer.Get());

    // MF timestamps are in 100ns units.
    const LONGLONG mfPts = static_cast<LONGLONG>(ptsUs) * 10;
    const LONGLONG mfDuration = static_cast<LONGLONG>(10000000ULL / (m_fps > 0 ? m_fps : 60));
    sample->SetSampleTime(mfPts);
    sample->SetSampleDuration(mfDuration);

    hr = m_transform->ProcessInput(m_inputStreamId, sample.Get(), 0);
    if (FAILED(hr) && hr != MF_E_NOTACCEPTING)
    {
        return EncoderError::ProcessInputFailed;
    }

    ++m_frameIndex;
    return EncoderError::Ok;
}

EncoderError H264Encoder::SubmitFrameGpu(ID3D11Texture2D* srcTexture, uint64_t ptsUs)
{
    // Ring-buffered output texture (see H264Encoder.h SubmitFrameGpu
    // comment) -- cycling avoids overwriting a texture the async encoder
    // MFT may still be reading from a previous frame (zero-copy path).
    const UINT ringIndex = static_cast<UINT>(m_frameIndex % kNv12RingSize);
    ID3D11Texture2D* nv12Texture = m_nv12GpuTextures[ringIndex].Get();
    ID3D11VideoProcessorOutputView* nv12OutputView = m_nv12OutputViews[ringIndex].Get();

    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc{};
    inDesc.FourCC = 0;
    inDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    inDesc.Texture2D.MipSlice = 0;
    inDesc.Texture2D.ArraySlice = 0;

    ComPtr<ID3D11VideoProcessorInputView> inputView;
    HRESULT hr = m_videoDevice->CreateVideoProcessorInputView(srcTexture, m_vpEnumerator.Get(), &inDesc, inputView.GetAddressOf());
    if (FAILED(hr) || !inputView)
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: CreateVideoProcessorInputView that bai hr=0x%08lX o runtime.\n", hr);
        return EncoderError::ConvertFailed;
    }

    D3D11_VIDEO_PROCESSOR_STREAM stream{};
    stream.Enable = TRUE;
    stream.pInputSurface = inputView.Get();

    hr = m_videoContext->VideoProcessorBlt(m_videoProcessor.Get(), nv12OutputView, 0, 1, &stream);
    if (FAILED(hr))
    {
        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion: VideoProcessorBlt that bai hr=0x%08lX o runtime.\n", hr);
        return EncoderError::ConvertFailed;
    }

    if (m_mftD3D11Aware)
    {
        ComPtr<IMFMediaBuffer> buffer;
        hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12Texture, 0, FALSE, buffer.GetAddressOf());
        if (FAILED(hr) || !buffer)
        {
            fprintf(stderr, "[DisplayBridge.Native] Zero-copy: MFCreateDXGISurfaceBuffer that bai hr=0x%08lX -- chuyen ve map CPU cho frame nay.\n", hr);
        }
        else
        {
            ComPtr<IMFSample> sample;
            hr = MFCreateSample(sample.GetAddressOf());
            if (SUCCEEDED(hr))
            {
                sample->AddBuffer(buffer.Get());
                const LONGLONG mfPts = static_cast<LONGLONG>(ptsUs) * 10;
                const LONGLONG mfDuration = static_cast<LONGLONG>(10000000ULL / (m_fps > 0 ? m_fps : 60));
                sample->SetSampleTime(mfPts);
                sample->SetSampleDuration(mfDuration);

                hr = m_transform->ProcessInput(m_inputStreamId, sample.Get(), 0);
                if (SUCCEEDED(hr) || hr == MF_E_NOTACCEPTING)
                {
                    ++m_frameIndex;
                    return EncoderError::Ok;
                }
                fprintf(stderr, "[DisplayBridge.Native] Zero-copy: ProcessInput that bai hr=0x%08lX -- chuyen ve map CPU cho frame nay.\n", hr);
            }
        }
        // Fall through to the CPU-map path below for THIS frame rather than
        // dropping it outright -- a single zero-copy hiccup shouldn't cost
        // a whole frame if the CPU-map fallback can still deliver it.
    }

    // GPU-converted, but not fed zero-copy: map the already-NV12 texture to
    // CPU (plain per-row memcpy, no per-pixel color math) and feed via the
    // existing memory-buffer path.
    std::vector<uint8_t> nv12;
    EncoderError mapErr = MapNv12TextureToCpu(nv12Texture, nv12);
    if (mapErr != EncoderError::Ok) return mapErr;
    return SubmitNv12MemoryBuffer(nv12, ptsUs);
}

EncoderError H264Encoder::SubmitFrame(ID3D11Texture2D* texture, uint64_t ptsUs)
{
    if (!m_initialized || !m_transform) return EncoderError::NotInitialized;

    if (m_gpuColorConversionAvailable)
    {
        EncoderError gpuErr = SubmitFrameGpu(texture, ptsUs);
        if (gpuErr == EncoderError::Ok) return gpuErr;

        fprintf(stderr, "[DisplayBridge.Native] GPU color conversion loi khi chay (err=%d) -- HA XUONG CPU fallback (cham hon) cho cac frame tiep theo trong phien nay.\n", static_cast<int>(gpuErr));
        m_gpuColorConversionAvailable = false;
        // Fall through to the CPU path below so THIS frame still gets
        // delivered instead of being silently dropped.
    }

    std::vector<uint8_t> nv12;
    UINT stride = 0;
    EncoderError convErr = ConvertBgraToNv12CpuFallback(texture, nv12, stride);
    if (convErr != EncoderError::Ok) return convErr;
    return SubmitNv12MemoryBuffer(nv12, ptsUs);
}

EncoderError H264Encoder::TryGetEncodedSample(std::vector<uint8_t>& outData, uint64_t& outPtsUs, bool& outKeyframe)
{
    if (!m_initialized || !m_transform) return EncoderError::NotInitialized;

    MFT_OUTPUT_STREAM_INFO streamInfo{};
    HRESULT hr = m_transform->GetOutputStreamInfo(m_outputStreamId, &streamInfo);
    if (FAILED(hr)) return EncoderError::ProcessOutputFailed;

    ComPtr<IMFSample> outputSample;
    ComPtr<IMFMediaBuffer> outputBuffer;
    bool providesSamples = (streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

    MFT_OUTPUT_DATA_BUFFER outputDataBuffer{};
    outputDataBuffer.dwStreamID = m_outputStreamId;

    if (!providesSamples)
    {
        hr = MFCreateSample(outputSample.GetAddressOf());
        if (FAILED(hr)) return EncoderError::ProcessOutputFailed;
        hr = MFCreateMemoryBuffer(streamInfo.cbSize > 0 ? streamInfo.cbSize : (m_width * m_height * 2), outputBuffer.GetAddressOf());
        if (FAILED(hr)) return EncoderError::ProcessOutputFailed;
        outputSample->AddBuffer(outputBuffer.Get());
        outputDataBuffer.pSample = outputSample.Get();
    }

    DWORD status = 0;
    hr = m_transform->ProcessOutput(0, 1, &outputDataBuffer, &status);

    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
    {
        outData.clear();
        return EncoderError::ProcessOutputFailed;
    }
    if (FAILED(hr))
    {
        return EncoderError::ProcessOutputFailed;
    }

    ComPtr<IMFSample> resultSample = providesSamples ? ComPtr<IMFSample>(outputDataBuffer.pSample) : outputSample;
    if (!resultSample)
    {
        return EncoderError::ProcessOutputFailed;
    }

    ComPtr<IMFMediaBuffer> resultBuffer;
    hr = resultSample->ConvertToContiguousBuffer(resultBuffer.GetAddressOf());
    if (FAILED(hr)) return EncoderError::ProcessOutputFailed;

    BYTE* data = nullptr;
    DWORD maxLen = 0, curLen = 0;
    hr = resultBuffer->Lock(&data, &maxLen, &curLen);
    if (FAILED(hr)) return EncoderError::ProcessOutputFailed;

    outData.assign(data, data + curLen);
    resultBuffer->Unlock();

    LONGLONG sampleTime = 0;
    resultSample->GetSampleTime(&sampleTime);
    outPtsUs = static_cast<uint64_t>(sampleTime / 10); // 100ns -> us

    UINT32 cleanPoint = 0;
    hr = resultSample->GetUINT32(MFSampleExtension_CleanPoint, &cleanPoint);
    outKeyframe = SUCCEEDED(hr) && cleanPoint != 0;

    // R17: ensure the sequence header is in-band immediately before this
    // NAL data if it's an IDR/keyframe sample (see H264Encoder.h comment +
    // M1-Android VideoDecoderActivity.kt which relies on this instead of
    // csd-0/csd-1, for both H.264 and HEVC).
    if (!m_spsPpsCaptured)
    {
        CaptureSequenceHeader(); // retry — some MFTs only populate this after 1st ProcessOutput
    }
    PrependSequenceHeaderIfKeyframe(outData, outKeyframe);

    // Release provider-owned sample event if applicable.
    if (providesSamples && outputDataBuffer.pEvents)
    {
        outputDataBuffer.pEvents->Release();
    }

    return EncoderError::Ok;
}

void H264Encoder::Shutdown()
{
    if (m_transform)
    {
        m_transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        m_transform->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        m_transform.Reset();
    }
    for (UINT i = 0; i < kNv12RingSize; ++i)
    {
        m_nv12OutputViews[i].Reset();
        m_nv12GpuTextures[i].Reset();
    }
    m_videoProcessor.Reset();
    m_vpEnumerator.Reset();
    m_dxgiDeviceManager.Reset();
    m_videoContext.Reset();
    m_videoDevice.Reset();
    m_context.Reset();
    m_device.Reset();

    if (m_mfStarted)
    {
        MFShutdown();
        m_mfStarted = false;
    }
    m_initialized = false;
    m_gpuColorConversionAvailable = false;
    m_mftD3D11Aware = false;
    m_backend = EncoderBackend::Unknown;
}
