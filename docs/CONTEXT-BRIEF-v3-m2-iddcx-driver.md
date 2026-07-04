# CONTEXT-BRIEF-v3-m2-iddcx-driver

**Created**: 2026-07-03
**Purpose**: Tài liệu tham khảo chuẩn bị code driver IddCx (M2) — ghi lại code mẫu cấu trúc, quy ước, PASS criteria, thông số thiết bị, và ghi chú về phần sẽ bị thay thế.
**Language**: vi
**Scope**: DisplayBridge M2 (IddCx Indirect Display Driver) — video capture từ virtual swapchain thay vì DXGI Desktop Duplication

---

## 1. Đường dẫn & cấu trúc sample code

**Thư mục sample**: `C:\Users\MInhhoangg\Desktop\AI\AI_LOCAL\APP_share\windows-driver\reference\video\IndirectDisplay\`

**Cấu trúc chính**:
```
IddSampleDriver/
├── Driver.h                    # struct IndirectSampleMonitor, Direct3DDevice, SwapChainProcessor, callbacks
├── Driver.cpp                  # EDID block 128 bytes + monitor mode list (sample const data)
├── Trace.h                     # tracing/logging macros
└── IddSampleDriver.inf         # INF file để cài driver
IddSampleApp/
└── ...                         # companion app (tạm bỏ qua, focus driver)
IddSampleDriver.sln            # Visual Studio solution
```

**File chính để tham khảo**:
- `Driver.h` — khai báo struct + class
- `Driver.cpp` line 25-77 — EDID block sample + mode list khai báo cách
- `Driver.cpp` line 82-120 — helper functions tạo mode + signal info
- `Driver.cpp` line 322-469 — SwapChainProcessor xử lý swapchain frame
- `Driver.cpp` line 485-555 — IndirectDeviceContext init adapter
- `IddSampleDriver.inf` — INF structure

---

## 2. Tóm tắt cấu trúc chính & code mẫu

### 2.1 Cấu trúc khai báo Monitor (EDID + Mode List)

**File**: `Driver.h` line 39-51

```cpp
struct IndirectSampleMonitor
{
    static constexpr size_t szEdidBlock = 128;
    static constexpr size_t szModeList = 3;

    const BYTE pEdidBlock[szEdidBlock];  // EDID binary, 128 bytes
    const struct SampleMonitorMode {
        DWORD Width;
        DWORD Height;
        DWORD VSync;  // Hz (e.g., 60, 120)
    } pModeList[szModeList];
    const DWORD ulPreferredModeIdx;  // index của mode ưa thích (thường 0)
};
```

### 2.2 EDID block sample (mẫu thực từ Dell S2719DGF)

**File**: `Driver.cpp` line 40-57

```cpp
// Modified EDID from Dell S2719DGF
{
    {
        0x00,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x00,0x10,0xAC,0xE6,0xD0,0x55,0x5A,0x4A,0x30,
        0x24,0x1D,0x01,0x04,0xA5,0x3C,0x22,0x78,0xFB,0x6C,0xE5,0xA5,0x55,0x50,0xA0,0x23,
        0x0B,0x50,0x54,0x00,0x02,0x00,0xD1,0xC0,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,
        0x01,0x01,0x01,0x01,0x01,0x01,0x58,0xE3,0x00,0xA0,0xA0,0xA0,0x29,0x50,0x30,0x20,
        0x35,0x00,0x55,0x50,0x21,0x00,0x00,0x1A,0x00,0x00,0x00,0xFF,0x00,0x37,0x4A,0x51,
        0x58,0x42,0x59,0x32,0x0A,0x20,0x20,0x20,0x20,0x20,0x00,0x00,0x00,0xFC,0x00,0x53,
        0x32,0x37,0x31,0x39,0x44,0x47,0x46,0x0A,0x20,0x20,0x20,0x20,0x00,0x00,0x00,0xFD,
        0x00,0x28,0x9B,0xFA,0xFA,0x40,0x01,0x0A,0x20,0x20,0x20,0x20,0x20,0x20,0x00,0x2C
    },
    // Mode list
    {
        { 2560, 1440, 144 },
        { 1920, 1080,  60 },
        { 1024,  768,  60 },
    },
    0  // preferred index
}
```

**Chú ý**: Mỗi EDID block là 128 bytes binary. Có thể:
- Copy từ EDID thực của thiết bị (dùng công cụ như Aida64/MonitorInfo trên Windows)
- Hoặc tạo EDIDs giả lập sau khi có tool xây dựng EDID (chưa cần ngay bây giờ)

### 2.3 Helper functions tạo mode từ thông số (resolution + refresh)

**File**: `Driver.cpp` line 82-120

```cpp
// Tạo DISPLAYCONFIG_VIDEO_SIGNAL_INFO từ width/height/vsync
static inline void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& Mode, 
    DWORD Width, DWORD Height, DWORD VSync, bool bMonitorMode)
{
    Mode.totalSize.cx = Mode.activeSize.cx = Width;
    Mode.totalSize.cy = Mode.activeSize.cy = Height;

    // vSyncFreqDivider: bMonitorMode=0 (monitor mode), driver mode=1
    Mode.AdditionalSignalInfo.vSyncFreqDivider = bMonitorMode ? 0 : 1;
    Mode.AdditionalSignalInfo.videoStandard = 255;

    Mode.vSyncFreq.Numerator = VSync;
    Mode.vSyncFreq.Denominator = 1;
    Mode.hSyncFreq.Numerator = VSync * Height;
    Mode.hSyncFreq.Denominator = 1;

    Mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;

    // Pixel rate = Hz × width × height
    Mode.pixelRate = ((UINT64) VSync) * ((UINT64) Width) * ((UINT64) Height);
}

// Tạo IDDCX_MONITOR_MODE (cho Monitor)
static IDDCX_MONITOR_MODE CreateIddCxMonitorMode(DWORD Width, DWORD Height, DWORD VSync, 
    IDDCX_MONITOR_MODE_ORIGIN Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER)
{
    IDDCX_MONITOR_MODE Mode = {};
    Mode.Size = sizeof(Mode);
    Mode.Origin = Origin;
    FillSignalInfo(Mode.MonitorVideoSignalInfo, Width, Height, VSync, true);
    return Mode;
}

// Tạo IDDCX_TARGET_MODE (cho Target/Source)
static IDDCX_TARGET_MODE CreateIddCxTargetMode(DWORD Width, DWORD Height, DWORD VSync)
{
    IDDCX_TARGET_MODE Mode = {};
    Mode.Size = sizeof(Mode);
    FillSignalInfo(Mode.TargetVideoSignalInfo.targetVideoSignalInfo, Width, Height, VSync, false);
    return Mode;
}
```

### 2.4 SwapChainProcessor — xử lý frame từ swapchain

**File**: `Driver.cpp` line 322-469

**Cách hoạt động**:
1. Constructor nhận `IDDCX_SWAPCHAIN` + D3D Device + frame event
2. Khởi động thread `RunThread()` để xử lý
3. Vòng lặp chính: gọi `IddCxSwapChainReleaseAndAcquireBuffer()` để lấy frame mới
4. Nếu frame sẵn sàng: xử lý frame (TODO: encode/compress) rồi `IddCxSwapChainFinishedProcessingFrame()`
5. Nếu chưa có frame: chờ event rồi thử lại
6. Khi thoát: gọi `WdfObjectDelete()` trên swapchain

**Code mẫu vòng lặp chính**:
```cpp
void SwapChainProcessor::RunCore()
{
    // Bước 1: Lấy DXGI device từ D3D device
    ComPtr<IDXGIDevice> DxgiDevice;
    HRESULT hr = m_Device->Device.As(&DxgiDevice);
    
    // Bước 2: Set device cho swapchain
    IDARG_IN_SWAPCHAINSETDEVICE SetDevice = {};
    SetDevice.pDevice = DxgiDevice.Get();
    hr = IddCxSwapChainSetDevice(m_hSwapChain, &SetDevice);

    // Bước 3: Vòng lặp frame
    for (;;)
    {
        ComPtr<IDXGIResource> AcquiredBuffer;
        IDARG_OUT_RELEASEANDACQUIREBUFFER Buffer = {};
        
        // Lấy frame tiếp theo
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &Buffer);

        if (hr == E_PENDING)
        {
            // Chưa có frame, chờ
            HANDLE WaitHandles[] = { m_hAvailableBufferEvent, m_hTerminateEvent.Get() };
            DWORD WaitResult = WaitForMultipleObjects(ARRAYSIZE(WaitHandles), WaitHandles, FALSE, 16);
            if (WaitResult == WAIT_OBJECT_0 + 1) break;  // terminate
            continue;
        }
        else if (SUCCEEDED(hr))
        {
            // ========== TODO: PROCESS FRAME HERE ==========
            // Buffer.MetaData.pSurface là IDXGIResource mới
            // → GPU copy / encode / compress / gửi qua socket
            // 
            // Thay thế DesktopDuplicationCapture logic ở đây:
            // - Thay vì chụp primary screen qua DXGI Duplication
            // - Giờ nhận frame đã được DWM render sẵn từ swapchain
            // - Có thể dùng lại H264Encoder/Media Foundation từ session 6
            // =============================================
            
            AcquiredBuffer.Attach(Buffer.MetaData.pSurface);
            // ... xử lý, sau đó Release
            AcquiredBuffer.Reset();
            
            // Báo xong frame này
            hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
            if (FAILED(hr)) break;
        }
        else
        {
            // Swapchain bỏ (e.g., DXGI_ERROR_ACCESS_LOST)
            break;
        }
    }
}
```

**Điểm khác SO VỚI DesktopDuplicationCapture**:
- DesktopDuplicationCapture: **chụp** primary output (capture toàn bộ desktop từ laptop → thumbnail chất lượng thấp)
- SwapChainProcessor: **nhận** frame từ DWM cho tablet virtual monitor (DWM compose thẳng→tablet ở native 3000×1920)

### 2.5 INF file cấu trúc

**File**: `IddSampleDriver.inf`

**Cấu trúc cơ bản**:
```
[Version]
Signature = "$Windows NT$"
ClassGUID = {4D36E968-E325-11CE-BFC1-08002BE10318}  # Display class
Class = Display
ClassVer = 2.0
Provider = <ManufacturerName>
CatalogFile = IddSampleDriver.cat
DriverVer = ...

[Manufacturer]
%ManufacturerName% = Standard, NT$ARCH$.10.0...22000

[Standard.NT$ARCH$.10.0...22000]
%DeviceName% = MyDevice_Install, Root\IddSampleDriver  # HW ID cho VS remote debug
%DeviceName% = MyDevice_Install, IddSampleDriver       # HW ID cho IddSampleApp

[MyDevice_Install.NT]
Include = WUDFRD.inf
Needs = WUDFRD.NT
CopyFiles = UMDriverCopy

[MyDevice_Install.NT.hw]
Include = WUDFRD.inf
Needs = WUDFRD.NT.HW
AddReg = MyDevice_HardwareDeviceSettings

[MyDevice_HardwareDeviceSettings]
HKR, , "UpperFilters", %REG_MULTI_SZ%, "IndirectKmd"
HKR, "WUDF", "DeviceGroupId", %REG_SZ%, "IddSampleDriverGroup"

[MyDevice_Install.NT.Services]
Include = WUDFRD.inf
Needs = WUDFRD.NT.Services

[MyDevice_Install.NT.Wdf]
UmdfService = IddSampleDriver, IddSampleDriver_Install
UmdfServiceOrder = IddSampleDriver
UmdfKernelModeClientPolicy = AllowKernelModeClients

[IddSampleDriver_Install]
UmdfLibraryVersion = $UMDFVERSION$
ServiceBinary = %12%\UMDF\IddSampleDriver.dll
UmdfExtensions = IddCx0102  # IddCx extension version

[DestinationDirs]
UMDriverCopy = 12,UMDF   # Copy to drivers\umdf

[UMDriverCopy]
IddSampleDriver.dll

[Strings]
ManufacturerName = "<YourManuName>"
DiskName = "IddSampleDriver Installation Disk"
DeviceName = "IddSampleDriver Device"
REG_MULTI_SZ = 0x00010000
REG_SZ = 0x00000000
```

**Chú ý INF khi code driver DisplayBridge**:
- `UpperFilters = IndirectKmd` — kernel-mode indirect display manager
- `UmdfExtensions = IddCx0102` — phiên bản IddCx (có thể cần cập nhật tùy WDK cài)
- HW ID (`Root\IddSampleDriver` hay custom) quyết định driver match với device nào
- Cần `cat` file (signed catalog) — trước đó dùng test-signing (`bcdedit /set testsigning on`)

---

## 3. PASS criteria (4 tiêu chí từ RESEARCH-v2)

**Định nghĩa "M2 hoàn thành thành công"** — đạt **tất cả 4 tiêu chí**:

1. **Windows Settings → Display hiện 2 monitor riêng biệt** (không phải 1 màn nhân bản)
   - Verify: `Get-Display` PowerShell / Settings UI "Display → Advanced display settings"
   - PASS: 2 entry riêng, mỗi cái tên khác nhau (e.g., "Generic PnP Monitor" cho tablet)

2. **Kéo cửa sổ từ laptop sang "Display 2" → cửa sổ biến mất khỏi laptop, hiện trên tablet** (Extend mode thật)
   - Verify: cmd `window.moveBy(x,y)` hoặc kéo bằng tay
   - PASS: cửa sổ chuyển display 100% (không giật/không mirror)

3. **Độ phân giải "Display 2" trong Windows Settings hiện đúng 3000×1920** (target của HONOR ROD2-W09)
   - KHÔNG phải 2560×1600 (laptop) hoặc bất kỳ resolution khác
   - Verify: Settings → Display → Advanced display → cột "Display resolution"
   - PASS: "3000 x 1920" hiển thị tĩnh (không downscale, không upscale)

4. **Ảnh hiển thị trên tablet KHÔNG méo/vỡ** (no letterboxing, no stretch)
   - Verify: khôi phục desktop 100% đơn vị pixel, không scaling quá bước render
   - PASS: aspect ratio đúng 1:1 (3000÷1920 = 1.5625 trên tablet)

---

## 4. Hardware profile — thông số thiết bị target

**Từ TASK-v1-tablet-display-tracker.md (probe 2026-07-03)**

| Item | Giá trị |
|------|--------|
| **Tablet model** | HONOR ROD2-W09 |
| **OS** | Android 16 (SDK 36) |
| **Serial** | AL9SBB4622000114 |
| **Panel resolution** | 3000×1920 px |
| **Panel refresh rate** | 60/90/120 Hz (NOT 144 Hz — loại bỏ hoàn toàn) |
| **DPI** | 400 dpi |
| **SoC** | Snapdragon 8s Gen 3 (SM8635) |
| **PC (dev)** | Ryzen 9 8945HX + RTX 5060 Laptop (NVENC Blackwell) |

**Default capture profile khi code driver**:
- **Resolution**: 3000×1920 (native tablet, đọc qua CAPS runtime, KHÔNG hardcode)
- **Refresh rate**: min(120, highest supported Hz)
- **Encoder** (session 6 onwards): H.264/HEVC via NVENC (Media Foundation, dùng lại từ H264Encoder.cpp)

---

## 5. Ghi chú: DesktopDuplicationCapture sẽ bị thay thế

**File hiện tại**: `pc-host/src/DisplayBridge.Native/DesktopDuplicationCapture.h/cpp`

**Vai trò hiện tại** (M1 — video PoC):
- Chụp primary output (laptop screen 2560×1600) → encode H.264 → gửi qua socket
- Fallback JPEG qua GDI (session 4)
- Comment rõ: "stand-in for the real IddCx virtual display (M2 scope)"

**Sẽ bị thay thế**:
- **Khi nào**: M2 driver IddCx bắt đầu code
- **Thay bằng**: SwapChainProcessor + frame loop gọi IddCxSwapChainReleaseAndAcquireBuffer
- **Giữ lại đúng**:
  - H264Encoder logic (Media Foundation + NVENC)
  - VideoStreamServer (socket server port 29500)
  - Protocol buffer (schema.yaml, mã hóa/giải mã frame)
  - Toàn bộ Android side (MediaCodec decode, SurfaceView, touch inject)
- **Chỉ đổi NGUỒN frame**: từ "chụp primary screen" → "đọc swapchain của virtual monitor"

**Timeline**:
- Session 4-6: JPEG + H.264 từ DesktopDuplication (mirror fallback)
- M2 onwards: H.264 từ IddCx swapchain (Extend mode thật)
- DesktopDuplicationCapture sẽ lưu lại trong repo cho tham khảo (hoặc xóa nếu tìm lại từ git history)

---

## 6. Test-Signing requirement — quyết định user cần đồng ý

**Để cài driver test-signed trên máy dev**:

```
bcdedit /set testsigning on
```

**Yêu cầu**:
- Phải chạy **Admin** (PowerShell/CMD as Administrator)
- **REBOOT máy** (không có cách nào tránh)
- Sau đó Windows sẽ chấp nhận các driver ký test-sign (không cần official Microsoft signature)

**Lưu ý**:
- Hiện tại **CHƯA thực hiện** — chỉ ghi chú chuẩn bị
- User cần quyết định trước khi M2 vào phase build/install
- Bật test-signing VÃ reboot máy là **điều kiện tiên quyết** để test driver IddCx thật

---

## 7. Checkpoint chuẩn bị M2

Trước khi bắt đầu code M2:

- [ ] Clone Windows-driver-samples + rút gọn `video/IndirectDisplay/` ✅ DONE
- [ ] Đọc Driver.h/cpp để hiểu struct EDID/mode/swapchain ✅ DONE
- [ ] Viết brief này ✅ DONE
- [ ] User kiểm tra WDK đã cài qua `wdksetup.exe` hoặc `Visual Studio Installer → Individual Components → Windows Driver Kit`
- [ ] User chuẩn bị reboot để bật test-signing (`bcdedit /set testsigning on`)
- [ ] Clone repo mẫu hoặc tạo project shell từ `IndirectDisplay/IddSampleDriver.vcxproj` template

---

## Sources & Refs

| # | Source | Mục đích |
|----|--------|---------|
| 1 | Microsoft Windows-driver-samples (github) | Sample IddCx driver source |
| 2 | RESEARCH-v2-spacedesk-extend-mode-mechanism.md | 4 PASS criteria + root cause phân tích |
| 3 | TASK-v1-tablet-display-tracker.md | Hardware profile (HONOR ROD2-W09 3000×1920@120Hz) |
| 4 | MS Learn — Indirect Display Driver Model | https://learn.microsoft.com/windows-hardware/drivers/display/indirect-display-driver-model-overview |
| 5 | MS Learn — IddCx Objects | https://learn.microsoft.com/windows-hardware/drivers/display/iddcx-objects |
| 6 | MS Learn — Download WDK | https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk |

---

## Session bridge

- **Session 6**: H.264 thật chạy E2E (M1 hoàn thành)
- **M2 prep** (phiên này): Tài liệu tham khảo code mẫu driver IddCx
- **M2 next**: Clone repo thực / fork Virtual-Display-Driver / code device context + monitor arrival + swapchain processor

**Unresolved questions**:
- Có fork `Virtual-Display-Driver` từ GitHub hay code từ đầu dựa mẫu Microsoft?
- EDID của tablet (ROD2-W09) có sẵn từ Android `dumpsys` hay tạo fake?
- Test-signing bật trên máy nào (máy dev + máy VM test)?
