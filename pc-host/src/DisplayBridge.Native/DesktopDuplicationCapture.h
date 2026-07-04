// DesktopDuplicationCapture.h — M1.1 PoC capture layer.
//
// RESEARCH-v2 root-cause fix (2026-07-03): this used to always duplicate
// adapter-0/output-0 (whatever Windows calls "primary"), which is why the
// tablet was mirroring the LAPTOP panel instead of showing its own native
// resolution -- Windows never even knew the tablet was a separate monitor.
// Since then the user installed "Virtual Driver Control" (VDD by MTT,
// HardwareID Root\MttVDD, monitor HardwareID prefix "MTT1337" confirmed via
// `Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID` ->
// "DISPLAY\MTT1337\...") which registers as its own PnP monitor/adapter.
// Init() now enumerates every IDXGIAdapter1/IDXGIOutput via
// IDXGIFactory1::EnumAdapters1 + IDXGIAdapter1::EnumOutputs, and for each
// output cross-references its DXGI_OUTPUT_DESC::DeviceName (a GDI device
// name like "\\.\DISPLAY2") against Win32 EnumDisplayDevices() to find the
// one whose attached monitor's DeviceID contains "MTT1337" (the VDD
// hardware ID -- more robust than matching the localized friendly string
// "Generic Monitor (VDD by MTT)", which can vary by OS language/build).
// If no such output is found (driver disabled/removed), Init() falls back
// to the old primary-output behavior and sets UsedFallbackPrimary() so the
// caller (CaptureEncodeExports.cpp / NativeCaptureEncoder.cs) can log a
// loud warning -- mirroring the laptop panel is explicitly NOT the desired
// behavior, see RESEARCH-v2-spacedesk-extend-mode-mechanism.md.
#pragma once

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <cstdint>
#include <string>

using Microsoft::WRL::ComPtr;

// Error codes returned by DisplayBridge_CaptureInit / GetFrame.
enum class CaptureError : int
{
    Ok = 0,
    D3D11DeviceCreateFailed = 1,
    DxgiDeviceQueryFailed = 2,
    DxgiAdapterGetFailed = 3,
    DxgiOutputGetFailed = 4,
    DxgiOutput1QueryFailed = 5,
    DuplicateOutputFailed = 6,
    AcquireFrameTimeout = 7,
    AcquireFrameFailed = 8,
    NotInitialized = 9,
    AlreadyInitialized = 10,
    DxgiFactoryCreateFailed = 11,
    NoOutputsEnumerated = 12,
};

// Thin RAII-ish wrapper around IDXGIOutputDuplication for the primary
// display. Not thread-safe; caller (NativeCaptureEncoder on the C# side)
// is expected to serialize calls.
class DesktopDuplicationCapture
{
public:
    DesktopDuplicationCapture() = default;
    ~DesktopDuplicationCapture();

    DesktopDuplicationCapture(const DesktopDuplicationCapture&) = delete;
    DesktopDuplicationCapture& operator=(const DesktopDuplicationCapture&) = delete;

    // Creates the D3D11 device + IDXGIOutputDuplication for the output that
    // hosts the "VDD by MTT" virtual monitor (see header comment). Falls
    // back to the primary output (old behavior) if that monitor can't be
    // found, in which case UsedFallbackPrimary() returns true.
    CaptureError Init();

    // Blocks up to timeoutMs waiting for the next desktop frame. On
    // success, outTexture holds an addref'd reference to the acquired
    // D3D11 texture (caller must Release / ComPtr handles it) and
    // outFrameInfo holds the DXGI frame metadata (present time, etc).
    // Caller MUST call ReleaseFrame() after consuming the texture before
    // calling AcquireFrame() again.
    CaptureError AcquireFrame(UINT timeoutMs, ComPtr<ID3D11Texture2D>& outTexture, DXGI_OUTDUPL_FRAME_INFO& outFrameInfo);

    // Releases the frame acquired by AcquireFrame. Safe to call even if
    // no frame is currently held (no-op).
    void ReleaseFrame();

    // Tears down duplication + device. Safe to call multiple times.
    void Shutdown();

    ID3D11Device* Device() const { return m_device.Get(); }
    ID3D11DeviceContext* Context() const { return m_context.Get(); }

    bool IsInitialized() const { return m_initialized; }

    // Real pixel size of the output actually being duplicated (from
    // DXGI_OUTPUT_DESC::DesktopCoordinates), NOT GetSystemMetrics(SM_CXSCREEN)
    // which always reports the PRIMARY monitor regardless of which output
    // we duplicated -- using that for the encoder's target size was the
    // reason frames used to get squeezed to the laptop's aspect ratio.
    UINT Width() const { return m_width; }
    UINT Height() const { return m_height; }

    // True if Init() could not find the "VDD by MTT" virtual monitor and
    // fell back to duplicating the primary (laptop) output instead -- i.e.
    // we are back to the OLD, undesired mirror behavior. Caller MUST log a
    // loud warning when this is true (see CaptureEncodeExports.cpp).
    bool UsedFallbackPrimary() const { return m_usedFallbackPrimary; }

    // GDI device name (e.g. "\\.\DISPLAY2") of the output actually
    // duplicated, for diagnostics/logging.
    const std::wstring& TargetDeviceName() const { return m_targetDeviceName; }

private:
    // Searches all active adapters/monitors via Win32 EnumDisplayDevices for
    // one whose monitor DeviceID contains "MTT1337" (VDD by MTT's hardware
    // ID, confirmed via WmiMonitorID -- see header comment). Returns the
    // GDI adapter device name (e.g. "\\.\DISPLAY2") that owns it, or an
    // empty string if not found.
    static std::wstring FindVirtualDisplayAdapterDeviceName();

    ComPtr<ID3D11Device> m_device;
    ComPtr<ID3D11DeviceContext> m_context;
    ComPtr<IDXGIOutputDuplication> m_duplication;
    bool m_initialized = false;
    bool m_frameHeld = false;
    UINT m_width = 0;
    UINT m_height = 0;
    bool m_usedFallbackPrimary = false;
    std::wstring m_targetDeviceName;
};
