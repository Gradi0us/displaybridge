// DesktopDuplicationCapture.cpp — M1.1 PoC capture layer implementation.
#include "DesktopDuplicationCapture.h"
#include <dxgi1_2.h>
#include <algorithm>
#include <cwctype>
#include <cstdio>

DesktopDuplicationCapture::~DesktopDuplicationCapture()
{
    Shutdown();
}

namespace
{
    // Case-insensitive substring search for wide strings.
    bool ContainsCaseInsensitive(const std::wstring& haystack, const wchar_t* needle)
    {
        std::wstring h = haystack;
        std::wstring n = needle;
        std::transform(h.begin(), h.end(), h.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        std::transform(n.begin(), n.end(), n.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        return h.find(n) != std::wstring::npos;
    }
}

std::wstring DesktopDuplicationCapture::FindVirtualDisplayAdapterDeviceName()
{
    // Walk every active adapter (EnumDisplayDevices(NULL, i, ..., 0)), then
    // every monitor attached to it (EnumDisplayDevices(adapterName, j, ...,
    // 0)). DeviceID for a monitor looks like
    // "MONITOR\\MTT1337\\{4d36e96e-...}\\0001" for VDD by MTT (hardware ID
    // "MTT1337" confirmed via WmiMonitorID -- see header comment). We match
    // on DeviceID (hardware-based, locale independent) rather than
    // DeviceString (the localized friendly name, e.g. "Generic Monitor (VDD
    // by MTT)") because DeviceID is stable across OS language/build.
    for (DWORD adapterIndex = 0; ; ++adapterIndex)
    {
        DISPLAY_DEVICEW adapterDevice{};
        adapterDevice.cb = sizeof(adapterDevice);
        if (!EnumDisplayDevicesW(nullptr, adapterIndex, &adapterDevice, 0))
        {
            break; // no more adapters
        }

        // Only adapters actually attached to the desktop can be duplicated.
        if (!(adapterDevice.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP))
        {
            continue;
        }

        for (DWORD monitorIndex = 0; ; ++monitorIndex)
        {
            DISPLAY_DEVICEW monitorDevice{};
            monitorDevice.cb = sizeof(monitorDevice);
            if (!EnumDisplayDevicesW(adapterDevice.DeviceName, monitorIndex, &monitorDevice, 0))
            {
                break; // no more monitors on this adapter
            }

            const std::wstring deviceId(monitorDevice.DeviceID);
            const std::wstring deviceString(monitorDevice.DeviceString);
            if (ContainsCaseInsensitive(deviceId, L"MTT1337") ||
                ContainsCaseInsensitive(deviceId, L"MttVDD") ||
                ContainsCaseInsensitive(deviceString, L"MTT") ||
                ContainsCaseInsensitive(deviceString, L"VDD"))
            {
                fprintf(stderr,
                    "[DesktopDuplicationCapture] Found virtual monitor via EnumDisplayDevices: adapter=%ls monitorDeviceString=%ls monitorDeviceId=%ls\n",
                    adapterDevice.DeviceName, monitorDevice.DeviceString, monitorDevice.DeviceID);
                return std::wstring(adapterDevice.DeviceName);
            }
        }
    }

    return std::wstring(); // not found
}

CaptureError DesktopDuplicationCapture::Init()
{
    if (m_initialized)
    {
        return CaptureError::AlreadyInitialized;
    }

    m_targetDeviceName = FindVirtualDisplayAdapterDeviceName();
    m_usedFallbackPrimary = m_targetDeviceName.empty();
    if (m_usedFallbackPrimary)
    {
        fprintf(stderr,
            "[DesktopDuplicationCapture] KHONG TIM THAY 'VDD by MTT' -- dang mirror man chinh (primary output). "
            "DAY KHONG PHAI hanh vi mong muon (xem RESEARCH-v2-spacedesk-extend-mode-mechanism.md).\n");
    }

    // 1. Create a D3D11 device (BGRA support required for duplication).
    static const D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0,
    };
    D3D_FEATURE_LEVEL obtainedLevel{};

    // 2. Find the IDXGIAdapter1/IDXGIOutput pair whose DXGI_OUTPUT_DESC::
    // DeviceName matches m_targetDeviceName (both are the same GDI device
    // name, e.g. "\\.\DISPLAY2" -- documented DXGI behavior). If
    // m_usedFallbackPrimary, targetDeviceName is empty and this loop simply
    // never matches, so outputToUse/adapterToUse stay null and we fall
    // through to the old default-adapter/output-0 behavior below.
    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(hr) || !factory)
    {
        return CaptureError::DxgiFactoryCreateFailed;
    }

    ComPtr<IDXGIAdapter1> adapterToUse;
    ComPtr<IDXGIOutput> outputToUse;

    if (!m_usedFallbackPrimary)
    {
        for (UINT ai = 0; ; ++ai)
        {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(ai, adapter.GetAddressOf()) == DXGI_ERROR_NOT_FOUND)
            {
                break;
            }

            for (UINT oi = 0; ; ++oi)
            {
                ComPtr<IDXGIOutput> output;
                if (adapter->EnumOutputs(oi, output.GetAddressOf()) == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }

                DXGI_OUTPUT_DESC desc{};
                if (SUCCEEDED(output->GetDesc(&desc)) && m_targetDeviceName == desc.DeviceName)
                {
                    adapterToUse = adapter;
                    outputToUse = output;
                    // Session 17 bug fix: DesktopCoordinates is an arbitrary
                    // desktop-space rectangle -- its width/height is NOT
                    // guaranteed even just because the virtual display's
                    // configured resolution is (observed 1013x1011 for a
                    // monitor whose DesktopCoordinates happened to start at
                    // an odd desktop X offset). NV12 requires even
                    // width/height (4:2:0 chroma subsampling): odd
                    // dimensions made the GPU NV12 output texture creation
                    // fail (CreateTexture2D hr=0x80070057/E_INVALIDARG) AND
                    // corrupted the heap in the CPU fallback path
                    // (ConvertBgraToNv12CpuFallback's UV-plane indexing
                    // overruns nv12Out's width*height/2-sized allocation
                    // when width/height are odd), causing the
                    // AccessViolationException crashes reproduced via
                    // NativeSmokeTest. Round down to the nearest even pixel
                    // on both axes -- losing at most 1 px of edge content is
                    // imperceptible and far simpler than plumbing odd-size
                    // handling through every NV12 consumer.
                    m_width = static_cast<UINT>(desc.DesktopCoordinates.right - desc.DesktopCoordinates.left) & ~1u;
                    m_height = static_cast<UINT>(desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top) & ~1u;
                    fprintf(stderr,
                        "[DesktopDuplicationCapture] Matched output %ls: DesktopCoordinates=(%ld,%ld)-(%ld,%ld) AttachedToDesktop=%d\n",
                        desc.DeviceName, desc.DesktopCoordinates.left, desc.DesktopCoordinates.top,
                        desc.DesktopCoordinates.right, desc.DesktopCoordinates.bottom, desc.AttachedToDesktop);
                }
            }

            if (outputToUse)
            {
                break;
            }
        }

        if (!outputToUse)
        {
            // Matched a monitor via EnumDisplayDevices but DXGI doesn't see
            // it as an output (shouldn't normally happen if it's attached
            // to the desktop) -- fall back rather than hard-fail.
            m_usedFallbackPrimary = true;
            fprintf(stderr,
                "[DesktopDuplicationCapture] 'VDD by MTT' co trong EnumDisplayDevices nhung khong tim thay qua DXGI EnumOutputs "
                "-- dang mirror man chinh (primary output).\n");
        }
    }

    // 3. Create the D3D11 device on the matched adapter (or the default
    // adapter if we're falling back to the old primary-output behavior).
    hr = D3D11CreateDevice(
        adapterToUse.Get(), // nullptr => default adapter (fallback path)
        adapterToUse ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        featureLevels, ARRAYSIZE(featureLevels),
        D3D11_SDK_VERSION,
        m_device.GetAddressOf(),
        &obtainedLevel,
        m_context.GetAddressOf());

    if (FAILED(hr) || !m_device)
    {
        return CaptureError::D3D11DeviceCreateFailed;
    }

    if (m_usedFallbackPrimary)
    {
        // Old behavior: QI up to IDXGIDevice -> adapter -> output 0.
        ComPtr<IDXGIDevice> dxgiDevice;
        hr = m_device.As(&dxgiDevice);
        if (FAILED(hr))
        {
            return CaptureError::DxgiDeviceQueryFailed;
        }

        ComPtr<IDXGIAdapter> adapter;
        hr = dxgiDevice->GetAdapter(adapter.GetAddressOf());
        if (FAILED(hr))
        {
            return CaptureError::DxgiAdapterGetFailed;
        }

        hr = adapter->EnumOutputs(0, outputToUse.ReleaseAndGetAddressOf());
        if (FAILED(hr))
        {
            return CaptureError::DxgiOutputGetFailed;
        }

        DXGI_OUTPUT_DESC desc{};
        if (SUCCEEDED(outputToUse->GetDesc(&desc)))
        {
            m_targetDeviceName = desc.DeviceName;
            // Same odd-dimension NV12 hazard as the primary match above --
            // see that comment for the full explanation.
            m_width = static_cast<UINT>(desc.DesktopCoordinates.right - desc.DesktopCoordinates.left) & ~1u;
            m_height = static_cast<UINT>(desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top) & ~1u;
        }
    }

    ComPtr<IDXGIOutput1> output1;
    hr = outputToUse.As(&output1);
    if (FAILED(hr))
    {
        return CaptureError::DxgiOutput1QueryFailed;
    }

    // 4. Duplicate it.
    hr = output1->DuplicateOutput(m_device.Get(), m_duplication.GetAddressOf());
    if (FAILED(hr))
    {
        return CaptureError::DuplicateOutputFailed;
    }

    m_initialized = true;
    return CaptureError::Ok;
}

CaptureError DesktopDuplicationCapture::AcquireFrame(UINT timeoutMs, ComPtr<ID3D11Texture2D>& outTexture, DXGI_OUTDUPL_FRAME_INFO& outFrameInfo)
{
    if (!m_initialized || !m_duplication)
    {
        return CaptureError::NotInitialized;
    }

    // Release any previously held frame first (defensive — normal callers
    // call ReleaseFrame() explicitly after consuming the texture).
    if (m_frameHeld)
    {
        ReleaseFrame();
    }

    ComPtr<IDXGIResource> desktopResource;
    ZeroMemory(&outFrameInfo, sizeof(outFrameInfo));

    HRESULT hr = m_duplication->AcquireNextFrame(timeoutMs, &outFrameInfo, desktopResource.GetAddressOf());
    if (hr == DXGI_ERROR_WAIT_TIMEOUT)
    {
        return CaptureError::AcquireFrameTimeout;
    }
    if (FAILED(hr))
    {
        // DXGI_ERROR_ACCESS_LOST etc — caller should re-Init.
        return CaptureError::AcquireFrameFailed;
    }

    m_frameHeld = true;

    ComPtr<ID3D11Texture2D> texture;
    hr = desktopResource.As(&texture);
    if (FAILED(hr))
    {
        ReleaseFrame();
        return CaptureError::AcquireFrameFailed;
    }

    outTexture = texture;
    return CaptureError::Ok;
}

void DesktopDuplicationCapture::ReleaseFrame()
{
    if (m_frameHeld && m_duplication)
    {
        m_duplication->ReleaseFrame();
        m_frameHeld = false;
    }
}

void DesktopDuplicationCapture::Shutdown()
{
    ReleaseFrame();
    m_duplication.Reset();
    m_context.Reset();
    m_device.Reset();
    m_initialized = false;
}
