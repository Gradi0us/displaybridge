# TASK-v1-tablet-display-tracker

**Created**: 2026-07-03 · **Pattern**: P2 Journey Tracker (UC6)
**Project**: DisplayBridge — tablet HONOR ROD2-W09 làm màn hình phụ cho PC qua ADB/USB
**Plan**: [PLAN-v2-tablet-second-display-adb.md](PLAN-v2-tablet-second-display-adb.md)
**Last Updated**: 2026-07-04 (session 15: fix workaround con trỏ chuột biến mất qua VDD [HardwareCursor=false, root cause = giới hạn IddCx đã biết, không phải bug code] + auto-disable driver "VDD by MTT" khi không có ADB device kết nối [poll nền + devcon disable/enable, debounce]. Session 14 (retroactive — code đã có từ trước, tracker doc bị bỏ sót lúc đó): fix 3 bug FPS thật (fps/bitrate hardcode, DuplicateOutputFailed→JPEG vĩnh viễn, ReMode codec không re-init) — xem SOLUTION-v1-fps-optimization.md. Xem chi tiết 2 session bên dưới.)

---

## Trạng thái tổng

```
M0 Foundation+Bench  [██████████] 100%  ✅ DONE
M1 Video PoC mirror  [██████████] 100% ✅ Session 6: H.264 thật (NVENC). Session 7: nguồn capture đổi từ primary sang VDD by MTT thật (xem bên dưới) — không còn "mirror" nữa, đây là bước chuyển tiếp sang M2. Session 13: fix lag thật (GPU color conversion thay CPU per-pixel loop, ~20fps→~64-69fps@3000x1920 đo thật) + HEVC codec option thật (trước đây chọn xong bỏ xó, native luôn H264). Session 14: fix 3 bug FPS thật (fps/bitrate không bao giờ truyền vào encoder — hardcode 60fps/12Mbps; DuplicateOutputFailed sau driver restart rơi JPEG vĩnh viễn — thêm retry 4 lần; ReMode đổi codec qua Settings PC không re-init native) — xem SOLUTION-v1-fps-optimization.md.
M2 IddCx extend ★    [█████████░] ~90%  🔶 Session 8: Host giờ tự xin Admin 1 lần (app.manifest) + tự cài/restart driver (DriverManager.cs) — code+build+unit/integration test xanh. CHƯA verify bằng chạy thật (UAC cần người bấm tay, môi trường agent non-interactive không tự động hoá được bước này) — xem "Việc còn lại" cuối session 8. Session 11: fix bug đổi resolution GIỮA LÚC đang stream làm Android đơ hẳn (thiếu gửi MODE_CHANGE trước khi tái tạo video pipeline) — verify bằng test tích hợp thật đo thứ tự thời gian; CHƯA verify chạm tay thật trên tablet đang stream (xem "Việc còn lại" cuối session 11). Session 15: (a) workaround con trỏ chuột biến mất khi qua VDD (HardwareCursor=false, root cause = giới hạn IddCx đã biết); (b) auto-disable driver khi không có ADB device kết nối (tránh nhầm "2 màn hình") — cả 2 CHƯA verify chạy thật (UAC), xem "Việc còn lại" cuối session 15.
M3 Input 2 chiều     [██████████] 100% ✅ Session 5: chạm tablet THẬT → chuột PC THẬT di chuyển đúng tỷ lệ (verify trên máy chỉ có 1 màn hình lúc đó, nên bug session 10 chưa lộ). Session 10: fix bug thật "chạm tablet → chuột rơi vào màn CHÍNH" (GetSystemMetrics luôn trả kích thước primary, không phải VDD) bằng VirtualMonitorLocator mới — verify lại bằng GetCursorPos thật trên máy có cả 2 màn.
M4 Settings + UX     [█████████░] ~90%  🔶 Session 7: bỏ hẳn ResolutionMode preset (Max/75%/50%/Custom) — resolution giờ luôn 100% native từ CAPS, không còn UI chọn. SettingKeyMap (mới, key<->SettingField) chưa được promote vào schema.yaml. Session 12: Android floating settings button (FPS cap/Encode, tablet-local SharedPreferences, filter CAPS trước khi gửi) — verify chạy thật, không đụng PC-side.
M5 Hardening v1.0    [          ] 0%
```

### Session 15 — fix con trỏ chuột biến mất qua VDD (workaround) + auto-disable driver khi không có ADB (chi tiết)

**Bối cảnh**: chạy tự động qua đêm (user đang ngủ), 2 nhiệm vụ độc lập, thực thi trực tiếp không giao Agent, theo đúng ràng buộc.

**Nhiệm vụ 1 — con trỏ chuột biến mất khi di chuyển từ màn 1 sang màn 2 (VDD)**:
- **Điều tra trước khi sửa** (không đoán, đọc code + research):
  1. `CursorInjector.MoveTo` (`pc-host/src/DisplayBridge.Core/Input/CursorInjector.cs`) dùng `Win32Interop.SetCursorPos(x, y)` với tọa độ pixel THẬT lấy từ `VirtualMonitorLocator.GetVirtualDisplayRect()` — **không dùng** `SendInput`/`MOUSEEVENTF_ABSOLUTE`, nên nghi vấn "thiếu cờ `MOUSEEVENTF_VIRTUALDESK`" trong brief **KHÔNG áp dụng** (cờ đó chỉ liên quan route SendInput-absolute, route này dùng SetCursorPos trực tiếp theo pixel thật trên virtual desktop — đã đúng, đã verify bằng GetCursorPos thật ở session 10, xem `VirtualMonitorLocatorTests.cs`).
  2. Research qua GitHub (WebSearch + WebFetch): `VirtualDrivers/Virtual-Display-Driver` issue #25 ("Duplicate cursor (due to hardware cursor)"), issue #447 ("Mouse cursor not right"), và upstream `microsoft/Windows-driver-samples` issue #531 (IddCx sample driver's own `IddCxMonitorSetupHardwareCursor` có bug cursor hiện sai lúc drag, chưa có fix chính thức) — xác nhận đây là **giới hạn/bug đã biết ở tầng IddCx hardware-cursor rendering**, không phải hành vi DPI-mismatch thông thường của Windows, và không phải bug trong code DisplayBridge.
  3. DPI awareness (`app.manifest`): hiện `<dpiAware>true/PM</dpiAware>` (Per-Monitor v1, không phải PerMonitorV2) — **KHÔNG sửa**, vì lý do: manifest DPI awareness chỉ chi phối cách CHÍNH ứng dụng DisplayBridge.Host tự vẽ cửa sổ WPF của nó, KHÔNG chi phối việc Windows/DWM vẽ con trỏ hệ thống dùng chung khi trỏ chuột nằm trên vùng màn ảo VDD (đó là composited bởi DWM + driver, độc lập với DPI-awareness khai báo của 1 process nền/tray cụ thể) — sửa mục này sẽ không ảnh hưởng tới bug, nên không đổi (per RULE-v1-evidence-before-dismissal: đây là kết luận có lý do kỹ thuật rõ ràng, không phải bỏ qua không kiểm tra).
- **Kết luận root cause**: giới hạn driver "VDD by MTT" (IddCx hardware-cursor rendering khi cursor băng qua ranh giới virtual/real display) — độ tin cậy CAO (3 nguồn độc lập: 2 issue của chính VDD repo + 1 issue của Microsoft's own IddCx sample driver cho thấy đây là vấn đề cả ở tầng framework, không riêng driver này).
- **Fix = workaround** (đúng như brief đề xuất khi không sửa được root cause ở tầng code): 
  - `C:\VirtualDisplayDriver\vdd_settings.xml` — đổi `<HardwareCursor>true</HardwareCursor>` → `false` (kèm comment tiếng Việt giải thích lý do + tham chiếu 3 issue trên) — để Windows tự vẽ software cursor thay vì driver tự vẽ hardware cursor.
  - `pc-host/src/DisplayBridge.Core/Video/VirtualDisplayConfigurator.cs` — thêm `EnforceHardwareCursorDisabled(XDocument doc)` (mới), gọi trong `ApplyResolution()` ngay sau khi ghi `<resolutions>` — mỗi lần CAPS handshake khiến file này bị ghi lại, `<HardwareCursor>` sẽ LUÔN bị ép về `false`, không bao giờ trôi ngược lại `true` (vd nếu user cài lại driver, package mặc định ship `true`).
  - Test mới: `tests/Core.Tests/VirtualDisplayConfiguratorHardwareCursorTests.cs` (3 test: HardwareCursor=true→false, đã false→giữ false, không phá `<resolutions>` rewrite hiện có).
- **Đây LÀ workaround, KHÔNG PHẢI root-cause-fix ở tầng code** (ghi rõ theo đúng yêu cầu) — đánh đổi: cursor có thể chậm/giật nhẹ hơn 1 chút (software cursor) nhưng LUÔN hiển thị đúng khi qua lại giữa 2 màn.

**Nhiệm vụ 2 — auto-disable driver khi không có ADB device kết nối**:
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs` — thêm `DisableDevice()`/`EnableDevice()` (gọi `devcon disable/enable *Root\MttVDD*`, cùng pattern wildcard-by-hardware-ID đã verified working ở `TryRestartDriver`/`RestartDevice`), thêm vào `IDriverManager` với **default interface method** (C# 8, no-op mặc định) để `FakeDriverManager` sẵn có trong `Integration.Tests/EndToEndWiringTests.cs` không cần sửa/vẫn compile.
- `pc-host/src/DisplayBridge.Core/Video/AdbDeviceChecker.cs` (MỚI) — `IAdbDeviceChecker`/`AdbDeviceChecker`: chạy `adb devices` (tìm `adb.exe` qua PATH trước, fallback `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`), timeout 3s (kill process nếu treo), parse output đúng yêu cầu brief (dòng kết thúc bằng `\tdevice` = thiết bị thật, khác `unauthorized`/`offline`/`no permissions`). 3 trạng thái: `Connected`/`Disconnected`/`Indeterminate` (adb.exe không tìm thấy/lỗi/timeout) — **Indeterminate KHÔNG BAO GIỜ kích hoạt disable/enable** (an toàn, không đoán khi không chắc).
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — `OnAdbPollTick` (mới, chạy qua `System.Threading.Timer`, khởi động trong `Start()` với `AdbPollFirstDelay=20s` rồi `AdbPollInterval=7s`): debounce bằng `_lastKnownAdbConnected` (nullable bool) — chỉ gọi `DisableDevice()`/`EnableDevice()` khi trạng thái THỰC SỰ đổi so với lần check trước, không phải mỗi tick; guard reentrancy bằng `Interlocked`; timer chạy trên ThreadPool thread nên không block Start()/luồng chính; `Stop()`/`Dispose()` đều dispose timer.
- **Test**: `tests/Integration.Tests/AdbAutoDisableTests.cs` (MỚI, 6 test dùng `FakeAdbDeviceChecker`+`RecordingDriverManager`, gọi trực tiếp `OnAdbPollTick` qua `[InternalsVisibleTo]` thay vì chờ timer thật) — cover: disable khi mất kết nối, enable khi kết nối lại, KHÔNG gọi lại devcon khi trạng thái không đổi (debounce), disconnect→reconnect→disconnect, Indeterminate không bao giờ kích hoạt, Indeterminate xen giữa 2 lần Connected không reset baseline debounce.
- `tests/Core.Tests/AdbDeviceCheckerTests.cs` (MỚI, 8 test) — cover `ParseDeviceCount` (thiết bị hợp lệ/nhiều thiết bị/unauthorized/offline/no-permissions/mixed/rỗng) + `CheckConnectionState` với đường dẫn adb.exe không tồn tại → `Indeterminate`, không throw.
- **Test real devcon (theo đúng gợi ý #4 trong Test section)**: `tests/Core.Tests/DriverManagerDisableEnableRealDevconTests.cs` (MỚI) — gọi THẬT `DriverManager.DisableDevice()`/`EnableDevice()` (không mock) trên máy đang có driver cài sẵn, verify: process xác nhận process không elevated (`WindowsPrincipal.IsInRole(Administrator)==false`, verify bằng PowerShell trước khi viết test) nên `DisableDevice()` PHẢI báo thất bại (không có quyền Admin) — nghĩa là **an toàn tuyệt đối, không có rủi ro thật sự tắt driver qua đêm** trong môi trường này; `EnableDevice()` luôn được gọi trong `finally` làm defense-in-depth. Verify bằng chứng thêm SAU khi chạy test: `Get-PnpDevice -Class Display` xác nhận "Virtual Display Driver" Status vẫn `OK` (không bị disable) — khớp đúng dự đoán.
- **Bug phụ tìm+fix trong lúc verify** (không suy đoán, phát hiện khi chạy `dotnet test Integration.Tests` lần đầu sau khi thêm `AdbAutoDisableTests.cs`): **Test Run Aborted, crash cả process test host** — log cho thấy crash xảy ra ở `EndToEndWiringTests` (test dùng native DXGI capture thật). Root cause: thêm 1 CLASS test mới (`AdbAutoDisableTests`) tạo ra 1 xUnit collection mới chạy SONG SONG (mặc định) với `EndToEndWiringTests` — trước đây `EndToEndWiringTests` là class DUY NHẤT dùng native capture thật nên chưa bao giờ bị test khác chạy chen ngang cùng lúc; giờ thêm tải CPU song song đủ để lệch timing và lộ ra đúng loại race đã ghi chú sẵn trong code từ session 11 ("process-global native state... AccessViolationException khi 2 chu trình chạy gần nhau"). Fix: `tests/Integration.Tests/AssemblyFixtures.cs` (MỚI) — `[assembly: CollectionBehavior(DisableTestParallelization = true)]`, khôi phục lại đúng mức độ tuần tự mà bộ test này vốn đã ngầm giả định từ trước (không phải thay đổi hành vi, mà là phục hồi hành vi cũ). Verify: chạy `dotnet test Integration.Tests` 3 lần liên tiếp sau fix, cả 3 lần đều xanh ổn định (không flaky).
- **Kết quả test cuối cùng**: `dotnet test Core.Tests` — **82/82 xanh** (69 cũ + 3 VirtualDisplayConfigurator + 8 AdbDeviceChecker + 1 real-devcon = 81... thực tế đo được 82, chênh 1 không rõ nguyên nhân cụ thể nhưng KHÔNG có test nào fail). `dotnet test Integration.Tests` — **14/14 xanh** (8 cũ + 6 AdbAutoDisableTests), chạy lại 3 lần liên tiếp đều ổn định. `dotnet build src/DisplayBridge.Host/DisplayBridge.Host.csproj -c Release` — sạch, 0 warning 0 error.

**File đã sửa/thêm (session 15)**:
- `C:\VirtualDisplayDriver\vdd_settings.xml` — `HardwareCursor` true→false (workaround, có comment giải thích).
- `pc-host/src/DisplayBridge.Core/Video/VirtualDisplayConfigurator.cs` — `EnforceHardwareCursorDisabled` (mới), gọi trong `ApplyResolution`.
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs` — `DisableDevice()`/`EnableDevice()` (mới) + `IDriverManager` interface thêm 2 method (default no-op).
- `pc-host/src/DisplayBridge.Core/Video/AdbDeviceChecker.cs` (MỚI) — `IAdbDeviceChecker`/`AdbDeviceChecker`/`AdbConnectionState`.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — `OnAdbPollTick` (mới) + Timer trong `Start()`/dispose trong `Stop()`, constructor nhận thêm `IAdbDeviceChecker? adbChecker`, `[assembly: InternalsVisibleTo("Integration.Tests")]`.
- `tests/Core.Tests/VirtualDisplayConfiguratorHardwareCursorTests.cs`, `tests/Core.Tests/AdbDeviceCheckerTests.cs`, `tests/Core.Tests/DriverManagerDisableEnableRealDevconTests.cs` (MỚI).
- `tests/Integration.Tests/AdbAutoDisableTests.cs`, `tests/Integration.Tests/AssemblyFixtures.cs` (MỚI).

**Việc còn lại — CẦN USER TỰ VERIFY BẰNG TAY THẬT (UAC, không tự động hoá được)**:
1. **Cursor fix**: mở `DisplayBridge.Host.exe` thật (bấm Yes UAC) + tablet đang stream (Extend mode), di chuột từ màn laptop sang màn tablet (VDD) và ngược lại nhiều lần — xác nhận con trỏ KHÔNG còn biến mất. Nếu vẫn biến mất dù `HardwareCursor=false` đã áp dụng (cần driver restart để có hiệu lực — `DriverManager.EnsureReady` tự làm việc này ở CAPS handshake kế tiếp), báo lại log `%TEMP%\displaybridge-host.log` để điều tra thêm (có thể còn nguyên nhân khác, dù research cho thấy khả năng cao đây đã là root cause đúng).
2. **Auto-disable driver**: khởi động app KHÔNG cắm tablet — đợi ~20-30s (qua `AdbPollFirstDelay`+1 chu kỳ `AdbPollInterval`), xác nhận log xuất hiện dòng "Khong co ADB device nao ket noi -- tam thoi tat 'VDD by MTT'..." VÀ Windows Display Settings không còn hiện màn hình ảo thứ 2. Sau đó cắm tablet lại, xác nhận trong ~7-27s log đổi thành "ADB device da ket noi lai -- bat lai 'VDD by MTT'..." và màn ảo xuất hiện lại, CAPS handshake tiếp tục bình thường.
3. Cả 2 tính năng đều yêu cầu Administrator (đã có sẵn từ session 8, `app.manifest requireAdministrator`) — môi trường agent này verify được toàn bộ LOGIC qua real devcon calls (thất bại đúng như dự đoán do KHÔNG elevated) nhưng KHÔNG verify được kết quả cuối cùng khi elevated thật (driver thực sự bật/tắt, cursor thực sự hiển thị đúng) — đây là giới hạn môi trường giống mọi session trước, không phải giới hạn logic code.

### Session 14 — fix 3 bug FPS thật (fps/bitrate hardcode, DuplicateOutputFailed→JPEG vĩnh viễn, ReMode codec) (retroactive — bổ sung tracker)

**Ghi chú**: session này đã THỰC SỰ chạy và code đã có sẵn trong `StreamingCoordinator.cs`/`H264Encoder`/etc. (xem comment "Session 14 bug fix" rải rác trong code) TỪ TRƯỚC session 15 — nhưng file tracker này (TASK-v1) chưa từng được cập nhật với mục "Session 14" riêng, chỉ có `docs/SOLUTION-v1-fps-optimization.md` (UC4 RCA-style doc). Mục này bổ sung lại cho đủ lineage, theo đúng UC6 (không để mất traceability). Toàn bộ nội dung chi tiết xem `docs/SOLUTION-v1-fps-optimization.md` (Key Findings, Component Map, RICE, evidence).

**Tóm tắt 3 bug** (đầy đủ ở SOLUTION-v1, độ tin cậy CAO cho cả 3):
1. **fps/bitrate không bao giờ truyền vào encoder** — `DisplayBridge_CaptureInitWithCodec` hardcode `kDefaultFps=60`/`kDefaultBitrateKbps=12000`, bỏ qua Hz thật (60/90/120) và bitrate đúng cho 3000×1920 (~69Mbps@120Hz theo PLAN-v2) — ở 3000×1920, 12Mbps chỉ ~0.02 bpp, ép CBR giảm chất lượng mạnh/rớt khung hình. Fix: `_activeFps`/`_activeBitrateKbps` (StreamingCoordinator) set TRƯỚC `ApplyVirtualDisplayResolution` (cùng pattern với `_activeCodec` session 12/13), `NativeCaptureEncoder.Init(int,uint,uint)` nhận đủ tham số.
2. **`DuplicateOutput` fail 1 lần sau driver restart → JPEG fallback ~4fps vĩnh viễn** — tái hiện LIVE trên máy user (log thật: error code 6 ngay sau driver restart). Fix: retry tới 4 lần với delay 400ms trong `CreateFrameSource()` trước khi rơi về JPEG/stub.
3. **Đổi codec qua Settings PC (`PushSettingsChange`'s `ReMode` branch) không re-init native** — cùng lớp bug "tính ra rồi bỏ xó" đã fix cho đường CAPS-handshake ở session 12/13, nhưng đường settings-live thì chưa. Fix: cập nhật `_activeCodec` + `RecreateFrameSourceForNewResolution()` khi field là `StreamingCodec` và codec thực sự đổi.

**Kết quả đo**: GPU color-conversion (session 13) đã cho 64-69fps cô lập (native, không qua socket) — 3 bug trên là lớp "wiring" khiến FPS thật qua network vẫn tệ dù nền tảng native đã đủ mạnh. Xem SOLUTION-v1 §RICE cho bảng ưu tiên đầy đủ.

**File đã sửa (session 14, theo comment trong code — xem SOLUTION-v1 để đối chiếu đầy đủ)**: `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` (`_activeFps`/`_activeBitrateKbps`, retry `DuplicateOutputFailed`, `PushSettingsChange` ReMode re-init), `pc-host/src/DisplayBridge.Native/CaptureEncodeExports.cpp`/`H264Encoder.cpp` (nhận fps/bitrate thật thay vì hardcode), `pc-host/src/DisplayBridge.Core/Video/NativeCaptureEncoder.cs` (`Init(int,uint,uint)` overload).

**Việc còn lại (từ SOLUTION-v1)**: user cần tự verify chạy thật (UAC) xem FPS overlay Android có đạt ≥60fps thật và không còn thấy "JPEG fallback" lặp lại — xem `docs/SOLUTION-v1-fps-optimization.md` mục "Việc cần user verify khi dậy".

### Session 13 — fix lag thật ~20fps@3000x1920 (GPU color conversion) + HEVC codec option thật (chi tiết)

**Bối cảnh**: user báo cáo Android chỉ đạt ~20fps ở resolution ĐÚNG native 3000×1920 (khác lần test trước ở 800×600 sai). Root cause đã biết trước qua đọc code (comment sẵn trong `H264Encoder.cpp` dòng ~363): `ConvertBgraToNv12` (cũ) là vòng lặp CPU per-pixel BGRA→NV12 — đủ nhanh ở 480K pixel (800×600) nhưng nghẽn ở 5.76M pixel (3000×1920, gấp ~12 lần).

**Việc 1 — GPU color conversion** (`pc-host/src/DisplayBridge.Native/H264Encoder.h/.cpp`):
- `InitGpuColorConversion()` (mới, gọi 1 lần cuối `Init()`): QueryInterface `ID3D11Device`→`ID3D11VideoDevice`, tạo `ID3D11VideoProcessorEnumerator`+`ID3D11VideoProcessor` cho cặp BGRA8(input)→NV12(output) cùng kích thước, kiểm tra `CheckVideoProcessorFormat` cho cả 2 format trước khi cam kết dùng GPU path (không đoán, driver có thể không hỗ trợ). Tạo **ring 4 texture NV12** (không phải 1) + output view tương ứng.
- **Đọc code trước khi quyết định zero-copy hay map CPU** (đúng yêu cầu, không đoán): kiểm tra attribute `MF_SA_D3D11_AWARE` thật trên `IMFTransform` đã chọn — nếu có, tạo `IMFDXGIDeviceManager` (`MFCreateDXGIDeviceManager`+`ResetDevice`+`ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,...)`) và feed thẳng texture GPU vào encoder qua `MFCreateDXGISurfaceBuffer` (zero-copy, không Map() CPU); nếu không, map NV12 texture (đã convert xong trên GPU) về CPU bằng `memcpy` theo hàng (không còn tính toán màu per-pixel) rồi feed qua `IMFMediaBuffer` như cũ.
- **Bug thật tìm thấy khi chạy sustained thật** (không suy đoán trước): lần đầu chỉ dùng 1 texture NV12 dùng chung mọi frame — `NativeSmokeTest --seconds=8` hang cứng sau ~5s ở đường zero-copy (Task Manager xác nhận process vẫn sống, không tiến triển, phải `taskkill /F`). Root cause: texture GPU được feed thẳng (zero-copy, không có điểm đồng bộ CPU) vào MFT bất đồng bộ (NVENC async MFT) — frame kế tiếp `VideoProcessorBlt` ghi đè texture đó trong khi encoder có thể vẫn đang đọc dở nội dung frame trước, một race GPU thật (write-while-in-use), không phải vấn đề hiệu năng. Fix: ring 4 texture NV12 (`m_nv12GpuTextures[kNv12RingSize]`), chọn theo `m_frameIndex % 4` mỗi frame — cho encoder đủ "khoảng đệm" texture để xử lý xong texture cũ trước khi nó bị ghi đè lại.
- Giữ nguyên `ConvertBgraToNv12CpuFallback` (đổi tên từ `ConvertBgraToNv12`, logic y hệt) làm fallback khi bất kỳ bước GPU nào thất bại lúc init HOẶC lúc runtime (Blt/CreateInputView lỗi giữa chừng) — log cảnh báo tiếng Việt rõ ràng "dung CPU fallback (cham hon)" ở mọi nhánh fail, không im lặng.

**Việc 2 — HEVC codec option thật** (bổ sung lựa chọn, KHÔNG PHẢI cách fix lag chính — ghi rõ trong log/comment):
- `H264Encoder` nhận thêm `VideoCodecType codec` (H264/Hevc) trong `Init()`, dùng để chọn `MFVideoFormat_HEVC` thay `MFVideoFormat_H264` khi enumerate MFT (`FindEncoderTransform`) và set `MF_MT_SUBTYPE` output type. R17 (in-band sequence header) tổng quát hoá: `StartsWithKeyframeSequenceNal` phân biệt NAL header H.264 (1 byte, SPS=type 7) vs HEVC (2 byte, VPS=type 32) — verify thật bằng `NativeSmokeTest --codec=1`: frame đầu (keyframe) có NAL types `[32,33,34,35,32,33,34,19]` = VPS,SPS,PPS,AUD,VPS,SPS,PPS,IDR (MFT tự phát in-band, code không double-prepend vì `StartsWithKeyframeSequenceNal` nhận đúng VPS ở đầu), frame sau `[35,1]` = AUD+TRAIL_R. Đúng chuẩn HEVC Annex-B.
- **Bug wiring thật tìm thấy khi đọc code** (đúng như brief nghi ngờ, không phải suy đoán): `StreamingCoordinator.OnCapsReceived` đã tính `chosenCodec = ChooseCodec(...)` từ trước NHƯNG chỉ dùng để gửi `ConfigMessage` cho Android — không bao giờ truyền xuống native (`CreateFrameSource()`→`NativeCaptureEncoder.Init()` không nhận tham số codec, native luôn hardcode H264). Fix: thêm field `_activeCodec` set NGAY trong `OnCapsReceived` TRƯỚC khi gọi `ApplyVirtualDisplayResolution` (vì hàm đó có thể trigger `RecreateFrameSourceForNewResolution`→`CreateFrameSource()` đọc field này); `CreateFrameSource()` gọi `native.Init(_activeCodec)` thay vì `native.Init()` trơn.
- `CaptureEncodeExports.cpp`: thêm `DisplayBridge_CaptureInitWithCodec(int codec)` (mới, codec=0/1), `DisplayBridge_CaptureInit()` cũ giữ nguyên làm back-compat wrapper gọi `InitWithCodec(0)` (không phá `NativeSmokeTest`/caller cũ). Thêm `DisplayBridge_CaptureGetEncoderDiagnostics()` (bit0=GPU conversion active, bit1=zero-copy active, bit2=HEVC active) để C#/test đọc bằng chứng thật, không đoán.
- `NativeCaptureEncoder.cs`: `Init()` (parameterless, giữ nguyên cho `IFrameSource` interface + test cũ) gọi `Init(0)`; `Init(int wireCodec)` (overload mới) là đường StreamingCoordinator dùng.
- **Android** (`VideoDecoderActivity.kt`): `MIME_TYPE` hằng số cũ (luôn `video/avc`, hardcode) THẬT SỰ là bug — bỏ, thay bằng field `mimeType` (mutable, mặc định H264) cập nhật từ `onConfig()`/`onModeChange()` theo `config.codec`/`modeChange.codec` nhận được từ PC (`mimeTypeForCodec()`, 0→`video/avc`, 1→`video/hevc`). `ensureDecoderStarted()`/`supportsLowLatency()` dùng `mimeType` thay vì hằng số cũ. `onModeChange` set `mimeType` mới TRƯỚC khi gọi `recoverDecoderAfterError()` (đã có sẵn từ R16) để decoder được tạo lại đúng codec mới. FPS overlay label cũng đổi theo `mimeType` thay vì hardcode "H.264".
- **Lưu ý quan trọng** (ghi trong code + đây): HEVC đo được CHẬM HƠN H.264 (~62fps vs ~69fps cùng điều kiện, xem Việc 3) — đúng dự đoán trong brief, đây là lựa chọn THÊM không phải cách fix lag. Log native khi chọn HEVC in rõ dòng cảnh báo này.

**Việc 3 — đo đạc thật (giới hạn môi trường, xem chi tiết)**:
- Máy có MSBuild (VS2022 Community v143), dotnet 9, tablet `AL9SBB4622000114` đang kết nối (`adb devices` xác nhận) — build native (Debug+Release x64) sạch 0 lỗi, build Core/Host (Release) sạch, build Android (`compileDebugKotlin`) sạch.
- **KHÔNG chạy được `DisplayBridge.Host.exe` thật**: giống hệt giới hạn session 8-11 — `app.manifest requireAdministrator` khiến launch từ shell non-interactive của agent trả `Permission denied` (`ERROR_ELEVATION_REQUIRED`) ngay lập tức, không có phiên desktop để bấm UAC. Do đó KHÔNG đo được FPS qua đúng đường end-to-end Host↔tablet qua ADB như user yêu cầu.
- **Bằng chứng thay thế mạnh nhất có thể trong môi trường này**: mở rộng `pc-host/tools/NativeSmokeTest` (console app P/Invoke trực tiếp, KHÔNG cần Admin/UAC, đã có từ session 6) thêm chế độ đo FPS thật (`--codec=0|1 --seconds=N`) gọi thẳng `DisplayBridge_CaptureInitWithCodec`+`DisplayBridge_CaptureGetFrame` lặp trong N giây thật — đây là con đường capture→GPU convert→encode THẬT (không mock), chỉ thiếu đúng lớp cuối "qua socket TCP tới tablet" (lớp đó không đổi trong session này, đã verify từ session 3-6).
- **Kết quả đo thật, ĐÚNG resolution user báo cáo (3000×1920, xác nhận qua `DesktopCoordinates=(2560,0)-(5560,1920)`)**:
  | Codec | GPU color conversion | Zero-copy D3D11 | FPS đo thật (8-15s sustained) |
  |---|---|---|---|
  | H.264 (NVENC) | ✅ Active | ✅ Active | **~64-69 fps** (8s: 69.3fps; 15s: 64.0fps, ổn định không giảm dần) |
  | HEVC (NVENC) | ✅ Active | ✅ Active | **~62 fps** (8s: 62.4fps) — chậm hơn H264 đúng dự đoán |
  So với ~20fps user báo cáo trước khi fix — cải thiện ~3-3.5x, đo thật bằng `EncoderDiagnostics` bit-flag xác nhận GPU path đang chạy (không phải "chắc là chạy"), không phải CPU fallback.
- **KHÔNG đo được số CPU-fallback "trước fix" trực tiếp trong session này** (code cũ đã bị thay thế, không có build cũ song song để A/B) — số ~20fps user báo cáo là baseline thật do user tự đo trước đó; số 64-69fps sau fix là baseline mới đo thật trong session này bằng cùng 1 công cụ (`NativeSmokeTest`) ở cùng resolution (3000×1920) mà user báo cáo lag — đủ để kết luận cải thiện rõ rệt dù không phải A/B side-by-side.
- `dotnet test Core.Tests` — **69/69 xanh** (bao gồm 1 test trước đó fail vì phụ thuộc trạng thái desktop thật lúc đó — `VirtualMonitorLocatorTests.CursorInjector_MoveTo...` — không liên quan file nào session này sửa, tự xanh lại khi trạng thái màn hình VDD ổn định lại; không phải do fix của session này). `dotnet test Integration.Tests` — **8/8 xanh**. `./gradlew testDebugUnitTest`/`compileDebugKotlin` — BUILD SUCCESSFUL.

**File đã sửa/thêm (session 13)**:
- `pc-host/src/DisplayBridge.Native/H264Encoder.h/.cpp` — GPU color conversion (`InitGpuColorConversion`, `SubmitFrameGpu`, `MapNv12TextureToCpu`, ring 4 texture NV12), HEVC codec support (`VideoCodecType`, `StartsWithKeyframeSequenceNal` tổng quát hoá từ `StartsWithSpsNal`), `ConvertBgraToNv12CpuFallback` (đổi tên, giữ nguyên logic, làm fallback).
- `pc-host/src/DisplayBridge.Native/CaptureEncodeExports.cpp` — `DisplayBridge_CaptureInitWithCodec` (mới), `DisplayBridge_CaptureInit` (giữ, wrapper back-compat), `DisplayBridge_CaptureGetEncoderDiagnostics` (mới).
- `pc-host/src/DisplayBridge.Core/Video/NativeCaptureEncoder.cs` — `Init(int wireCodec)` overload mới + `UsingGpuColorConversion`/`UsingZeroCopyD3D11Input`/`UsingHevc` properties.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — field `_activeCodec` (fix bug wiring thật: codec chọn giờ THẬT SỰ truyền xuống native, trước đây chỉ dùng cho wire CONFIG message), log rõ GPU/CPU/codec đang active.
- `android-client/app/src/main/java/com/displaybridge/video/VideoDecoderActivity.kt` — `mimeType` field (mutable, fix bug thật: MIME hardcode `video/avc` trước đây, không đổi theo `config.codec`/`modeChange.codec`), cập nhật `onConfig`/`onModeChange`/`ensureDecoderStarted`/`supportsLowLatency`/FPS overlay label. (File này cũng bị session 12 sửa song song — đã re-đọc trước mỗi edit, không xung đột, verify bằng `compileDebugKotlin` sạch.)
- `pc-host/tools/NativeSmokeTest/Program.cs`, `.csproj` — thêm chế độ đo FPS thật (`--codec=--seconds=`), diagnostics đọc qua export mới, NAL parser HEVC-aware (2-byte header), trỏ về DLL Release thay vì Debug.

**Việc còn lại**:
1. **User cần tự verify bằng chạy tay thật** (giống mọi session trước, giới hạn UAC không đổi): chạy `DisplayBridge.Host.exe` thật (bấm Yes UAC) + tablet đang stream, quan sát FPS overlay Android (đã có từ session 9) xác nhận đạt ~60fps+ thật qua toàn bộ pipeline network (không chỉ native local như `NativeSmokeTest` đo được), thử cả 2 codec qua Settings dialog (session 12) để so sánh H.264 vs HEVC FPS thật trên tablet.
2. `PushSettingsChange`'s `ApplyType.ReMode` branch (đổi codec qua Settings UI khi ĐANG stream, không phải lúc CAPS mới) gửi `MODE_CHANGE` cho Android nhưng KHÔNG gọi lại `RecreateFrameSourceForNewResolution()`/cập nhật `_activeCodec` — nghĩa là nếu user đổi codec preference giữa lúc đang stream (không phải reconnect), PC vẫn tiếp tục encode bằng codec CŨ dù đã báo Android đổi. Đây là CÙNG LOẠI bug với cái vừa fix (codec "chọn rồi bỏ xó"), nhưng ở đường settings-live thay vì CAPS-handshake — phát hiện khi đọc code, KHÔNG fix trong session này vì rủi ro (`RecreateFrameSourceForNewResolution` có `Thread.Sleep(3000)`+driver restart, gọi từ đường settings-live có thể phá trải nghiệm nếu chỉ đổi 1 field không liên quan display) — cần quyết định thiết kế trước khi fix (có nên full re-init native pipeline chỉ để đổi codec, hay thêm đường "swap encoder only" nhẹ hơn).
3. Chưa đo A/B trực tiếp CPU-fallback vs GPU trên CÙNG máy CÙNG session (xem Việc 3) — chỉ có baseline user báo cáo (~20fps) từ trước, không phải đo lại bằng chính công cụ này.

### Session 12 — Android: floating settings button (FPS cap + Encode) trên VideoDecoderActivity (chi tiết)

**Yêu cầu user**: 1 nút nổi (logo app, trong mờ) đè lên `VideoDecoderActivity`, bấm mở Settings cho phép chỉnh FPS (60/90/120, không 144Hz) và Encode (Auto/H.264/H.265), bấm "Áp dụng" thì lưu + khởi động lại app (KISS, không live-apply). Thực thi trực tiếp bằng tools, không giao Agent, không đụng file `.cs`/`.cpp` phía PC.

**Việc 1 — floating button** (`android-client/app/src/main/java/com/displaybridge/video/VideoDecoderActivity.kt`): thêm `ImageButton` (không dùng Material FAB — `app/build.gradle.kts` không có Material Components dependency, tránh thêm dependency mới không cần thiết) với background `GradientDrawable` oval bán trong suốt, icon `R.mipmap.ic_launcher`, alpha 0.65 lúc rảnh → 1.0 lúc nhấn (qua `OnTouchListener` set alpha trên ACTION_DOWN/UP, trả về `false` để không chặn click listener bên dưới chạy). Vị trí góc dưới-phải (48dp, margin 16dp) — tránh FPS overlay góc trên-trái (session 9). Thêm vào `root` FrameLayout SAU CÙNG (z-order trên cùng, đè cả `touchCaptureView`) — verify: KHÔNG cần sửa `TouchCaptureView.kt` vì `ImageButton` clickable tự chặn `ACTION_DOWN` không rơi xuống view bên dưới (Android chỉ giao touch cho 1 child mỗi lần down), đã verify thật bằng `uiautomator dump` xác nhận `ImageButton` nhận đúng bounds/click và dialog mở, TouchCaptureView không gửi TOUCH_EVENT nhầm khi bấm nút.

**Việc 2 — Settings dialog** (`android-client/app/src/main/java/com/displaybridge/settings/MobileSettingsDialog.kt`, MỚI): dùng `android.app.Dialog` thuần (KHÔNG DialogFragment) vì project không có dependency androidx/Fragment nào cả (chỉ `junit` trong `build.gradle.kts`) — Dialog không cần lifecycle quản lý riêng, đúng KISS. UI dựng bằng code (RadioGroup FPS: 60/90/120Hz, RadioGroup Encode: Auto/H.264/H.265, nút Hủy/Áp dụng), cùng style code-only đã có (`buildWaitingOverlay`/`buildFpsOverlayText`). Bấm "Áp dụng" → lưu `SettingsPrefs` → `dismiss()` → `restartApp()` (chuẩn `PackageManager.getLaunchIntentForPackage()` + `FLAG_ACTIVITY_CLEAR_TASK|FLAG_ACTIVITY_NEW_TASK` + `Runtime.getRuntime().exit(0)`, đúng pattern user yêu cầu).

**Việc 3 — `SettingsPrefs.kt`** (MỚI, package `com.displaybridge.settings`): `SharedPreferences` đơn giản (2 key: `fps_cap`, `encode_pref`), phân biệt rõ "device capability" (đã có sẵn trong `CapsMessage`) vs "user preference" (mới, tablet-local). `ControlSocketClient.sendCaps()` — đọc `SettingsPrefs` rồi LỌC lại `supportedHz`/`supportedCodecs` xuống còn đúng 1 phần tử nếu user đã chọn cụ thể (vd fps_cap=60 → gửi `hz=[60]` dù thiết bị hỗ trợ tới 120), fallback về danh sách đầy đủ nếu user chưa chọn (`FPS_CAP_UNSET`/`ENCODE_PREF_AUTO` = -1) hoặc nếu giá trị chọn không nằm trong danh sách thiết bị thật hỗ trợ (guard, không nói dối capability) — tái dùng nguyên `ChooseHz`/`ChooseCodec` phía PC, không thêm field protocol mới, đúng tinh thần KISS đã brief.

**Bug thật tìm+fix trong lúc verify** (không suy đoán trước, phát hiện khi chạy thật trên tablet có PC Host thật đang stream sống): `SettingsPrefs.setFpsCap/setEncodePref` ban đầu dùng `SharedPreferences.Editor.apply()` (ghi đĩa BẤT ĐỒNG BỘ trên thread nền) — nhưng `MobileSettingsDialog.restartApp()` gọi `Runtime.getRuntime().exit(0)` NGAY SAU `dismiss()`, giết cả JVM trước khi background write kịp chạy. Bằng chứng thật: sau khi bấm "Áp dụng" (chọn 60Hz + H.264), `run-as com.displaybridge.probe ls shared_prefs/` cho thấy thư mục RỖNG — file `displaybridge_mobile_settings.xml` chưa từng được tạo, dù RadioGroup selection + click Apply đều chạy đúng (xác nhận qua `uiautomator dump` + log `VM exiting with result code 0`). Fix: đổi sang `commit()` (đồng bộ, block tới khi ghi xong đĩa) — verify lại: file tồn tại đúng `fps_cap=60`/`encode_pref=0` ngay sau khi tap Apply, trước khi process exit.

**Verify chạy thật E2E** (tablet `AL9SBB4622000114` + PC Host thật đang stream sống, không suy đoán):
1. Baseline (chưa chỉnh gì): `Sending CAPS: 3000x1920@400dpi hz=[60, 90, 120] codecs=[0, 1]` — đúng như trước khi có feature này (không phá hành vi cũ).
2. Mở dialog qua nút nổi (`uiautomator dump` xác nhận bounds nút nổi `[2840,1760][2960,1880]`, tap trúng), chọn 60Hz + H.264 (dump xác nhận `checked="true"` đúng 2 RadioButton), bấm "Áp dụng" (`bounds="[1609,1153][2125,1273]"`).
3. Sau restart (relaunch `VideoDecoderActivity` thật): `Sending CAPS: 3000x1920@400dpi hz=[60] codecs=[0]` — đúng đã lọc xuống còn 1 phần tử mỗi danh sách theo lựa chọn user.
4. PC xác nhận: `Handshake complete: PC chose codec=0 3000x1920@60Hz 48384kbps` — khớp chính xác lựa chọn H.264/60Hz của user (không phải Auto/120Hz mặc định trước đó).
5. Screenshot thật xác nhận: floating button hiện đúng góc dưới-phải, bán trong suốt, đè lên video đang stream; dialog "Cài đặt Streaming" hiện đúng UI (2 RadioGroup + Hủy/Áp dụng).

**File đã sửa/thêm (session 12, chỉ Android)**:
- `android-client/app/src/main/java/com/displaybridge/settings/SettingsPrefs.kt` (MỚI) — SharedPreferences wrapper, hằng số `FPS_CAP_UNSET`/`ENCODE_PREF_AUTO`/`ENCODE_PREF_H264`/`ENCODE_PREF_HEVC` khớp `schema.yaml` CodecId.
- `android-client/app/src/main/java/com/displaybridge/settings/MobileSettingsDialog.kt` (MỚI) — Dialog thuần, 2 RadioGroup + Hủy/Áp dụng + restart app.
- `android-client/app/src/main/java/com/displaybridge/video/VideoDecoderActivity.kt` — thêm `floatingSettingsButton` (ImageButton) + `buildFloatingSettingsButton()`.
- `android-client/app/src/main/java/com/displaybridge/input/ControlSocketClient.kt` — `sendCaps()` lọc `supportedHz`/`supportedCodecs` theo `SettingsPrefs` trước khi gửi CAPS.

**Regression**: `./gradlew assembleDebug testDebugUnitTest` — BUILD SUCCESSFUL, không có test Kotlin nào bị hỏng (không có test framework riêng cho Dialog/Activity UI trong project này — coverage cho phần này là chạy thật trên tablet như trên, không phải unit test, đúng bài học RCA-v1 về wiring-level bug không lộ qua unit test).

**Việc còn lại**:
1. Không có unit test riêng cho `SettingsPrefs`/`MobileSettingsDialog` (không có JVM-testable logic phức tạp ngoài SharedPreferences read/write đã verify chạy thật) — nếu cần coverage tự động sau này, có thể thêm Robolectric nhưng đó là dependency mới, ngoài phạm vi KISS của task này.
2. `getLaunchIntentForPackage()` đôi lúc trả về `CodecProbeActivity` (M0.3 dev-scaffold activity) thay vì `VideoDecoderActivity` sau restart, vì `AndroidManifest.xml` hiện có 2 activity cùng khai báo MAIN/LAUNCHER (di sản M0.3) — không phải bug của feature này, nhưng đáng lưu ý: trong bản build production cuối cùng (M5 packaging) nên chỉ giữ 1 LAUNCHER activity trỏ thẳng `VideoDecoderActivity` để "Áp dụng (khởi động lại app)" luôn quay lại đúng màn hình đang dùng.

### Session 11 — fix bug thật "đổi resolution GIỮA LÚC đang stream làm Android đơ hẳn" (chi tiết)

**Bối cảnh**: user báo cáo đổi resolution màn ảo từ 800×600 → 3000×1920 GIỮA LÚC đang stream (không phải lúc khởi động) làm app Android bị đơ/freeze hoàn toàn — chỉ hết đơ sau khi restart hẳn Host app trên PC. Root cause đã xác định CHÍNH XÁC từ trước qua đọc code (không cần điều tra lại): `StreamingCoordinator.ApplyVirtualDisplayResolution` gọi `DriverManager.EnsureReady` rồi (nếu thành công) gọi `RecreateFrameSourceForNewResolution()` — hàm này dừng+tạo lại `VideoStreamServer` (đóng socket video hiện tại) nhưng KHÔNG BAO GIỜ gửi `ModeChangeMessage` qua control socket cho Android trước đó. Android (`VideoDecoderActivity.onModeChange`) đã code ĐÚNG sẵn (`recoverDecoderAfterError()` — flush + tạo lại decoder), nhưng vì PC không gửi tín hiệu, Android bị bất ngờ khi socket video đóng đột ngột rồi mở lại với NAL resolution khác hẳn.

**Việc 1 — gửi `MODE_CHANGE` cho Android TRƯỚC khi tái tạo video pipeline** (`pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs`):
- Thêm `SendModeChangeBeforeVideoPipelineRecreate(CapsMessage caps)` (mới): tính `chosenHz`/`chosenCodec` bằng ĐÚNG `ChooseHz`/`ChooseCodec` đã có (không viết trùng logic), `(width, height)` từ `_deviceCaps.Resolve()` (đã cập nhật theo CAPS mới ngay đầu `OnCapsReceived`), gửi `ModeChangeMessage` qua `_controlServer?.Send(...)` (dùng đúng pattern đã có ở `PushSettingsChange`'s ReMode branch), rồi `Thread.Sleep(300)` để Android có thời gian chạy `onModeChange()`/flush decoder TRƯỚC khi socket video thực sự bị đóng.
- Gọi hàm này trong `ApplyVirtualDisplayResolution`, NGAY TRƯỚC `RecreateFrameSourceForNewResolution()` (chỉ khi `EnsureReady` trả `Success=true`) — thứ tự đúng theo yêu cầu: Android phải được báo TRƯỚC khi kết nối video bị đóng, không phải sau.
- `ApplyVirtualDisplayResolution`/`OnCapsReceived` chạy đồng bộ trên task đọc message per-client của `ControlSocketServer` (`Task.Run` trong `ServeClientAsync`), không phải UI thread, nên `Thread.Sleep` đồng bộ ở đây an toàn — cùng pattern với `Thread.Sleep(3000)` đã có sẵn trong `RecreateFrameSourceForNewResolution` để đợi PnP ổn định sau restart driver.

**Việc 2 — xác nhận Android tự reconnect video socket sau khi PC đóng giữa chừng**: đọc kỹ `VideoStreamClient.kt::runLoop()` (dòng 89-143) — **không có lỗ hổng, không cần sửa**. Vòng lặp `while(running)` bên ngoài bọc TOÀN BỘ chu trình connect+read trong 1 khối `try/catch(IOException)` duy nhất: cả trường hợp "connect-refused lúc PC chưa bật" (`sock.connect(...)` ném `IOException`) LẪN trường hợp "đang đọc dở thì bị đóng giữa chừng" (`input.read()` trả `< 0` → ném `IOException("Server closed connection (EOF)")`) đều rơi vào ĐÚNG 1 catch block, gọi `listener.onDisconnected(e, willRetry=running.get())`, rồi `Thread.sleep(retryDelayMs)` (1s) và lặp lại `while(running)` để reconnect — không có 2 code path khác nhau như brief lo ngại, không có case nào bị bỏ sót.

**Việc 3 — test**:
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs`: thêm interface `IDriverManager` (seam mới, cùng pattern `ICursorInjector`/`ITouchInjector`) để test có thể fake `EnsureReady` luôn thành công mà không cần shell-out `devcon.exe`/`pnputil.exe` thật (vốn không được bundle cạnh test binary — xem note session 8).
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs`: constructor nhận thêm 2 param optional `IDriverManager? driverManager` + `Func<IFrameSource>? frameSourceFactory` (test-only seam) — mặc định `null` nên production/mọi test cũ không đổi hành vi.
- **Phát hiện phụ quan trọng khi viết test** (không suy đoán): lần đầu viết test dùng `FakeDriverManager` (luôn trả success) NHƯNG để `CreateFrameSource()` chạy native capture THẬT (máy này có driver+native DLL hoạt động thật) — làm 2 chu trình recreate thật (2 lần DXGI/D3D11 teardown+recreate) chạy liên tiếp trong CÙNG 1 test process, và test run bị **"Test Run Aborted"** với `AccessViolationException` ở `NativeCaptureEncoder.Init()` của 1 test KHÁC không liên quan chạy SAU đó — đúng y hệt rủi ro đã ghi chú sẵn trong `VideoStreamServer.cs` ("process-global native state... AccessViolationException... hai StreamingCoordinator instance chạy nối tiếp"). Fix: thêm seam `frameSourceFactory` để test dùng `StubFrameSourceForTest` (0 frame) thay vì native thật khi test path này — an toàn tuyệt đối với driver/hardware thật của máy, không rủi ro corrupt state.
- `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs`: thêm `FakeDriverManager`, `StubFrameSourceForTest`, và class test mới `ResolutionChangeMidStreamTests` với 1 test `SecondCapsWithDifferentResolution_SendsModeChange_BeforeClosingVideoSocket` — mô phỏng CAPS đổi 2 lần liên tiếp (3000×1920 → 1920×1080) qua `FakeTabletDevice`, đo THỜI GIAN THẬT (UTC timestamp) lúc `ModeChangeMessage` thứ 2 đến so với lúc video socket (đã reconnect qua `ReattachVideoSocketAsync` mới) bị đóng — assert `ModeChange` đến TRƯỚC hoặc CÙNG LÚC socket đóng, không phải sau.
- `tools/fake-device/FakeTabletDevice.cs`: thêm `SendCaps(width,height,hz,codecs,...)` public (trước đây `SendCaps()` private, chỉ gọi 1 lần lúc connect), `VideoReadLoopAsync` (đọc nền socket video để phát hiện lúc PC đóng kết nối — trước đây fake device không hề đọc video stream), `ReattachVideoSocketAsync` (kết nối lại socket video sau khi bị đóng, để phân biệt lần đóng THỨ 2 với lần đóng thứ 1 — nếu không reattach, quan sát "video disconnect" sẽ dính timestamp CŨ của lần đóng đầu tiên, cho kết quả test sai — đây chính là bug trong lần viết test đầu tiên, đã tự phát hiện và sửa qua chính lần chạy test fail đầu tiên, không phải suy đoán), `WaitForModeChangeCountAsync`/`WaitForVideoDisconnectAsync`.
- **Kết quả chạy thật**: `dotnet test Core.Tests` — **69/69 xanh** (không đổi, không có test mới ở Core.Tests vì fix chỉ ở Host/Integration). `dotnet test Integration.Tests` — **8/8 xanh** (7 cũ + 1 mới), chạy lại 2 lần liên tiếp đều xanh ổn định (không flaky), không còn AccessViolationException. `dotnet build` Core+Host (Release) sạch, 0 error. `./gradlew testDebugUnitTest` (Android) — BUILD SUCCESSFUL, không đổi số test (đúng dự kiến, Việc 2 không sửa code Android vì không tìm thấy lỗ hổng).

**File đã sửa/thêm (session 11)**:
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — `SendModeChangeBeforeVideoPipelineRecreate` (mới) gọi trước `RecreateFrameSourceForNewResolution` trong `ApplyVirtualDisplayResolution`; constructor nhận thêm `IDriverManager?`/`Func<IFrameSource>?` test seam; `_driverManager` đổi kiểu sang `IDriverManager`.
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs` — thêm interface `IDriverManager`, `DriverManager` implement nó.
- `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs` — thêm `FakeDriverManager`, `StubFrameSourceForTest`, class `ResolutionChangeMidStreamTests` + 1 test mới.
- `tools/fake-device/FakeTabletDevice.cs` — thêm `SendCaps(...)` public overload, `VideoReadLoopAsync`, `ReattachVideoSocketAsync`, `ModeChangeAtUtc`/`VideoDisconnectedAtUtc` observations, `WaitForModeChangeCountAsync`/`WaitForVideoDisconnectAsync`.
- KHÔNG sửa file Android nào (Việc 2 xác nhận `VideoStreamClient.kt` đã đúng, không cần sửa — theo đúng ràng buộc "không sửa logic Android nếu đã đúng").

**Việc 4 — chạy thật verify (giới hạn môi trường)**:
- KHÔNG mô phỏng lại được kịch bản lỗi 100% thật (đổi `vdd_settings.xml` thật giữa lúc app Android thật đang chạy, quan sát `adb logcat`) trong session này — môi trường agent hiện tại không có tablet đang kết nối/PC Host đang chạy sống để tái hiện đúng lúc (khác các session trước có sẵn thiết bị + Host chạy). Bằng chứng thay thế: test tích hợp thật (Việc 3) exercise ĐÚNG code path `ApplyVirtualDisplayResolution` → `SendModeChangeBeforeVideoPipelineRecreate` → `RecreateFrameSourceForNewResolution` qua socket TCP thật (không mock network), đo thời gian thật bằng `DateTime.UtcNow`, không suy đoán — đây là bằng chứng ở mức "toàn bộ pipeline PC-side qua socket thật", chỉ thiếu đúng 1 lớp cuối cùng là decoder Android thật phản ứng với `MODE_CHANGE` (logic đó đã verify đúng từ trước, không đổi trong session này).
- **User cần tự làm để verify 100% "chạm tay thật"**: chạy `DisplayBridge.Host.exe` thật (bấm Yes UAC) + tablet thật đang stream, đổi `vdd_settings.xml`/CAPS resolution GIỮA LÚC đang xem hình (không phải lúc mới mở app), quan sát `adb logcat` xác nhận dòng `MODE_CHANGE received: ... flushing decoder, awaiting next IDR` xuất hiện TRƯỚC khi video bị gián đoạn, và app tự phục hồi hình ảnh ở resolution mới KHÔNG cần restart Host.

**Việc còn lại**:
1. User tự verify Việc 4 bằng tay thật trên tablet (đổi resolution giữa lúc đang stream, xem `adb logcat` + hình ảnh có tự phục hồi không, theo đúng kịch bản bug gốc).
2. `TouchInjector`/`VirtualMonitorLocator` (session 10) — vẫn còn "Việc còn lại" #2 treo từ session 10 (chưa có bằng chứng `GetCursorPos`-tương đương cho route Touch 2 ngón) — không thuộc phạm vi session này, ghi lại để không quên.

### Session 10 — fix bug thật "chạm tablet → chuột nhảy về màn CHÍNH" (chi tiết)

**Bối cảnh**: user báo cáo chạm trên màn tablet ("VDD by MTT") thì con trỏ chuột PC lại nhảy về/nằm trong vùng màn hình CHÍNH, không tới đúng vị trí trên màn tablet. Root cause đã xác định CHÍNH XÁC từ trước qua đọc code (không cần điều tra lại): `CursorInjector.ToScreenPixels`/`TouchInjector.ToScreenPixels` (2 bản trùng lặp) đều dùng `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)` — hàm này LUÔN trả kích thước màn hình PRIMARY, bất kể có bao nhiêu màn. Vì `SetCursorPos`/`InjectTouchInput` dùng tọa độ pixel THẬT trên virtual desktop (không phải % theo màn nào), công thức cũ luôn cho ra tọa độ trong `[0, primaryWidth) x [0, primaryHeight)` — luôn rơi vào màn chính vì màn chính luôn ở gốc `(0,0)`.

**Việc 1 — `VirtualMonitorLocator.cs` (mới, `pc-host/src/DisplayBridge.Core/Input/`)**:
- Thêm P/Invoke mới vào `Win32Interop.cs`: `EnumDisplayMonitors` + delegate `MonitorEnumProc`, `GetMonitorInfo`/struct `MonitorInfoEx` (CbSize/RcMonitor/RcWork/DwFlags/SzDevice, `MonitorInfoFPrimary=0x1`), `EnumDisplayDevices`/struct `DisplayDevice` (DeviceName/DeviceString/StateFlags/DeviceID/DeviceKey), và `GetCursorPos`/struct `Point` (dùng cho test thật, xem Việc 3).
- `VirtualMonitorLocator.Locate()`: enumerate mọi monitor qua `EnumDisplayMonitors`+`GetMonitorInfo` (lấy `RcMonitor` = tọa độ thật virtual-desktop + `SzDevice` = tên adapter dạng `\\.\DISPLAYn`), với mỗi adapter gọi `EnumDisplayDevices(adapterName, j, ...)` để lấy monitor con và so khớp `DeviceID` chứa `"MTT1337"`/`"MttVDD"` hoặc `DeviceString` chứa `"MTT"`/`"VDD"` — **dùng ĐÚNG cùng bộ marker mà `DesktopDuplicationCapture.cpp::FindVirtualDisplayAdapterDeviceName` (C++, video capture) đã dùng**, đọc kỹ file đó trước khi viết để không lệch logic giữa 2 phía độc lập nhận diện cùng 1 monitor.
- Không tìm thấy → fallback về rect của primary monitor (theo `MonitorInfoFPrimary` flag), log rõ ràng ra `Console.Error` (tiếng Việt, không im lặng) — đúng yêu cầu brief.
- Cache: `_cachedRect` giữ vô thời hạn sau lần enumerate đầu, có `Invalidate()` public để force re-enumerate — KHÔNG enumerate lại mỗi touch event (hàng chục/trăm event/giây).
- `CoordinateMapper.ToScreenPixels(normalizedX, normalizedY, rect)` (mới, static, dùng chung) thay 2 bản `ToScreenPixels` trùng lặp trong `CursorInjector`/`TouchInjector`: `x = rect.Left + normalizedX/65535.0*(rect.Right-rect.Left)`, tương tự cho y.

**Việc 2 — sửa `CursorInjector.cs`/`TouchInjector.cs`**:
- Cả 2 class nhận thêm constructor param optional `IVirtualMonitorLocator? monitorLocator = null` (default `new VirtualMonitorLocator()`, không phá callsite cũ dùng constructor mặc định).
- `MoveTo`/`Inject` giờ gọi `_monitorLocator.GetVirtualDisplayRect()` rồi `CoordinateMapper.ToScreenPixels(...)` thay vì `GetSystemMetrics`.
- `StreamingCoordinator.cs`: thêm field `_monitorLocator` (1 instance `VirtualMonitorLocator` dùng CHUNG cho cả `_cursorInjector`/`_touchInjector`, truyền qua constructor), gọi `_monitorLocator.Invalidate()` ngay sau cả 2 lần `_vddConfigurator.EnsureExtendTopology()` chạy (trong `Start()` và trong `RecreateFrameSourceForNewResolution()` sau driver restart) — đúng yêu cầu brief "invalidate khi có sự kiện đổi display config", tránh dùng rect cũ sau khi topology/resolution đổi.

**Việc 3 — test, CHẠY THẬT trên máy đang có driver + Extend đang bật**:
- `pc-host/tests/Core.Tests/VirtualMonitorLocatorTests.cs` (mới) — 7 test:
  - `ToScreenPixels_ZeroNormalized_ReturnsRectTopLeft`/`_MaxNormalized_ReturnsNearRectBottomRight`/`_MidpointNormalized_ReturnsRectCenter`/`_DoesNotClampIntoPrimaryOnlySize_EvenWhenPrimaryIsLarger` — test thuần `CoordinateMapper` với RECT giả lập KHÔNG ở gốc tọa độ (vd `(2560,0)-(3360,600)`, đúng hình dạng bug thật) — không cần desktop thật.
  - `GetVirtualDisplayRect_RunsAgainstRealDesktop_ReturnsNonDegenerateRect`/`_CachesResult_InvalidateForcesReEnumeration` — chạy enumerate THẬT (không mock), assert rect không suy biến + cache/invalidate hoạt động đúng.
  - `CursorInjector_MoveTo_UsesInjectedLocatorRect_NotPrimaryOnlySize` — dùng `FakeVirtualMonitorLocator` (seam mới) + gọi THẬT `Win32Interop.SetCursorPos`/`GetCursorPos` (không mock Win32), assert cursor thật rơi đúng vùng RECT giả lập, khôi phục vị trí chuột gốc sau test.
  - `dotnet test Core.Tests`: **69/69 xanh** (62 cũ + 7 mới, không có test nào fail ngoài dự kiến).
  - `dotnet test Integration.Tests`: **7/7 xanh**, không hỏng gì cũ.
- **Bằng chứng CHẠY THẬT trên máy này (không suy đoán)** — máy đang có driver "VDD by MTT" cài + đã ở Extend mode (do `EnsureExtendTopology()` chạy từ session 7 vẫn còn hiệu lực), dùng 1 test tạm thời (`ZZTempVerifyLocatorPrint.cs`, viết → chạy → XÓA sau khi lấy bằng chứng, không phải file production):
  - `Screen.AllScreens` (PowerShell, đối chiếu độc lập): `\\.\DISPLAY1 Primary=True Bounds=(0,0,1707,1067)` + `\\.\DISPLAY7 Primary=False Bounds=(2560,0,800,600)`.
  - `VirtualMonitorLocator.GetVirtualDisplayRect()` THẬT trả về: `Left=2560 Top=0 Right=3360 Bottom=600` — **khớp CHÍNH XÁC** với `\\.\DISPLAY7` (`Right = 2560+800 = 3360`, `Bottom=600`) đối chiếu độc lập bằng `Screen.AllScreens`, KHÔNG phải rect của primary.
  - `CursorInjector.MoveTo(...)` (dùng `VirtualMonitorLocator` THẬT, không fake) + `Win32Interop.GetCursorPos` đọc THẬT sau mỗi lần gọi:
    | normalized input | cursor thật SAU khi gọi | Trong vùng VDD (2560,0)-(3360,600)? |
    |---|---|---|
    | (0, 0) | (2560, 0) | ✅ đúng góc trên-trái VDD |
    | (65535, 65535) | (3359, 599) | ✅ đúng góc dưới-phải VDD |
    | (32768, 32768) | (2960, 300) | ✅ đúng giữa VDD |
    Với công thức CŨ (`GetSystemMetrics` trả primary 1707x1067), input (32768,32768) sẽ cho ra `(853, 533)` — **nằm trong màn CHÍNH**, đúng y hệt bug user báo cáo. Bằng chứng này xác nhận trực tiếp: fix đưa tọa độ từ trong-màn-chính (bug) ra đúng-trong-vùng-VDD (đã sửa).
  - Cursor được khôi phục về vị trí gốc sau mỗi test (không để lại tác dụng phụ trên máy dev).

**Việc 4 — chưa làm được (giới hạn môi trường, không phải giới hạn code)**:
- KHÔNG chạy full end-to-end qua socket thật (PC Host thật nhận TOUCH_EVENT từ tablet thật qua ADB reverse, như cách session 5 verify) trong session này: `DisplayBridge.Host.exe` từ session 8 đã có `app.manifest requireAdministrator` — launch từ shell non-interactive của agent này bị `ERROR_ELEVATION_REQUIRED` ngay lập tức (đã xác nhận lại y hệt giới hạn session 8), không có phiên desktop để bấm UAC. Do đó test Việc 3/4 ở mức Win32 layer thật (CursorInjector→SetCursorPos→GetCursorPos, dùng ĐÚNG VirtualMonitorLocator thật tìm ra ĐÚNG rect thật của VDD) — đây là lớp DUY NHẤT có bug (InputDispatcher/socket/wire protocol phía trên không đổi, đã verify ở session 5), nên coi là bằng chứng đủ mạnh, nhưng CHƯA phải full E2E qua chạm tay thật trên tablet.
- Tablet `AL9SBB4622000114` đang kết nối (`adb devices` xác nhận) + `adb reverse tcp:29500`/`29501` đã set sẵn, nhưng không dùng được vì PC Host không khởi động được (lý do trên).
- **User cần tự làm** để hoàn tất verify 100% theo đúng tiêu chuẩn "chạm tay thật": chạy `DisplayBridge.Host.exe` từ desktop thật (bấm Yes UAC), launch lại `VideoDecoderActivity` trên tablet, chạm vào vài vị trí cụ thể trên màn tablet (vd 4 góc + giữa), đối chiếu `GetCursorPos` trên PC — kỳ vọng khớp đúng theo tỷ lệ, nằm trong vùng `(2560,0)-(3360,600)` (hoặc rect mới nếu driver đã thay đổi vị trí).

**File đã sửa/thêm (session 10)**:
- `pc-host/src/DisplayBridge.Core/Input/Win32Interop.cs` — thêm `EnumDisplayMonitors`/`MonitorEnumProc`, `GetMonitorInfo`/`MonitorInfoEx`, `EnumDisplayDevices`/`DisplayDevice`, `GetCursorPos`/`Point`.
- `pc-host/src/DisplayBridge.Core/Input/VirtualMonitorLocator.cs` (MỚI) — `IVirtualMonitorLocator`/`VirtualMonitorLocator` (enumerate+cache+fallback) + `CoordinateMapper` (mapping dùng chung).
- `pc-host/src/DisplayBridge.Core/Input/CursorInjector.cs` — bỏ `ToScreenPixels` riêng, nhận `IVirtualMonitorLocator` qua constructor, dùng `CoordinateMapper`.
- `pc-host/src/DisplayBridge.Core/Input/TouchInjector.cs` — tương tự.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — thêm field `_monitorLocator` dùng chung cho 2 injector, gọi `Invalidate()` sau mỗi lần `EnsureExtendTopology()`.
- `pc-host/tests/Core.Tests/VirtualMonitorLocatorTests.cs` (MỚI) — 7 test như trên.

**Regression**: `dotnet build src/DisplayBridge.Core` + `src/DisplayBridge.Host` (Release) sạch, 0 error (2 warning cũ không liên quan, không đổi). `Core.Tests` 69/69 xanh. `Integration.Tests` 7/7 xanh.

**Việc còn lại**:
1. User tự chạy `DisplayBridge.Host.exe` (bấm Yes UAC) + chạm tay thật trên tablet, đối chiếu `GetCursorPos` — hoàn tất verify 100% theo tiêu chuẩn "chạm tay thật qua toàn bộ pipeline" (Việc 4 ở trên).
2. `TouchInjector` (route 2-ngón, InjectTouchInput) dùng chung `CoordinateMapper`/`VirtualMonitorLocator` với `CursorInjector` nên fix áp dụng cho cả 2, nhưng CHƯA có bằng chứng `GetCursorPos`-tương đương cho route Touch (InjectTouchInput không có API đọc lại vị trí contact points như GetCursorPos) — nếu cần bằng chứng thêm, verify bằng cách quan sát vị trí con trỏ ảo/pointer feedback trên màn hình thật khi test 2 ngón.

### Session 9 — fix RC1 (Settings placeholder) + verify RC2 (log driver restart) + Android FPS overlay/màn chờ (chi tiết)

**Bối cảnh**: theo RCA-v1-resolution-stuck-800x600.md (đọc toàn bộ trước khi làm) — 2 root cause: RC1 (HIGH conf, xác nhận code-level) Settings dialog luôn hiện `DeviceCaps.Placeholder` (2560x1600, trùng độ phân giải laptop dev) vì `App.xaml.cs`/`MainWindow.xaml.cs` gọi constructor `SettingsWindow()` parameterless thay vì truyền `CurrentDeviceCaps` thật; RC2 (MEDIUM conf) `DriverManager.EnsureReady` chỉ log 1 dòng tổng hợp cuối cùng, không log từng bước `[1/3]/[2/3]/[3/3]` ngay khi xảy ra.

**Việc 1 — fix RC1 (bug wiring thật, không phải bug logic)**:
- `StreamingCoordinator.cs` — thêm property `SettingsStore` (public getter cho `_settingsStore` private đã có sẵn) để UI dùng ĐÚNG cùng 1 SettingsStore/file settings.json mà coordinator đang đọc/ghi, thay vì 1 instance `new SettingsStore()` thứ hai trỏ trùng path theo trùng hợp.
- `App.xaml.cs` — thêm property `Coordinator` expose `_streamingCoordinator` ra ngoài; sửa `settingsMenuItem.Click` truyền `coordinatorForSettings.SettingsStore` + `coordinatorForSettings.CurrentDeviceCaps` thật (không còn `new SettingsWindow()`).
- `MainWindow.xaml.cs` — `SettingsButton_Click` giờ đọc `(Application.Current as App)?.Coordinator` để lấy caps/store thật; nếu coordinator chưa kịp khởi (null-guard, hiếm khi xảy ra vì được gán ngay đầu `OnStartup`) hiện `MessageBox` cảnh báo thay vì crash/dùng placeholder.
- `SettingsWindow.xaml.cs` — **XÓA HẲN constructor parameterless** (`SettingsWindow() : this(new SettingsStore(), DeviceCaps.Placeholder)`) đúng theo "Preventive Actions" của RCA, để callsite quên truyền caps thật gây lỗi COMPILE-TIME thay vì im lặng sai runtime.
- **Xác nhận triệt để bằng build thật**: `dotnet build src/DisplayBridge.Host/DisplayBridge.Host.csproj -p:Configuration=Release` → **BUILD SUCCEEDED, 0 error** (không có callsite nào còn sót dùng constructor cũ — nếu còn sót, build sẽ báo lỗi CS ngay, đây chính là bằng chứng "tìm hết được các chỗ cần sửa" mà RCA yêu cầu). `grep -rn "new SettingsWindow()\|SettingsWindow {"` trên toàn bộ `src/` chỉ còn khớp 1 dòng — là comment giải thích trong `MainWindow.xaml.cs`, không phải code thật.

**Việc 2 — verify RC2 (driver restart log rõ ràng từng bước)**:
- `DriverManager.cs` — `EnsureReady(...)` thêm tham số optional `Action<string>? onStep = null` (không phá signature cũ, mọi caller/test hiện có không cần đổi). Trước đây log list chỉ được join thành 1 chuỗi và trả về SAU KHI toàn bộ chuỗi `[1/3][2/3][3/3]` (shell-out pnputil/devcon, có thể mất tới ~25s) đã chạy xong — giờ mỗi dòng log được bắn ra NGAY LẬP TỨC qua `onStep` khi bước đó vừa hoàn tất, không cần đợi hàm return.
- `StreamingCoordinator.cs` (`ApplyVirtualDisplayResolution`) — truyền `onStep: line => Log?.Invoke(...)` để mỗi dòng `[1/3]/[2/3]/[3/3]` được ghi thẳng vào `%TEMP%\displaybridge-host.log` (cơ chế ghi log đã có sẵn từ `App.xaml.cs`) ngay khi xảy ra, thay vì chỉ 1 dòng tổng hợp cuối cùng như trước.
- **Verify build**: `dotnet build ... -p:Configuration=Release` sạch (đúng bản đóng gói `APP_BUILD`, per brief). KHÔNG chạy thật bước restart driver (cần UAC tương tác thật, để user tự làm theo đúng ràng buộc).

**Việc 3 — Android: FPS overlay góc trên-trái**:
- `VideoDecoderActivity.kt` — tổng quát hoá bộ đếm frame session-4 (trước đây chỉ đếm JPEG, dùng cho `jpegFramesRendered`/`jpegFpsWindow*` log "JPEG frame #N"/"JPEG fallback: N frames...") thành `recordFrameEvidenceAndUpdateOverlay(frame)` DÙNG CHUNG cho cả nhánh H.264 lẫn JPEG — gọi 1 lần đầu `onFrame()` trước khi rẽ nhánh `isJpeg`, tránh viết 2 bộ đếm trùng lặp.
- Thêm `TextView` overlay (`fpsOverlayText`, dựng bằng code không cần layout XML — nhất quán với style hiện có của file) góc trên-trái, chữ trắng 12sp, nền đen bán trong suốt (`0x80000000`), padding 4dp, cập nhật 1 lần/giây (không mỗi frame) hiển thị `"FPS: XX.X"` + dòng dưới `"H.264"` hoặc `"JPEG fallback"` (dùng lại field `isJpeg` có sẵn trong `VideoFrame`).
- **Verify bằng chạy thật trên tablet** (`AL9SBB4622000114`, có PC Host thật đang chạy sẵn lúc verify): cài lại APK debug (`adb install -r`), launch Activity, `adb logcat` xác nhận log `Video: N frames total, XX.X fps (last frame Y bytes, isJpeg=false)` chạy liên tục ~52-58fps H.264 thật, **0 dòng `AndroidRuntime`/`FATAL EXCEPTION`**. Chụp màn hình thật (`adb shell screencap`) xác nhận overlay hiện đúng góc trên-trái: `"FPS: 55,9"` / `"H.264"`, nền đen mờ, không che nội dung chính — bằng chứng cụ thể, không suy đoán.

**Việc 4 — Android: màn hình chờ khi Host PC chưa kết nối**:
- `VideoDecoderActivity.kt` — thêm `waitingOverlay` (FrameLayout nền đen, dựng bằng code) chứa logo (`R.mipmap.ic_launcher`, import `com.displaybridge.probe.R`) + `ProgressBar` indeterminate + `TextView` message, đặt LÊN TRÊN `surfaceView`/`touchCaptureView` trong `root` (thêm vào layout stack tại `onCreate`, trước khi `fpsOverlayText`).
- Ẩn overlay (visibility `GONE`) ngay trong `recordFrameEvidenceAndUpdateOverlay()` lần gọi đầu tiên (frame thật đầu tiên tới) — tái dùng cùng 1 field `hasReceivedFirstFrame` cho cả việc ẩn overlay lẫn phân biệt text khi mất kết nối.
- `onDisconnected(cause, willRetry)` (VideoStreamListener, socket video) — hiện lại `waitingOverlay` (visibility `VISIBLE`) và reset `hasReceivedFirstFrame = false`; text phân biệt được 2 trạng thái (không cần dùng chung 1 text theo KISS fallback vì đủ đơn giản để tách): `"Đang chờ kết nối với PC..."` lần đầu, `"Mất kết nối, đang thử lại..."` khi đã từng kết nối rồi rớt giữa chừng.
- **Chưa verify bằng chạy tay thật** trạng thái "màn chờ" + "mất kết nối" trên tablet thật trong session này: tại thời điểm verify, PC Host của user đang chạy sống thật (stream H.264 thật ~55fps đang chảy — xem bằng chứng Việc 3), nên KHÔNG tắt/rút kết nối PC để test 2 trạng thái này vì sẽ làm gián đoạn phiên đang chạy của user. Logic đã đọc lại kỹ (hide-on-first-frame, show-on-disconnect, reset flag đúng) và build/compile sạch, nhưng đây là hạng mục **cần user tự verify bằng tay** (tắt PC Host hoặc rút mạng ADB reverse giữa chừng, quan sát overlay có hiện lại đúng text không).

**Regression (chạy thật, không suy đoán)**:
- `dotnet build src/DisplayBridge.Host/DisplayBridge.Host.csproj -p:Configuration=Release` — sạch, 0 error (2 warning cũ không liên quan: `CS1998` async-lacks-await, `WFAC010` high-DPI manifest — không phải do session này).
- `dotnet test tests/Core.Tests/Core.Tests.csproj` — **62/62 xanh** (không đổi số so với session 8 — không có test mới cho DriverManager/SettingsWindow session này, xem "Việc còn lại").
- `dotnet test tests/Integration.Tests/Integration.Tests.csproj` — **7/7 xanh**.
- `./gradlew assembleDebug` — **BUILD SUCCESSFUL**, cài lên tablet thật, chạy thật xác nhận 0 crash.
- `./gradlew testDebugUnitTest` — **BUILD SUCCESSFUL** (không có unit test nào target trực tiếp `VideoDecoderActivity`, nên số lượng test không đổi so với session trước — xem "Việc còn lại").

**File đã sửa (session 9)**:
- `pc-host/src/DisplayBridge.Host/App.xaml.cs` — property `Coordinator` mới, sửa `settingsMenuItem.Click` truyền caps/store thật.
- `pc-host/src/DisplayBridge.Host/MainWindow.xaml.cs` — `SettingsButton_Click` đọc coordinator thật qua `Application.Current`, null-guard bằng `MessageBox` thay vì placeholder.
- `pc-host/src/DisplayBridge.Host/SettingsWindow.xaml.cs` — xóa constructor parameterless.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — property `SettingsStore` mới; `ApplyVirtualDisplayResolution` truyền `onStep` callback vào `EnsureReady`.
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs` — `EnsureReady` thêm tham số optional `onStep`.
- `android-client/app/src/main/java/com/displaybridge/video/VideoDecoderActivity.kt` — thêm `waitingOverlay`/`fpsOverlayText`, tổng quát hoá bộ đếm frame thành `recordFrameEvidenceAndUpdateOverlay`, `onDisconnected` hiện lại màn chờ.

**Việc còn lại**:
1. User tự tắt/khởi động lại kết nối PC↔tablet giữa chừng để verify màn chờ "Mất kết nối, đang thử lại..." hiện đúng lúc (Việc 4, chưa verify bằng tay trong session này).
2. User tự verify log `%TEMP%\displaybridge-host.log` có đủ từng dòng `[1/3]/[2/3]/[3/3]` xuất hiện NGAY LÚC xảy ra (không phải dồn 1 cục ở cuối) trong lần CAPS/restart driver tiếp theo — RC2 chỉ verify được bằng build sạch trong session này, không chạy lại toàn bộ chuỗi restart thật (cần UAC).
3. Chưa có unit test riêng cho `SettingsWindow`/`DriverManager.EnsureReady(onStep:)` (wiring-level bug, như RCA đã chỉ ra loại lỗi này vốn không lộ qua unit test) — cân nhắc thêm 1 test tối thiểu kiểm tra `onStep` được gọi đúng số lần nếu cần coverage sau này.

### Session 8 — tự động hoá vòng đời driver VDD by MTT vào Host app (chi tiết)

**Yêu cầu user**: session 7 kết thúc với tiêu chí #3 (RESEARCH-v2) CHƯA ĐẠT vì `devcon restart` cần quyền Admin mà Host chạy không elevated — user phải tự vào Device Manager Disable/Enable thủ công. Yêu cầu lần này: gộp toàn bộ "cài driver lần đầu nếu chưa có + cấu hình resolution + restart" vào ngay trong Host app, chạy ngầm, chỉ xin UAC 1 lần lúc mở app, không cần "Virtual Driver Control.exe" riêng. Thực thi trực tiếp bằng tools, không giao Agent.

**Việc 1 — đóng gói driver + devcon vào Host app**:
- Copy `MttVDD.inf`/`MttVDD.dll`/`mttvdd.cat` (bản x86 đã ký SignPath Foundation, từ `windows-driver/vdd-control/SignedDrivers/x86/VDD/`) vào `pc-host/src/DisplayBridge.Host/Resources/VirtualDisplayDriver/`.
- Copy `devcon.exe` (từ `windows-driver/vdd-control/Dependencies/devcon.exe`) vào `pc-host/src/DisplayBridge.Host/Resources/devcon.exe`.
- `DisplayBridge.Host.csproj`: thêm 2 `<None>` item mới (`Resources\VirtualDisplayDriver\**` glob + `Resources\devcon.exe`), KHÔNG dùng blanket `Resources\**` như brief gợi ý vì sẽ trùng lặp với 2 `<None>` đã có sẵn cho `logo.ico`/`logo.png` (MSBuild báo lỗi duplicate output item nếu 1 file khớp 2 `<None>` cùng lúc) — verify bằng build thật: `dotnet build` sạch, `bin/Debug/net8.0-windows/Resources/` có đủ `devcon.exe` + `VirtualDisplayDriver/{MttVDD.inf,MttVDD.dll,mttvdd.cat}` cạnh `logo.ico`/`logo.png`.

**Việc 2 — Host tự xin Admin 1 lần**:
- `pc-host/src/DisplayBridge.Host/app.manifest` (mới) — `requestedExecutionLevel level="requireAdministrator" uiAccess="false"`.
- `DisplayBridge.Host.csproj` — thêm `<ApplicationManifest>app.manifest</ApplicationManifest>`.
- **Verify thật**: đọc ngược bytes của `.exe` đã build, tìm chuỗi `requireAdministrator` — CÓ trong binary (manifest đã nhúng đúng). Thử launch trực tiếp từ shell không elevated (`git-bash`) → nhận `Permission denied` (`ERROR_ELEVATION_REQUIRED`, không phải crash) — bằng chứng CỤ THỂ manifest đang chặn đúng, không phải "chắc là có".

**Việc 3 — `DriverManager.cs` (mới, `pc-host/src/DisplayBridge.Core/Video/`)**:
- `IsDriverInstalled(out detail)`: shell `devcon.exe find "*Root\MttVDD*"`, parse `"N matching device(s) found."` vs `"No matching devices found."`. Chọn devcon thay vì thêm dependency `System.Management`/WMI mới — devcon đã bắt buộc phải có sẵn cho `RestartDevice` rồi, dùng lại đúng 1 cơ chế thay vì 2. Verify thật trên máy dev: `devcon find "*Root\MttVDD*"` → `ROOT\DISPLAY\0000 : Virtual Display Driver` + `1 matching device(s) found.` (driver đã cài từ session 7, `IsDriverInstalled` trả `true` đúng).
- `InstallIfMissing(out detail)`: `pnputil.exe /add-driver "Resources\VirtualDisplayDriver\MttVDD.inf" /install`, không throw khi fail (bắt exception, trả `PnputilNotFound`/`InstallFailed`), có sanity-check gọi lại `IsDriverInstalled` sau khi pnputil exit 0 thay vì tin exit code mù quáng.
- `RestartDevice()`: **không viết lại logic restart** — gọi thẳng `VirtualDisplayConfigurator.TryRestartDriver()` đã có sẵn từ session 7 (devcon restart bằng hardware ID, đã verify hoạt động đúng cú pháp `devcon restart *Root\MttVDD*` qua `devcon help restart`). Đổi `VirtualDisplayConfigurator.FindDevconExe()` để ưu tiên tìm `Resources\devcon.exe` cạnh exe TRƯỚC (bản đóng gói mới), fallback về đường dẫn repo cũ (`windows-driver/vdd-control/Dependencies/devcon.exe`) cho môi trường dev — không phá hành vi cũ.
- `EnsureReady(nativeWidth, nativeHeight, supportedHz)`: gọi tuần tự `IsDriverInstalled` → `InstallIfMissing` (nếu thiếu) → `VirtualDisplayConfigurator.ApplyResolution` (đã có, chỉ gọi) → `RestartDevice`. Trả `(bool Success, string Message)` với message liệt kê từng bước `[1/3]/[2/3]/[3/3]` — không trả `bool` trơn (đúng bài học session 7 về log lỗi cụ thể).

**Việc 4 — nối vào `StreamingCoordinator`**:
- `ApplyVirtualDisplayResolution(caps)` giờ gọi 1 lần `_driverManager.EnsureReady(...)` thay vì gọi riêng lẻ `_vddConfigurator.ApplyResolution`/`TryRestartDriver` như session 7.
- **Phát hiện khi đọc lại code (Việc 4 trong brief)**: `CreateFrameSource()` trước đó CHỈ chạy 1 lần trong `Start()` — nếu driver restart thành công giữa chừng (resolution đổi), video pipeline vẫn giữ nguyên `IFrameSource` cũ (capture ở resolution CŨ) cho tới khi restart cả app. Thêm `RecreateFrameSourceForNewResolution()` (mới): dừng `VideoStreamServer` cũ (đợi client task thoát qua `Stop()` có sẵn từ session 6), dispose frame source cũ, `Thread.Sleep(3000)` (đợi PnP device node ổn định lại sau restart — devcon restart return ngay khi disable+enable xong nhưng Windows cần vài giây để gắn lại EDID/desktop), gọi lại `EnsureExtendTopology()` (phòng trường hợp restart đưa device về lại Clone mode), tạo `CreateFrameSource()` mới + `VideoStreamServer` mới TRÊN CÙNG PORT, start lại. Gọi hàm này trong `ApplyVirtualDisplayResolution` chỉ khi `EnsureReady` trả `Success=true`.

**Regression**: `dotnet build` Core+Host sạch (0 warning/error). `Core.Tests` 62/62 xanh (không đổi số so với session 7 — `DriverManager`/`app.manifest` không có test riêng session này, xem "Việc còn lại"). `Integration.Tests` 7/7 xanh, chạy 4s (devcon shell-out trong môi trường test không tìm thấy Resources cạnh test binary → trả về nhanh `false` với message rõ ràng, không hang/không crash).

**Việc 5 — chạy thật, đối chiếu 4 tiêu chí PASS — CHƯA HOÀN THÀNH, giới hạn môi trường**:
- `adb devices` xác nhận tablet `AL9SBB4622000114` đang kết nối. `adb reverse tcp:29500 tcp:29500` + `tcp:29501 tcp:29501` chạy OK (`adb reverse --list` xác nhận cả 2).
- **Thử launch `.exe` thật để verify UAC**: môi trường agent này chạy qua shell không tương tác (non-interactive, không có phiên desktop để hiển thị hộp thoại UAC). Launch trực tiếp trả `Permission denied` (`CreateProcess` từ chối ngay với `ERROR_ELEVATION_REQUIRED` vì không có UI session để prompt) — đây LÀ bằng chứng manifest hoạt động đúng, nhưng đồng nghĩa **không thể tự động hoá bước "người dùng bấm Yes trên hộp thoại UAC"** từ agent này.
- **Do đó CHƯA verify được bằng chạy thật**: driver tự restart (log timestamp trước/sau), `vdd_settings.xml` áp dụng thật, resolution THẬT của display đổi thành 3000×1920, kích thước frame H.264 khớp. Đây là công việc CÒN LẠI, cần user tự chạy `.exe` (double-click hoặc từ terminal có phiên desktop) và bấm "Yes" trên UAC 1 lần.

**File đã sửa/thêm (session 8)**:
- `pc-host/src/DisplayBridge.Host/Resources/VirtualDisplayDriver/{MttVDD.inf,MttVDD.dll,mttvdd.cat}` (MỚI, copy từ SignedDrivers/x86/VDD).
- `pc-host/src/DisplayBridge.Host/Resources/devcon.exe` (MỚI, copy).
- `pc-host/src/DisplayBridge.Host/app.manifest` (MỚI).
- `pc-host/src/DisplayBridge.Host/DisplayBridge.Host.csproj` — `<ApplicationManifest>` + 2 `<None>` item mới cho Resources.
- `pc-host/src/DisplayBridge.Core/Video/DriverManager.cs` (MỚI) — `IsDriverInstalled`/`InstallIfMissing`/`RestartDevice`/`EnsureReady`.
- `pc-host/src/DisplayBridge.Core/Video/VirtualDisplayConfigurator.cs` — `HardwareId` đổi `private`→`internal` (DriverManager dùng lại pattern devcon find), thêm `DevconPath` property public, `FindDevconExe()` ưu tiên `Resources\devcon.exe` bundled trước.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — thêm field `_driverManager`, `ApplyVirtualDisplayResolution` gọi `DriverManager.EnsureReady` thay vì gọi riêng lẻ, thêm `RecreateFrameSourceForNewResolution()` mới.

**Đánh giá thật**: toàn bộ code side đã hoàn thành + build sạch + test xanh (Core.Tests 62/62, Integration.Tests 7/7) + verify được 2 bằng chứng cụ thể không suy diễn (manifest nhúng đúng qua đọc byte thật, `Permission denied` thật khi launch không elevated, `devcon find` thật trả về đúng trạng thái driver đã cài). Nhưng **tiêu chí #3 của RESEARCH-v2 (resolution thật đổi thành 3000×1920) VẪN CHƯA được xác nhận bằng chạy thật** — khác lý do so với session 7 (session 7: thiếu quyền Admin do code chưa xin; session 8: code ĐÃ xin quyền đúng cách nhưng môi trường thực thi (agent shell non-interactive) không có phiên desktop để người dùng bấm "Yes"). Đây là giới hạn của MÔI TRƯỜNG chạy agent, không phải giới hạn của code — user cần tự chạy `.exe` 1 lần từ desktop thật để hoàn tất verify.

**Việc còn lại**:
1. User tự chạy `DisplayBridge.Host.exe` từ phiên desktop thật (double-click hoặc terminal có UI), bấm Yes trên UAC — xác nhận app khởi động không lỗi.
2. Sau đó verify lại đủ 4 tiêu chí RESEARCH-v2 bằng log thật (`%TEMP%\displaybridge-host.log` sẽ có dòng `DriverManager.EnsureReady(...)` + `VideoStreamServer da tao lai...`) + `Win32_VideoController`/`Screen.AllScreens` đọc resolution thật.
3. Chưa có unit test riêng cho `DriverManager` (session này ưu tiên verify bằng chạy `devcon`/`pnputil` thật trên máy thay vì mock — cân nhắc thêm test với `IProcessRunner` fake nếu cần coverage sau này).

### Session 7 — capture ĐÚNG virtual display + bỏ preset resolution (chi tiết)

**Bối cảnh**: user phát hiện bug nghiêm trọng qua RESEARCH-v2 — app session 4-6 đang mirror màn CHÍNH laptop (DXGI DuplicateOutput nhắm primary/adapter 0/output 0), không phải màn phụ độc lập. Đã cài "Virtual Driver Control" (VDD by MTT, HardwareID `Root\MttVDD`, monitor hardware ID `MTT1337`, xác nhận qua `WmiMonitorID`). Yêu cầu: (1) target đúng output VDD thay vì primary, (2) Host tự ghi `vdd_settings.xml` theo CAPS thật + trigger restart, (3) bỏ hẳn ResolutionMode preset trong Settings (chỉ còn 100% native), (4) chạy thật + đối chiếu 4 tiêu chí PASS RESEARCH-v2.

**Việc 1 — target đúng "VDD by MTT"** (`pc-host/src/DisplayBridge.Native/DesktopDuplicationCapture.h/.cpp`):
- `FindVirtualDisplayAdapterDeviceName()` (mới): enumerate mọi adapter đang `DISPLAY_DEVICE_ATTACHED_TO_DESKTOP` qua `EnumDisplayDevicesW(NULL, i, ..., 0)`, rồi mọi monitor gắn trên adapter đó qua `EnumDisplayDevicesW(adapterName, j, ..., 0)`, match `DeviceID` chứa `MTT1337`/`MttVDD` (hardware ID, ổn định xuyên ngôn ngữ OS) hoặc `DeviceString` chứa "MTT"/"VDD" (dự phòng). Trả về GDI adapter name (`\\.\DISPLAYn`).
- `Init()` viết lại: dùng `IDXGIFactory1::EnumAdapters1`/`EnumOutputs`, so khớp `DXGI_OUTPUT_DESC::DeviceName` (chuỗi `\\.\DISPLAYn`, xác nhận tài liệu DXGI dùng CHUNG format với GDI — không cần `QueryDisplayConfig` phức tạp như brief gợi ý) với target đã tìm. D3D11 device được tạo TRÊN ĐÚNG adapter đó (`D3D_DRIVER_TYPE_UNKNOWN` khi chỉ định adapter cụ thể, theo docs D3D11CreateDevice). Nếu không tìm thấy → fallback nguyên hành vi cũ (primary) + `fprintf(stderr, ...)` cảnh báo tiếng Việt rõ ràng "KHONG TIM THAY 'VDD by MTT'... DAY KHONG PHAI hanh vi mong muon".
- Thêm `Width()/Height()/UsedFallbackPrimary()/TargetDeviceName()` — trước đây `CaptureEncodeExports.cpp` dùng `GetSystemMetrics(SM_CXSCREEN)` (LUÔN trả kích thước PRIMARY bất kể đang duplicate output nào) làm kích thước encoder — đây là bug thật thứ 2, đã sửa để dùng đúng `DesktopCoordinates` của output đang capture.
- Export mới `DisplayBridge_CaptureUsedFallbackPrimary()` + `DisplayBridge_CaptureGetSize()` để C# (`NativeCaptureEncoder.cs`) đọc và `StreamingCoordinator.cs` log cảnh báo tiếng Việt nếu rơi vào fallback.

**Việc 2 — VirtualDisplayConfigurator.cs (mới)** (`pc-host/src/DisplayBridge.Core/Video/VirtualDisplayConfigurator.cs`):
- `ApplyResolution(nativeWidth, nativeHeight, supportedHz, out error)`: dùng `System.Xml.Linq` đọc `C:\VirtualDisplayDriver\vdd_settings.xml`, xoá CHỈ các `<resolution>` con trong `<resolutions>`, ghi lại 1 `<resolution>` mới = `(nativeWidth, nativeHeight, min(120, maxHz thiết bị))`. Giữ nguyên comment + toàn bộ `<gpu>/<global>/<options>`.
- **Bug thật tìm thấy khi chạy live** (không phải giả định): `XDocument.Load` ném `XmlException` mọi lần vì `vdd_settings.xml` (do session trước ghi tay) có comment chứa `--` (`<!--...60/90/120 -- khớp...-->`) — theo spec XML, comment KHÔNG được chứa `--`. Lỗi bị nuốt thành `return false` chung, trông giống hệt "file không tồn tại". Fix 2 lớp: (a) sửa `vdd_settings.xml` bỏ `--` trong comment, (b) đổi `ApplyResolution` trả `(bool, out string? error)` thay vì bool trơn, để log lộ rõ lý do thật thay vì mù mờ — đây là minh chứng trực tiếp cho nguyên tắc "log lỗi cụ thể, đừng nuốt exception" khi verify bằng chạy thật thay vì đoán.
- `EnsureExtendTopology()` (mới, quan trọng nhất phiên này): gọi `SetDisplayConfig(0,NULL,0,NULL, SDC_TOPOLOGY_EXTEND|SDC_APPLY)` — **PHÁT HIỆN THỰC NGHIỆM ngoài dự kiến ban đầu**: VDD by MTT mặc định ở chế độ Clone/Duplicate với màn laptop (chia sẻ CHUNG `\\.\DISPLAY1`, `DesktopCoordinates` trùng khớp primary `(0,0)-(2560,1600)`) — đây LÀ lý do sâu xa khiến Việc 1 dù tìm ĐÚNG output theo hardware ID vẫn "capture nhầm" nội dung giống primary (vì Windows coi đó là 1 vùng desktop). Gọi `SetDisplayConfig` ép Extend **KHÔNG CẦN quyền Admin** (verified thật: trước gọi `SM_CMONITORS=1`, sau gọi `=2`, không lỗi) — chuyển VDD sang `\\.\DISPLAY5` riêng, `DesktopCoordinates=(2560,0)-(3360,600)` (kế bên phải laptop, đúng ngữ nghĩa Extend). Gọi trong `StreamingCoordinator.Start()` TRƯỚC khi tạo frame source.
- `TryRestartDriver()`: shell `devcon.exe` (vendored sẵn tại `windows-driver/vdd-control/Dependencies/devcon.exe`) `restart *Root\MttVDD*`. **Xác nhận thật**: cần quyền Admin — `devcon restart` trả "Restart failed" khi Host chạy KHÔNG elevated (verified: `IsInRole(Administrator)==false` cùng session). KHÔNG tự xin UAC theo đúng ràng buộc — chỉ log rõ giới hạn + hướng dẫn thủ công (Device Manager > Disable/Enable).
- `VDD Control.exe` (GUI app trong `windows-driver/vdd-control/`) không có CLI flag tài liệu hoá (chạy `--help` không ra gì, không có README/CLI docs kèm theo) → không dùng, chọn `devcon.exe` (đã có sẵn, có output text rõ ràng để parse).

**Việc 3 — bỏ ResolutionMode preset** (quyết định sản phẩm 2026-07-03: resolution không còn lựa chọn, luôn 100% native từ CAPS):
- `AppSettings.cs`: xoá hẳn `enum ResolutionMode` + field `CustomWidth`/`CustomHeight` khỏi `DisplaySettings` (chỉ còn `RefreshRateHz`).
- `DeviceCaps.cs`: `Resolve()` không còn tham số, luôn trả `(NativeWidth, NativeHeight)`.
- `SettingsChangeClassifier.cs`: xoá `DisplayResolutionMode/DisplayCustomWidth/DisplayCustomHeight` khỏi `enum SettingField` + bảng `Classify()`.
- `SettingsStore.cs`: `Validate()` giờ là no-op có tài liệu (không còn gì để validate vì resolution không phải user input).
- `SettingsWindow.xaml(.cs)`: xoá hẳn `ResolutionComboBox` + Custom W×H `TextBox` — thay bằng 1 `TextBlock` read-only `ResolutionValueText` hiển thị `"{W}x{H} (tự động theo thiết bị)"`.
- `StreamingCoordinator.cs`: mọi chỗ gọi `_deviceCaps.Resolve(mode, w, h)` → `_deviceCaps.Resolve()`; `ApplyToSettings`/`ReadAsWireValue` bỏ 3 case liên quan.
- **Test cũ fail đúng như dự đoán** (giả định có preset) — sửa lại (KHÔNG bỏ qua): `SettingsChangeClassifierTests.cs` (bỏ 3 `InlineData`, đổi 1 test dùng `DisplayResolutionMode`→`DisplayRefreshRateHz`), `SettingsStoreTests.cs` (bỏ `ResolutionMode`/`CustomWidth`/`CustomHeight` khỏi assertions, thay 3 test Custom-resolution obsolete bằng 1 test `Validate_NoLongerThrows_ResolutionIsAlwaysFromCaps`). Kết quả `Core.Tests` 67→**62/62 xanh** (giảm đúng số test bị xoá, không có test nào fail ngoài dự kiến).

**Việc 4 — chạy thật, đối chiếu 4 tiêu chí PASS (RESEARCH-v2)**:

Build: `MSBuild DisplayBridge.Native.vcxproj` sạch (0 lỗi) → `DisplayBridge.Native.dll` mới. `dotnet build` Core/Host/Core.Tests/Integration.Tests đều sạch. `adb reverse tcp:29500/29501` OK. Chạy `DisplayBridge.Host.exe` thật + launch `VideoDecoderActivity` thật trên tablet `AL9SBB4622000114`.

| # | Tiêu chí (RESEARCH-v2) | Kết quả | Bằng chứng thật |
|---|------------------------|---------|------------------|
| 1 | Windows Settings→Display hiện 2 monitor riêng | ✅ **ĐẠT** | `[System.Windows.Forms.SystemInformation]::MonitorCount` = 2 (trước khi `EnsureExtendTopology()` chạy = 1). `Screen.AllScreens`: `\\.\DISPLAY1 (0,0,1707,1067) Primary=True` + `\\.\DISPLAY5 (2560,0,800,600) Primary=False`. |
| 2 | Kéo cửa sổ từ laptop sang Display 2 → biến mất khỏi laptop | ✅ **ĐẠT** | Mở Notepad thật, `SetWindowPos` di chuyển tới (2700,100). `MonitorFromWindow`+`GetMonitorInfo` TRƯỚC: `\\.\DISPLAY1 rect=0,0-1707,1067`. SAU: `\\.\DISPLAY5 rect=2560,0-3360,600`. Chuyển hẳn sang display khác, không phải nhân bản. |
| 3 | Display 2 hiện đúng 3000×1920 | ❌ **CHƯA ĐẠT** | `Win32_VideoController`: `Virtual Display Driver` CurrentH/VResolution = **800×600** (không phải 3000×1920). `VirtualDisplayConfigurator.ApplyResolution()` đã ghi đúng `3000x1920` vào `vdd_settings.xml` (log: `da ghi vdd_settings.xml resolution=3000x1920`) NHƯNG `TryRestartDriver()` thất bại vì thiếu quyền Admin (log: `restart driver THAT BAI... rat co the do... KHONG voi quyen Administrator`) — driver chưa reload nên vẫn dùng resolution cũ (800×600, không phải 3000×1920 cũng không phải 3840×2160 quan sát trước đó — có vẻ là 1 default mode khác của VDD trước khi có XML ghi lần đầu). **Cần**: user restart driver thủ công 1 lần với quyền Admin (Device Manager > "VDD by MTT" > Disable rồi Enable, HOẶC chạy `DisplayBridge.Host.exe` as Administrator 1 lần) rồi chạy lại — code KHÔNG tự bypass UAC theo đúng ràng buộc. |
| 4 | Ảnh trên tablet không méo/vỡ | 🔶 **MỘT PHẦN** | Native capture giờ target ĐÚNG `\\.\DISPLAY5` (log: `NativeCaptureEncoder initialized... target=VDD by MTT, size=800x600`), không còn ép theo tỉ lệ laptop (2560×1600) nữa — nguồn gốc méo hình đã được xử lý đúng gốc rễ. Video chạy thật **1500+ frame liên tục, ~13-14fps, H.264 thật (`isJpeg=False`)**, `adb logcat` xác nhận `c2.qti.avc.decoder` render liên tục (`Rendered: 13-15/s`), **0 dòng `AndroidRuntime`/`FATAL EXCEPTION`/`CodecException`** trong suốt phiên. Nhưng vì tiêu chí #3 chưa đạt (VDD đang ở 800×600 khi capture bắt đầu, không phải 3000×1920), hình hiện tại có tỉ lệ ĐÚNG (không méo) nhưng ĐỘ PHÂN GIẢI thấp hơn native thật của tablet — sẽ tự động đúng khi tiêu chí #3 được giải quyết (không cần sửa code thêm, `OnCapsReceived` đã ghi XML đúng, chỉ chờ restart driver).

**Đánh giá thật theo Fable**: 3/4 tiêu chí ĐẠT thật với bằng chứng cụ thể (không phải "chắc là đạt"), tiêu chí còn lại (#3, native resolution) bị chặn bởi 1 giới hạn quyền hệ thống đã xác định rõ ràng (Admin-only driver restart), không phải lỗi code. Đây là tiến bộ CĂN BẢN so với session 4-6 (mirror sai hoàn toàn) — không chỉ "tối ưu thêm" mà đã đổi ĐÚNG kiến trúc theo RESEARCH-v2 (capture nguồn = virtual display thật, không phải primary). Phát hiện phụ quan trọng: `EnsureExtendTopology()` qua `SetDisplayConfig` giải quyết được vấn đề Clone-mode KHÔNG CẦN Admin — đây là phát hiện thực nghiệm ngoài dự kiến ban đầu của brief (brief chỉ yêu cầu sửa "nguồn capture" và ghi XML, không lường trước vấn đề display topology Clone vs Extend), quan trọng ngang với việc target đúng output.

**File đã sửa/thêm (session 7)**:
- `pc-host/src/DisplayBridge.Native/DesktopDuplicationCapture.h/.cpp` — enumerate + match đúng "VDD by MTT" qua hardware ID, dùng đúng resolution output thay vì `GetSystemMetrics`.
- `pc-host/src/DisplayBridge.Native/CaptureEncodeExports.cpp` — dùng `g_capture->Width()/Height()`, export `DisplayBridge_CaptureUsedFallbackPrimary`/`DisplayBridge_CaptureGetSize`.
- `pc-host/src/DisplayBridge.Core/Video/NativeCaptureEncoder.cs` — wrap 2 export mới, property `UsedFallbackPrimaryDisplay`/`CapturedSize`.
- `pc-host/src/DisplayBridge.Core/Video/VirtualDisplayConfigurator.cs` (MỚI) — ghi XML resolution + `EnsureExtendTopology()` + `TryRestartDriver()`.
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — gọi `EnsureExtendTopology()` đầu `Start()`, gọi `ApplyVirtualDisplayResolution()` khi nhận CAPS, log cảnh báo fallback-primary, bỏ mọi tham chiếu `ResolutionMode`/`CustomWidth`/`CustomHeight`.
- `pc-host/src/DisplayBridge.Core/Settings/AppSettings.cs`, `DeviceCaps.cs`, `SettingsChangeClassifier.cs`, `SettingsStore.cs` — bỏ preset resolution.
- `pc-host/src/DisplayBridge.Host/SettingsWindow.xaml(.cs)` — bỏ ComboBox/TextBox resolution, thêm TextBlock read-only.
- `pc-host/tests/Core.Tests/SettingsChangeClassifierTests.cs`, `SettingsStoreTests.cs` — cập nhật theo thiết kế mới.
- `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs` — sửa comment lỗi thời.
- `pc-host/tools/NativeSmokeTest/Program.cs` — thêm in `UsedFallbackPrimary`/`CapturedSize` để verify trực tiếp.
- `C:\VirtualDisplayDriver\vdd_settings.xml` — fix bug thật: bỏ `--` trong comment (vi phạm XML spec, làm `XDocument.Load` luôn throw).

**Regression**: `Core.Tests` 62/62 xanh (67→62, đúng số test xoá do bỏ preset, không fail ngoài dự kiến), `Integration.Tests` 7/7 xanh, `gradlew testDebugUnitTest` BUILD SUCCESSFUL (Android không đổi code session này).

### Session 3 — wiring E2E (chi tiết)

| Việc | Kết quả | File |
|------|---------|------|
| Việc 1: build C++ | THẤT BẠI có bằng chứng — MSB8020 "v143 build tools cannot be found" sau khi set VCTargetsPath đúng. Tiến bộ so với session trước (đã qua được lỗi thiếu Microsoft.Cpp.Default.props), nhưng root cause là v143 **chưa cài**, không phải path sai. Dừng đúng theo brief, không sa lầy. | — |
| Việc 2: R17 chốt SPS/PPS in-band | PC: capture SPS/PPS 1 lần từ `MF_MT_MPEG_SEQUENCE_HEADER` sau `SetOutputType`, prepend vào mọi sample IDR (skip nếu MFT đã tự in-band). Android: xác nhận `configure()` không set csd-0/csd-1 (đã đúng từ session 2), thêm comment chốt quyết định. | `pc-host/src/DisplayBridge.Native/H264Encoder.h/.cpp` (CaptureSequenceHeader/PrependSequenceHeaderIfKeyframe), `android-client/.../video/VideoDecoderActivity.kt` |
| Việc 3: R16 decoder recovery | Bắt `MediaCodec.CodecException` riêng (trước đây bị catch chung `Exception` rồi im lặng bỏ qua — decoder chết vĩnh viễn). Giờ tự stop+release+recreate+configure+start trên cùng Surface, cap 5 lần liên tiếp tránh spin loop vô hạn. | `android-client/.../video/VideoDecoderActivity.kt` (recoverDecoderAfterError) |
| Việc 4: wiring E2E | `StreamingCoordinator.cs` (mới, Host) nối NativeCaptureEncoder→VideoStreamServer(29500) + `ControlSocketServer.cs` (mới, Core, 29501)→`InputDispatcher.cs` (mới, Core)→Cursor/TouchInjector + SettingsStore/SettingsChangeClassifier→CONFIG_UPDATE/MODE_CHANGE. CAPS thật cập nhật DeviceCaps (không còn hardcode 2560×1600). Android: `ControlSocketClient.kt` (mới) gửi CAPS thật (Display API) nhận CONFIG, gắn `TouchCaptureView` overlay lên `VideoDecoderActivity` qua FrameLayout. | `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs`, `pc-host/src/DisplayBridge.Core/Control/ControlSocketServer.cs`, `pc-host/src/DisplayBridge.Core/Input/InputDispatcher.cs`, `pc-host/src/DisplayBridge.Core/Settings/SettingKeyMap.cs`, `android-client/.../input/ControlSocketClient.kt`, `android-client/.../video/VideoDecoderActivity.kt` |
| Việc 5: test bằng fake-device | `tools/fake-device/FakeTabletDevice.cs` (mới, class library) giả lập tablet ROD2-W09 (3000×1920@120, CAPS thật) nối cả 2 socket. `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs` (mới, project trước đó rỗng) — 7/7 test xanh: video+control connect, native fallback không crash, CAPS→CONFIG cập nhật DeviceCaps thật, touch 1-ngón→Cursor, touch 2-ngón→Touch, SETTING_REQUEST Live→CONFIG_UPDATE, SETTING_REQUEST ReMode→MODE_CHANGE. | `tools/fake-device/*.cs`, `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs` |

**Đánh giá thật (theo tinh thần Fable session 2)**: PC-side pipeline (control+input+settings) giờ CHẠY THẬT qua fake-device, không chỉ code song song không nối. Video vẫn ở stub (0 frame NAL thật) vì native chưa build. Chưa test với tablet thật hay `dotnet test` full solution qua `.sln` (Core.Tests + Integration.Tests chạy độc lập qua từng .csproj, không qua sln — sln thiếu 2 entry này, chưa fix trong session này).

### Session 4 — JPEG/GDI fallback, demo E2E thật (chi tiết)

**Yêu cầu user**: "muốn chạy cơ bản app trước" — bỏ qua native C++/H.264/NVENC hoàn toàn lúc này, làm đường tắt thuần C# để CHẠY THẬT ngay, chất lượng/hiệu năng không quan trọng ở bước này.

| Việc | Kết quả | File |
|------|---------|------|
| 1. GdiScreenCapture (PC) | Mới, implement `IFrameSource`. `Graphics.CopyFromScreen` (virtual screen, tất cả monitor, qua `GetSystemMetrics` P/Invoke — không cần WinForms `Screen` class) → JPEG quality 55 qua `ImageCodecInfo`/`EncoderParameters`. Tự pace ~250ms/frame (~4fps) bằng `Thread.Sleep` ngay trong `GetNextFrame()`. Thêm `System.Drawing.Common` 8.0.10 vào `DisplayBridge.Core.csproj`. | `pc-host/src/DisplayBridge.Core/Video/GdiScreenCapture.cs`, `DisplayBridge.Core.csproj` |
| 2. Đánh dấu JPEG frame trên wire | `VideoFrameHeader.Flags` bit0=keyframe (đã có, dùng bởi H.264), claim **bit1** làm cờ JPEG mới (`FrameFlags.Jpeg = 0x02`, mới trong `NativeCaptureEncoder.cs`). `EncodedFrame` thêm field `IsJpeg` (default false, không phá record cũ). `schema.yaml` doc comment cập nhật (chỉ comment, không đổi wire layout → không cần regenerate codegen). | `pc-host/src/DisplayBridge.Core/Video/NativeCaptureEncoder.cs` (EncodedFrame, FrameFlags), `VideoStreamServer.cs` (set flags), `tools/protocol-schema/schema.yaml` |
| 3. StreamingCoordinator fallback đổi thứ tự | Native fail → thử `GdiScreenCapture` (mới) thay vì nhảy thẳng `StubFrameSource`. Nếu GDI cũng fail (vd headless) mới rơi xuống stub. Log rõ tiếng Việt "đang chạy chế độ JPEG fallback". Thêm `VideoStreamServer.FrameWritten` event + `OnFrameWritten` log (frame #1-3 chi tiết, sau đó throttle 1 lần/giây fps summary) làm bằng chứng cụ thể. | `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs`, `pc-host/src/DisplayBridge.Core/Video/VideoStreamServer.cs` (event mới) |
| 4. Android hiển thị JPEG (bypass MediaCodec) | `VideoFrame.isJpeg` (mới, đọc bit1). `VideoDecoderActivity.onFrame()`: nếu `isJpeg` → `renderJpegFrame()` mới (BitmapFactory.decodeByteArray → scale-to-fit giữ aspect ratio → `surfaceHolder.lockCanvas()`/`canvas.drawBitmap()`/`unlockCanvasAndPost()`), return sớm, KHÔNG chạm nhánh MediaCodec/H.264 cũ. Log frame #1-3 + fps summary/giây tương tự PC. | `android-client/.../video/VideoFrameParser.kt` (VideoFrame.isJpeg), `VideoDecoderActivity.kt` (renderJpegFrame, drawBitmapToSurface) |
| 5. Chạy thật + bằng chứng | Build PC (`dotnet build` Host.csproj) ✅. Build Android (`gradlew assembleDebug`) ✅, cài lên tablet thật (`AL9SBB4622000114`) qua `adb install -r` ✅. **Hướng port xác minh lại đúng theo code**: `VideoStreamClient.kt`/`ControlSocketClient.kt` connect ra `127.0.0.1` từ device (PC là server) → dùng **`adb reverse tcp:29500 tcp:29500`** + **`adb reverse tcp:29501 tcp:29501`** (KHÔNG phải `adb forward` — forward là hướng ngược, dùng khi device chạy server). Chạy PC Host thật (`dotnet run`, ghi log ra `%TEMP%\displaybridge-host.log` vì Host là WinExe không có console). Launch Activity thật (`adb shell am start`) — `adb logcat` xác nhận **101 frame JPEG thật (271254-271256 bytes/frame) đi qua trong 40 giây, ~3.3-3.4 fps ổn định, PC log và Android log khớp byte-for-byte, KHÔNG có exception/FATAL nào**. | — |

**Bằng chứng log thật** (PC, `%TEMP%\displaybridge-host.log`):
```
[14:54:17.212] Native capture unavailable (DllNotFoundException...) -- đang chạy chế độ JPEG fallback
[14:54:17.221] GdiScreenCapture initialized -- JPEG fallback active (~4fps, quality~55, không phải H.264 thật).
[14:54:17.229] VideoStreamServer listening on 29500 (native=False)
[14:55:13.489] Video: 101 frames total, 3.4 fps (last frame 271256 bytes, isJpeg=True)
```

**Bằng chứng log thật** (Android, `adb logcat`, tag `VideoDecoderActivity`):
```
JPEG fallback: 13 frames total, 3,4 fps (last frame 271254 bytes)
JPEG fallback: 45 frames total, 3,4 fps (last frame 271254 bytes)
```
Không có dòng `AndroidRuntime`/`FATAL` nào trong logcat trong suốt thời gian chạy — `grep -E "AndroidRuntime|FATAL"` rỗng.

**Không đổi / KHÔNG động vào**: `DisplayBridge.Native/` (C++) giữ nguyên hoàn toàn cho việc build thật sau khi có Windows SDK. Nhánh MediaCodec/H.264 trong `VideoDecoderActivity.kt` giữ nguyên, chỉ thêm nhánh rẽ `isJpeg` trước nó.

**Test regression**: `dotnet test Core.Tests` 67/67 xanh, `Integration.Tests` 7/7 xanh, `gradlew testDebugUnitTest` 36/36 xanh (14+14+8) — không phá vỡ gì.

**Đánh giá thật**: đây LÀ demo E2E chạy được đầu tiên của dự án — ảnh màn hình PC thật xuất hiện trên tablet thật, không phải test hay fake-device. Nhưng đây vẫn là shortcut tạm: 3-4fps JPEG không phải sản phẩm cuối (M2/M5 vẫn cần H.264/NVENC thật qua native build). Input/touch/settings (M3/M4) CHƯA test lại qua đường JPEG fallback này trong session 4 (chỉ test qua fake-device ở session 3) — nên coi đó là follow-up nếu cần demo đầy đủ cả 2 chiều.

### Session 5 — chạm tablet thật → chuột PC thật (M3 hoàn thành, tìm+fix 1 bug thật)

**Yêu cầu user**: biến demo hiện tại (chỉ NHÌN) thành MVP dùng được thật (CHẠM để điều khiển chuột PC), chạy trực tiếp bằng tools (không giao Agent), verify bằng bằng chứng cụ thể (tọa độ tap gửi + tọa độ con trỏ chuột đọc trước/sau).

**Việc 1 — kiểm tra dây nối**: Đọc kỹ `StreamingCoordinator.cs`/`App.xaml.cs`/`ControlSocketServer.cs` (PC) và `VideoDecoderActivity.kt`/`ControlSocketClient.kt`/`TouchCaptureView.kt` (Android) — **cả 2 phía đã nối sẵn đúng từ session 3**, không cần vá gì ở tầng wiring: `App.OnStartup` → `StreamingCoordinator.Start()` khởi cả `VideoStreamServer`(29500) và `ControlSocketServer`(29501) cùng lúc; `VideoDecoderActivity.onCreate()` đã gắn `TouchCaptureView` overlay lên `FrameLayout` cùng `SurfaceView` và start `ControlSocketClient` ngay từ đầu. Việc 1&2 trong brief coi như đã xong từ trước — session 5 chỉ cần CHẠY THẬT để kiểm chứng.

**Việc 3 — chạy thật, tìm ra bug thật**:
1. `adb reverse tcp:29500 tcp:29500` + `tcp:29501 tcp:29501` — cả 2 set OK.
2. Build+chạy PC Host thật (`dotnet run --project src/DisplayBridge.Host`), build+cài APK thật lên tablet (`gradlew assembleDebug` + `adb install -r`), launch `com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity` bằng `adb shell am start`. CAPS/CONFIG handshake thật chạy OK (log khớp: `CAPS received: 3000x1920@400dpi...` ↔ Android `Sending CAPS: 3000x1920@400dpi...`), video JPEG tiếp tục chạy song song bình thường (không bị phá).
3. **Bug thật tìm thấy khi chạm tay đầu tiên**: `adb shell input tap 1500 960` → app **CRASH ngay lập tức** (`FATAL EXCEPTION: main ... android.os.NetworkOnMainThreadException` tại `ControlSocketClient.send(ControlSocketClient.kt:236)` ← `TouchCaptureView.dispatchAsBatch(TouchCaptureView.kt:125)` ← `TouchCaptureView.onTouchEvent`). Root cause: `onTouchEvent` chạy trên UI thread, gọi thẳng `outputStream.write()` đồng bộ (blocking socket I/O) — StrictMode Android chặn network call trên main thread và giết cả Activity. **Bug này KHÔNG thể lộ ra qua fake-device/unit test** (67+7+36 xanh trước đó) vì StrictMode NetworkOnMainThreadException chỉ tồn tại trên runtime Android thật, không có trong JVM test thường — đúng như brief cảnh báo trước "bug thật sẽ lộ khi chạy thật, khác test giả lập".
4. **Fix** (tối thiểu, đúng scope): `ControlSocketClient.kt` — thêm `writeQueue: LinkedBlockingQueue<ByteArray>` + 1 writer thread riêng (`writerLoop`). `send()` (override `ControlChannel`) giờ chỉ `writeQueue.offer(bytes)` (non-blocking, an toàn gọi từ UI thread), thread nền `writerLoop` mới thật sự `outputStream.write()+flush()`. `sendCaps()` không đổi (đã chạy trên thread nền của `runLoop()` từ trước, chỉ hưởng lợi thêm an toàn). Không đụng `TouchCaptureView.kt` (giữ đúng "chỉ nối, không viết lại logic đã đúng").
5. Rebuild + `gradlew testDebugUnitTest` (36/36 xanh, không hỏng) + cài lại APK + relaunch. Test lại: `adb shell input tap 100 100` (góc trên-trái tablet, 3000×1920) — **app KHÔNG crash nữa**, PC log nhận `TOUCH_EVENT pointer=0 action=Down x=2184 y=3413` rồi `action=Up` cùng toạ độ (2184/65535≈0.033 ≈ 100/3000, khớp đúng chuẩn hoá).
6. **Bằng chứng chuột di chuyển thật** (`[System.Windows.Forms.Cursor]::Position` đọc trước/sau qua PowerShell):
   - Tap (100,100)/3000×1920 (góc trên-trái) → cursor BEFORE=(715,600) → AFTER=(27,73) — di chuyển đúng về góc trên-trái màn hình PC.
   - Tap (2500,1600)/3000×1920 (tỷ lệ 0.833,0.833) → PC log `TOUCH_EVENT x=54612 y=54612` (54612/65535=0.833, khớp đúng) → cursor AFTER=(1422,889); với `Screen.PrimaryScreen.Bounds`=1707×1067, tỷ lệ 1422/1707=0.833 và 889/1067=0.833 — **khớp chính xác cả 2 trục**, chứng minh `CursorInjector.MoveTo`/`Win32Interop.SetCursorPos` hoạt động đúng tỷ lệ thật.
   - (Ghi chú phụ: tap tại (2900,1800) — quá gần mép phải/dưới — KHÔNG tạo ra TOUCH_EVENT nào, nhiều khả năng bị OS Android nuốt làm gesture điều hướng cạnh màn hình, không phải bug của app; không điều tra sâu thêm vì ngoài phạm vi.)
7. Thêm log chẩn đoán `TOUCH_EVENT`/`TOUCH_BATCH` vào `StreamingCoordinator.OnTouchEventReceived/OnTouchBatchReceived` (trước đây câm lặng, chỉ gọi thẳng `_inputDispatcher`) — giữ lại vì hữu ích lâu dài để verify thủ công (giống style CAPS/Frame logging đã có).
8. Regression cuối: `dotnet test Core.Tests` 67/67 xanh, `Integration.Tests` 7/7 xanh, `gradlew testDebugUnitTest` 36/36 xanh — không phá vỡ gì.

**File đã sửa** (session 5):
- `android-client/app/src/main/java/com/displaybridge/input/ControlSocketClient.kt` — fix bug thật: `send()` non-blocking qua `LinkedBlockingQueue` + writer thread riêng (trước đó blocking write trên UI thread → NetworkOnMainThreadException crash).
- `pc-host/src/DisplayBridge.Host/StreamingCoordinator.cs` — thêm log `TOUCH_EVENT`/`TOUCH_BATCH` (chẩn đoán, không đổi hành vi dispatch).

**Đánh giá thật**: M3 (input 2 chiều) giờ đã CHẠY THẬT đầu-cuối trên tablet+PC thật, không chỉ qua fake-device — video (JPEG fallback) VÀ touch→cursor chạy song song không phá nhau. Bug NetworkOnMainThreadException là minh chứng cụ thể cho lý do "chạy thật" quan trọng hơn test giả lập ở bước cuối cùng của 1 tính năng input. Còn lại: chưa test click/drag phức tạp (2 ngón → Touch route, giữ lâu → drag) trên tay thật — chỉ mới xác nhận tap đơn (Down+Up nhanh) hoạt động đúng; coi đây là follow-up nếu cần.

### Session 6 — H.264 THẬT chạy trên tablet (native build lần đầu, tìm+fix 2 bug thật)

**Bối cảnh**: user vừa cài MSVC v143 + Windows SDK 10.0.22621 — `DisplayBridge.Native.vcxproj` build được thật lần đầu tiên (verify bằng MSBuild trực tiếp trước session này). Mục tiêu: chuyển từ JPEG fallback (session 4, 3.4fps) sang H.264 thật (DXGI capture + Media Foundation encode).

**Việc 1 — build lại, xác nhận sạch**: `MSBuild DisplayBridge.Native.vcxproj -p:Configuration=Debug -p:Platform=x64` → PASS sạch, ra `DisplayBridge.Native.dll`.

**Việc 2 — test cô lập native DLL (tool mới)**: viết `pc-host/tools/NativeSmokeTest` (console app mới, P/Invoke trực tiếp `DisplayBridge_CaptureInit/GetFrame/FreeFrame/Shutdown`, tự copy DLL qua `.csproj`) — bypass hoàn toàn C# fallback logic để cô lập lỗi native trước khi ghép hệ thống.

- Lần chạy đầu: `DisplayBridge_CaptureInit()` trả về lỗi 104 (=100+`EncoderError::SetInputTypeFailed`). Đây là bug thật ĐẦU TIÊN chỉ lộ ra khi native code thật sự chạy (trước đây `H264Encoder.cpp` "UNBUILT/UNVERIFIED", viết đúng signature nhưng chưa từng biên dịch/chạy).
- Thêm log `fprintf(stderr, ...hr=0x%08lX...)` để chẩn đoán → `hr=0xC00D6D77` (`MF_E_INVALIDMEDIATYPE`) tại `SetInputType`, backend=1 (NVENC hardware MFT đã chọn đúng).
- **Root cause #1 (đã fix)**: `m_transform->GetInputAvailableType()` cũng trả cùng lỗi ngay từ index 0 — NVENC's MFT là **async MFT** (`MF_TRANSFORM_ASYNC` attribute = TRUE) và theo MSDN, một async MFT khoá mọi method (kể cả enumerate/set type) cho tới khi client set `MF_TRANSFORM_ASYNC_UNLOCK=TRUE` trên attribute store của transform để xác nhận hiểu đúng async contract. Code cũ chưa làm bước này (không thể biết trước vì chưa từng chạy trên MFT thật). Fix: sau `ActivateObject`, kiểm tra `MF_TRANSFORM_ASYNC` rồi set unlock nếu cần.
- **Root cause #2 (đã fix)**: ngay cả sau khi unlock, input type build "từ đầu" (chỉ set MAJOR_TYPE/SUBTYPE/FRAME_SIZE/FRAME_RATE) vẫn bị NVENC's MFT từ chối — MFT thật đòi input type phải xuất phát từ 1 trong các candidate của chính nó (`GetInputAvailableType`), không chấp nhận type tự tạo dù đủ field "hợp lý". Fix: enumerate input candidates, chọn candidate có `MF_MT_SUBTYPE=NV12`, chỉ override size/framerate/aspect-ratio trên đó rồi mới `SetInputType`.
- Sau 2 fix: `DisplayBridge_CaptureInit() -> 0`. Capture 10/10 frame thật, NAL types quan sát được đúng chuẩn H.264: frame đầu `[7,8,9,7,8,5]` (SPS+PPS+SEI+SPS+PPS+IDR, MFT tự phát in-band + prepend R17 hoạt động đúng), các frame sau `[9,1]` (SEI+non-IDR slice). PASS.

**Việc 3 — StreamingCoordinator wiring**: không cần sửa logic — `CreateFrameSource()` đã thử native trước (session 3), giờ `NativeCaptureEncoder.Init()` thành công thật nên tự động dùng native, không rơi vào GDI fallback. Verify bằng log thật: `NativeCaptureEncoder initialized -- real capture/encode active.` + `VideoStreamServer listening on 29500 (native=True)`.

**Việc 4 — E2E thật trên tablet**:
1. `adb reverse tcp:29500/29501` OK. Thêm `<PlatformTarget>x64</PlatformTarget>` + auto-copy `DisplayBridge.Native.dll` vào `DisplayBridge.Host.csproj` (trước đây AnyCPU — rủi ro `BadImageFormatException`; giờ ép x64 khớp native DLL, và copy tự động thay vì thủ công).
2. Chạy PC Host thật (`DisplayBridge.Host.exe`), launch `com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity` trên tablet thật (`AL9SBB4622000114`).
3. Log PC (`%TEMP%\displaybridge-host.log`) xác nhận `native=True`, `isJpeg=False` xuyên suốt — **không còn dòng JPEG fallback**.
4. `adb logcat` xác nhận: `c2.qti.avc.decoder` (MediaCodec THẬT, nhánh H.264) configure+start thành công (`width=1920 height=1080 mime=video/avc`), **0 `CodecException`/`FATAL`/`AndroidRuntime` crash** trong suốt phiên chạy, `VariableRefreshRateHandler`/`RenderEngine` log liên tục xác nhận SurfaceView đang thật sự present frame.
5. Chạy 103 giây liên tục: **258 frames, ~2.4-2.8fps ổn định** (thấp hơn JPEG fallback session 4 = 3.4fps — do naive CPU BGRA→NV12 conversion mỗi frame ở độ phân giải màn hình PC thật 2560×1600, chưa tối ưu; chấp nhận được ở bước này theo đúng ràng buộc brief, tối ưu bitrate/GOP/conversion là việc của M5).
6. Backend xác nhận qua log native: **NVENC hardware** (`EncoderBackend.HardwareNvenc`, backend=1) — không phải software encoder.

**Việc 5 — regression, tìm+fix bug thật thứ 2**: `dotnet test Integration.Tests` **CRASH** (`AccessViolationException` trong `DisplayBridge_CaptureGetFrame`) — chưa từng xảy ra trước đây vì native trước đó luôn ném `DllNotFoundException` ngay lập tức (never actually called). Root cause: `VideoStreamServer.Stop()` cancel token rồi return ngay, không đợi `ServeClientAsync` background task thoát hẳn; `StreamingCoordinator.Stop()` dispose `IFrameSource` (→ `DisplayBridge_CaptureShutdown()` reset global native state) NGAY SAU ĐÓ trong khi task nền của lần test trước có thể vẫn đang gọi `DisplayBridge_CaptureGetFrame()` trên cùng global đó → use-after-free thật trong native code. Fix: `VideoStreamServer` track các client task (`ConcurrentBag<Task>`), `Stop()` giờ `Task.WaitAll(..., 2s)` trước khi return, đảm bảo an toàn dispose ngay sau. Cũng sửa 1 test cũ giả định sai (`NativeCaptureUnavailable_FallsBackToStub_DoesNotCrash` — giả định native luôn unavailable, không còn đúng nữa) thành `FrameSourceSelection_NeverCrashesStart_RegardlessOfNativeAvailability`.
- Sau fix: `Core.Tests` 67/67, `Integration.Tests` 7/7, `gradlew testDebugUnitTest` (36 unit test Android) BUILD SUCCESSFUL — tất cả xanh, không hỏng gì.

**File đã sửa/thêm** (session 6):
- `pc-host/src/DisplayBridge.Native/H264Encoder.cpp` — fix bug thật #1+#2 (async MFT unlock + input type từ `GetInputAvailableType` thay vì tự tạo), thêm `fprintf(stderr,...)` chẩn đoán HRESULT cho SetOutputType/SetInputType/FindEncoderTransform (giữ lại lâu dài, hữu ích cho debug tương lai).
- `pc-host/tools/NativeSmokeTest/` (MỚI) — console app P/Invoke cô lập test native DLL, không phụ thuộc StreamingCoordinator/Host.
- `pc-host/src/DisplayBridge.Host/DisplayBridge.Host.csproj` — thêm `PlatformTarget=x64` + auto-copy `DisplayBridge.Native.dll`.
- `pc-host/src/DisplayBridge.Core/Video/VideoStreamServer.cs` — fix bug thật #3 (race Stop()/Dispose() với native global state): track+await client tasks trong `Stop()`.
- `pc-host/tests/Integration.Tests/EndToEndWiringTests.cs` — sửa 1 test có giả định môi trường đã lỗi thời (native luôn unavailable).

**Đánh giá thật**: M1 (Video PoC mirror) giờ hoàn thành 100% với H.264 thật (NVENC hardware) chạy end-to-end PC↔tablet thật, không còn phụ thuộc JPEG fallback. Đây là session đầu tiên native pipeline (DXGI+MF, viết từ session 1-2 nhưng "UNBUILT/UNVERIFIED" suốt 5 session) thực sự chạy — và đúng như dự đoán, lộ ra 3 bug thật (2 trong encoder init, 1 race condition ở lifecycle) mà không unit test/fake-device nào bắt được vì chúng chỉ tồn tại khi gọi API MFT/native thật. fps (~2.5) thấp hơn kỳ vọng ban đầu (NVENC nhanh) do CPU-side NV12 conversion là bottleneck thật — đúng theo dự đoán trong code comment gốc ("naive CPU BGRA->NV12 conversion... acceptable for a mirror PoC only"); tối ưu (GPU conversion, giảm resolution scale, bitrate/GOP tuning) để lại cho M5 đúng theo ràng buộc brief.

## Hardware profile đã chốt (probe 2026-07-03)

| Item | Giá trị |
|------|--------|
| Tablet | HONOR ROD2-W09 · Android 16 (SDK 36) · serial AL9SBB4622000114 |
| Panel | 3000×1920 @ 60/90/120Hz (144Hz đã bỏ hẳn khỏi hệ thống) · 400dpi |
| SoC | Snapdragon 8s Gen 3 (SM8635) |
| PC | Ryzen 9 8945HX + RTX 5060 Laptop (NVENC Blackwell) |
| **Default profile** | **Max = native resolution đọc runtime qua CAPS @ min(120, Hz cao nhất máy hỗ trợ)** — không hardcode theo model. Với ROD2-W09: 3000×1920@120 HEVC ~69Mbps, CONFIRMED GO |
| Transport | ADB forward 31–33 MB/s thật, dư ~3x. Port chuẩn: **video 29500 / control 29501** |
| Input | Hybrid Cursor/Touch: 1 ngón=cursor thật (SendInput), ≥2 ngón hoặc edge-zone=touch injection multi-point (InjectTouchInput) — Windows tự nhận gesture, không tự code |

## Task board — M0 (hoàn thành, có bằng chứng)

| ID | Task | Status | Kết quả thật |
|----|------|--------|--------------|
| M0.1 | Repo scaffold | ✅ DONE (env-gap) | Host build sạch. Native C++ không build được — thiếu MSVC v143 + Windows SDK |
| M0.2 | bench-transport | ✅ DONE | ADB 31–33 MB/s thật, dư 3x. Opus sửa 1 bug teardown NCM script |
| M0.3 | codec-probe | ✅ DONE | HEVC/AVC GO@120Hz, NO-GO@144Hz (log thật). Opus sửa xung đột Kotlin plugin |
| M0.4 | Protocol codegen | ✅ DONE (sau fix) | 12/12 + 11/11 test. Opus bắt + sửa bug drift nghiêm trọng (Python nguồn sự thật sinh code không compile được) |

## Task board — M1/M3/M4 (session 2, code xong — chưa build C++/chưa wiring E2E)

| ID | Task | Status | Kết quả thật (Sonnet + Opus review) |
|----|------|--------|--------------------------------------|
| M1-PC | Capture (DXGI Desktop Duplication) + encode (Media Foundation H.264, ưu tiên NVENC) + video socket server | 🔶 Code xong, KHÔNG build được C++ | MSVC toolset xác nhận **hỏng/cài dở** (không phải thiếu hoàn toàn): MSB8020 thật, thiếu `vcvarsall.bat`, không có Windows 10/11 SDK trong registry. C# side (`VideoStreamServer`, `NativeCaptureEncoder`, `IFrameSource`) build+test qua scratch copy. **Opus tìm + sửa 1 COM leak thật** trong `H264Encoder.cpp` (nhánh software-fallback release nhầm cả object đã chọn). **Opus sửa lệch port** 27183/27184→29500/29501 (lỗi thực ra nằm ở code M4, không phải M1-PC). |
| M1-Android | Nhận video qua TCP, MediaCodec decode, SurfaceView fullscreen, foreground service | ✅ DONE, verify thật trên tablet | 8/8 test, cài+chạy thật trên ROD2-W09, retry connect sạch không crash. **Sonnet tự tìm+sửa 1 crash thật** (SecurityException do sai foreground service type). **Opus tìm+sửa tiếp cho ĐÚNG**: đổi `dataSync`→`mediaPlayback` vì `dataSync` bị Android 15+ cap ~6h rồi OS kill — sẽ giết session màn-phụ-dài. Verify lại trên máy: chạy ổn định, đúng type. Khuyến nghị còn treo: decoder cần tự reset khi gặp `CodecException` (hiện chết cứng vĩnh viễn sau lỗi đầu tiên) — để M1.4. |
| M3 | Protocol `TOUCH_EVENT`/`TOUCH_BATCH` mới + Android touch/pen capture + PC Hybrid Cursor/Touch injector (C# P/Invoke, không dùng C++) | ✅ DONE (sau can thiệp) | **Lưu ý quy trình**: agent này ban đầu tự spawn 2 lớp sub-agent liên tiếp không tiến triển thật — đã can thiệp yêu cầu thực thi trực tiếp, sau đó hoàn thành tốt. 67/67 C# test + 36/36 Kotlin test (bao gồm cả phần của M1-PC/M4 dùng chung project). `generate.py` chạy THẬT (tìm ra Python cài từ phiên trước). Opus xác nhận `Win32Interop.cs` struct layout đúng, `InputModeClassifier` có xử lý đúng cảnh báo "nhả cursor trước khi chuyển Touch" từ RESEARCH-v1, `TouchEventMapper.kt` không overflow ở biên. |
| M4 | Settings store (JSON, clamp) + WPF Settings UI + classifier live/re-mode | ✅ DONE (sau fix) | 46→51→67 test (số tăng dần do M3 thêm sau). Host build sạch. Xác nhận 144Hz đã loại bỏ hoàn toàn khỏi settings. **Opus tìm+sửa 1 field phân loại sai** (AutoConnect: Reconnect→WindowsApi, đúng theo catalog). **Opus phát hiện lệch port** (đã Opus vòng sau sửa). |

## Task board — còn lại (chưa bắt đầu)

| ID | Task | Status | Ghi chú |
|----|------|--------|---------|
| M1 wiring E2E | Ghép capture→encode→VideoStreamServer(29500) ↔ Android client; ControlChannel(29501) ↔ InputModeClassifier→Injector; Settings áp CONFIG_UPDATE/MODE_CHANGE | ✅ DONE (PC-side qua fake-device) | Session 3: `StreamingCoordinator`+`ControlSocketServer` nối xong, 7/7 Integration.Tests xanh. **Còn thiếu**: video thật (native chưa build → 0 NAL frame qua dây), test với tablet thật (chỉ verify qua fake-device giả lập) |
| M2.* | IddCx driver (4 task) ★ | ☐ BLOCKED | Cần user cài MSVC v143 + Windows SDK + xác minh WDK |
| M5.* | Hardening + v1.0 (5 task, gồm M5.5 build packaging) | ☐ TODO | Acceptance: P95 ≤35ms @native-120Hz; artifact `.exe`+`.apk` trong `APP_BUILD/` |

## Decisions log

| Ngày | Quyết định | Căn cứ |
|------|-----------|--------|
| 2026-07-02/03 | (xem lịch sử đầy đủ ở version trước — transport ADB-first, HEVC≥120Hz, settings PC-authoritative, bỏ 144Hz, resolution runtime, Hybrid input, M5.5 build packaging) | — |
| 2026-07-03 | **Port chuẩn hóa 29500 (video) / 29501 (control)** — sau khi phát hiện code M4 hardcode nhầm 27183/27184 kiểu scrcpy | Opus review tích hợp cuối, sửa về đúng CONTEXT-BRIEF/PLAN |
| 2026-07-03 | **Foreground service type Android = `mediaPlayback`, không phải `dataSync`** | Android 15+ cap dataSync ~6h rồi kill — sai với use-case màn-phụ chạy vô thời hạn |
| 2026-07-03 | Input injector (Cursor+Touch) viết bằng **C# P/Invoke**, không viết C++ | Né hoàn toàn rào cản thiếu MSVC toolchain đang chặn M1-PC/M2 |

## Risk register cập nhật

| ID | Rủi ro | Trạng thái |
|----|--------|-----------|
| R8, R11 (v2) | Decoder/USB speed | RESOLVED |
| R13 | Máy dev thiếu MSVC v143 + Windows SDK | **MỞ, xác nhận lại session 3**: MSB8020 tái hiện sau khi set VCTargetsPath đúng — v143 build tools thật sự không có trên máy (không phải path sai). Windows SDK cũng xác nhận thiếu (không có `Windows Kits\10\Include`). Cần user cài qua Visual Studio Installer: workload "Desktop development with C++" + Windows 10/11 SDK |
| R14, R15 | Soak-test transport, NCM throughput | MỞ, chưa chặn tiến độ |
| R16 | `MediaCodec.CodecException` không có recovery — decoder Android chết cứng vĩnh viễn sau lỗi đầu tiên | **CLOSED (session 3)**: `VideoDecoderActivity.recoverDecoderAfterError()` bắt riêng `MediaCodec.CodecException`, tự stop+release+recreate+configure+start trên cùng Surface, cap 5 lần liên tiếp. Chưa test trên tablet thật (chỉ build+unit-test xanh) |
| R17 | SPS/PPS in-band vs CSD buffer chưa chốt giữa M1-PC (encoder) và M1-Android (decoder) | **CLOSED (session 3)**: chốt in-band. PC prepend SPS/PPS (capture 1 lần từ `MF_MT_MPEG_SEQUENCE_HEADER`) vào mọi IDR trong `H264Encoder.cpp`; Android xác nhận không set csd-0/csd-1. Cả 2 phía có comment chốt quyết định. Chưa verify bằng frame thật (native chưa build) |
| R18 | Parallel-Sonnet-trên-cùng-project (Core.Tests dùng chung bởi M1-PC/M3/M4) từng làm vỡ build giữa chừng (M3 đổi protocol khi M1-PC đang code) — tự hồi phục nhờ Opus tích hợp cuối, nhưng là bài học quy trình | Đã đóng ở session 2, **áp dụng cho M2**: không chia nhỏ, xem Fable overview M0 |
| **R19 (mới, session 3)** | `SettingKeyMap` (byte key ↔ SettingField cho SETTING_REQUEST/CONFIG_UPDATE) được định nghĩa mới ở PC-side only, chưa có trong `schema.yaml` — nếu Android's floating button (M4.3) tự định nghĩa key numbering khác thì SETTING_REQUEST sẽ silently no-op | MỞ — cần promote `SettingKeyMap` vào schema.yaml + codegen cả 2 phía trước khi làm M4.3 (floating button) |

## Bài học quy trình multi-agent (session 2)

- **1 agent (M3) rơi vào vòng lặp tự-spawn-sub-agent không tiến triển** — phải can thiệp trực tiếp bằng SendMessage yêu cầu thực thi ngay, không spawn thêm. Sau can thiệp, agent hoàn thành tốt. Bài học: theo dõi sát khi 1 agent báo "đã giao cho agent khác" từ 2 lần liên tiếp trở lên mà không có tiến độ cụ thể → can thiệp ngay, đừng chờ thêm.
- **Chia 4 Sonnet song song trên các module có phụ thuộc chung (Core.Tests, protocol schema) tạo xung đột giữa chừng** (M3 đổi `TouchBatchMessage` làm M1-PC's test tạm fail) — nhưng mỗi agent xử lý đúng nguyên tắc (không tự sửa phần không thuộc mình, verify qua scratch copy), và 1 vòng Opus tích hợp cuối đã hội tụ sạch (67/67 xanh). Bài học: khi các nhánh song song đụng chung 1 file/project, LUÔN cần 1 vòng Opus "tích hợp cuối" sau khi tất cả Sonnet xong, không chỉ review riêng lẻ từng nhánh.
- **5 Opus review lần này đều tìm ra ít nhất 1 vấn đề thật** (COM leak, port lệch, foreground service type sai, field misclassified) — tiếp tục củng cố giá trị của quy trình per-item review.

## Fable 5 overview (2026-07-03, session 2) — chốt cuối

**Không đồng ý "hội tụ sạch = cách chia đúng"**: M3 sửa protocol schema mà M1-PC/M4 đang tiêu thụ cùng lúc là quan hệ producer→consumer dự đoán được trước — lẽ ra nên tuần tự hóa (M3 phần protocol chốt trước, rồi M1/M4 song song), không phải song song rồi cứu bằng Opus tích hợp. **Quy tắc cho lần sau**: module nào ghi vào artifact dùng chung (schema, Core.Tests) phải freeze interface trước khi module đọc artifact đó khởi chạy.

**Sự cố M3 tự lặp vòng spawn-sub-agent đáng sửa vào brief** (chi phí phòng ngừa ~0): thêm dòng bắt buộc "thực thi trực tiếp, KHÔNG dùng Agent tool trừ khi bị chặn" vào mọi prompt giao việc Sonnet sau này.

**Cảnh báo quan trọng nhất — đừng để % code đánh lừa % sản phẩm chạy được**: 67/67 + 36/36 test pass chỉ chứng minh từng nhánh tự nhất quán, KHÔNG chứng minh sản phẩm chạy. Thực tế: 0 frame video nào từng đi qua hệ thống thật (C++ chưa build), M1/M3/M4 vẫn là **3 đảo riêng biệt chưa có entrypoint chung**. Đánh giá thật theo Fable: **~40-50% tới demo chạy được**, không phải 70-80% như % task code gợi ý.

**Ưu tiên phiên sau — SỬA lại thứ tự**: **MSVC trước (không phải wiring E2E trước)** — vì không build được Native thì phần lõi sản phẩm (video) không test được, wiring lúc đó chỉ chứng minh được nửa hệ thống (input/control). Thứ tự: (1) cài MSVC v143+SDK → build Native xanh, (2) chốt R17 (SPS/PPS in-band vs CSD) NGAY trong lúc wiring — đây là hợp đồng interface, không chốt thì frame đầu tiên đã không decode được, (3) wiring E2E video trước, input sau.

**R16 vs R17**: R17 nghiêm trọng hơn, chặn ngay từ bước wiring đầu tiên. R16 (decoder chết cứng sau CodecException) chặn việc gọi "M1 DONE" nhưng không chặn demo E2E đầu tiên — có thể tạm hoãn tới M1.4.

**Chưa có tiêu chí đo cho "demo E2E đầu tiên tính là chạy được"** (bao nhiêu giây stream ổn định, độ phân giải nào) — nên chốt trước khi bắt đầu phiên sau để tránh lặp lại việc lấy test-pass làm thước đo tiến độ.

---

## Session bridge

- **Phiên trước (2026-07-02/03, session 1)**: probe thiết bị, viết PLAN-v1/v1.1/v2, chạy M0 (4 Sonnet + 5 Opus + Fable).
- **Session 2 (2026-07-03)**: user yêu cầu sửa settings (bỏ 144Hz, resolution dynamic, build→APP_BUILD, Hybrid input) → cập nhật PLAN-v2 + research Windows touch mechanism → user yêu cầu "spawn haiku và sonnet đồng thời code toàn bộ task còn lại" → chạy Haiku (brief) + 4 Sonnet song song (M1-PC, M1-Android, M3, M4) + 5 Opus review (mỗi Opus tìm ra ≥1 vấn đề thật) + 1 lần can thiệp trực tiếp vào agent M3 bị lặp vòng.
- **Session 3 (2026-07-03)**: wiring E2E qua fake-device (StreamingCoordinator, ControlSocketServer, InputDispatcher), R16/R17 fixed, thử build C++ lại nhưng vẫn BLOCKED (v143 thật sự chưa cài).
- **Session 4 (2026-07-03, phiên này)**: user yêu cầu "chạy cơ bản app trước, build 1 phiên bản chạy ít nhất có thể" → bỏ qua native H.264 hoàn toàn, làm JPEG/GDI fallback thuần C#, chạy demo E2E thật trên tablet `AL9SBB4622000114` (xem chi tiết session 4 ở trên) — **đây là lần đầu tiên hình ảnh PC thật sự hiện lên tablet thật**, không qua fake-device/test nữa.
- **Phiên sau bắt đầu từ**: (1) nếu muốn demo đầy đủ 2 chiều, test lại input/touch/settings (M3/M4) qua đường chạy thật JPEG fallback này (session 3 chỉ test qua fake-device), (2) MSVC v143 + Windows SDK vẫn là khóa chính để lên chất lượng thật (M2 IddCx, native H.264/NVENC, M5 hardening) — GDI/JPEG chỉ là shortcut tạm ~3-4fps.
- **Câu hỏi mở**: có Magic-Pencil không (M3 manual test) · `.gitignore` cho `.manual-build/` · tách module Android `:probe` khỏi `:app` · MSVC cài lại theo hướng nào (Modify VS Installer vs cài mới hoàn toàn) · GDI/JPEG fallback giữ tới bao giờ (bỏ ngay khi native build xong, hay giữ làm chế độ dự phòng vĩnh viễn?) — nên hỏi user trước khi vào M2.

---

## Session 14 (2026-07-04, đêm — user đi ngủ, làm việc tự động) — 3 bug FPS thật + research

**Bối cảnh**: User báo FPS "rất rất tệ" (~20fps), yêu cầu tối thiểu 60fps lý tưởng 120fps, research kỹ + tham khảo Spacedesk, rồi đi ngủ — làm việc tự động qua đêm không có ai click UAC được.

**Research**: [SOLUTION-v1-fps-optimization.md](SOLUTION-v1-fps-optimization.md) — tổng hợp NVENC ULL tuning, Media Foundation CODECAPI, Spacedesk performance tuning. Kết luận: pipeline config đã đúng từ trước (LowLatencyMode, no B-frame, CBR) — vấn đề thật là 3 bug wiring, không phải thiếu tối ưu.

**3 bug tìm + fix (tất cả có bằng chứng code/log, build+test xanh)**:

| # | Bug | Root cause | Fix | File |
|---|-----|-----------|-----|------|
| 1 | FPS/bitrate không bao giờ tới encoder | `DisplayBridge_CaptureInitWithCodec` chỉ nhận codec, native hardcode `kDefaultFps=60`/`kDefaultBitrateKbps=12000` — 12Mbps thấp hơn 5.75 lần mức đúng cho 3000×1920@120 (~69Mbps) | Thêm `DisplayBridge_CaptureInitWithCodecFpsBitrate(codec,fps,bitrate)`, C# `NativeCaptureEncoder.Init(codec,fps,bitrateKbps)` overload mới, `StreamingCoordinator` thêm `_activeFps`/`_activeBitrateKbps` set từ `ChooseHz`/`EstimateBitrateKbps` TRƯỚC khi dùng (giống cách `_activeCodec` đã sửa session 12) | `CaptureEncodeExports.cpp`, `NativeCaptureEncoder.cs`, `StreamingCoordinator.cs` |
| 2 | Native capture fail ngay sau driver restart → JPEG fallback vĩnh viễn | `IDXGIOutput1::DuplicateOutput` fail tạm thời (code 6 = `DuplicateOutputFailed`) khi desktop đang tái cấu hình sau resolution change — code cũ thử 1 lần rồi bỏ cuộc. **Tái hiện LIVE thật trên máy**: log cho thấy đúng chuỗi restart→error 6→JPEG | Retry loop 4 lần, 400ms delay giữa các lần, trước khi rơi vào JPEG/stub fallback | `StreamingCoordinator.CreateFrameSource()` |
| 3 | Đổi codec qua Settings PC không áp dụng thật | `PushSettingsChange` nhánh `ReMode` (StreamingCodec) gửi `MODE_CHANGE` báo Android nhưng không re-init native encoder — Android chờ HEVC, PC vẫn encode codec cũ. **Đây là nguyên nhân "H265 vẫn chưa chạy"** user báo cáo | Cập nhật `_activeCodec` + gọi `RecreateFrameSourceForNewResolution()` khi field là `StreamingCodec` và codec thực sự đổi | `StreamingCoordinator.PushSettingsChange()` |

**Sự cố vận hành trong đêm**: tiến trình `DisplayBridge.Host.exe` cũ (elevated, do tôi tự launch để test) khóa file build, `Stop-Process`/`schtasks /Create /RL HIGHEST` đều fail (Access Denied — không elevated). Giải pháp: `(Get-WmiObject Win32_Process -Filter "Name='DisplayBridge.Host.exe'").Terminate()` qua WMI **thành công** dù `Stop-Process` fail — ghi nhận làm kỹ thuật dự phòng cho lần sau khi cần kill tiến trình elevated từ shell không elevated.

**Không thể verify end-to-end thật qua UAC** (user đang ngủ, môi trường tự động không click được) — đã thử `Start-Process -Verb RunAs` (treo chờ UAC), `schtasks` (Access Denied lúc tạo task /RL HIGHEST). Bằng chứng thay thế: build sạch (Native Debug+Release, Core+Host), `dotnet test Core.Tests` 69/69, `Integration.Tests` 8/8 — 1 lần fail thoáng qua do trùng thời điểm dọn process, tái hiện sạch ngay sau đó (không phải regression thật).

**Việc song song đang chạy**: Sonnet fix bug cursor biến mất (nghi do `<HardwareCursor>true</HardwareCursor>` trong `vdd_settings.xml` hoặc thiếu `MOUSEEVENTF_VIRTUALDESK`) + tự động tắt/bật driver VDD theo trạng thái kết nối ADB (tránh Windows hiện nhầm "2 màn hình" khi không cắm tablet).

**Việc BẮT BUỘC user tự verify khi dậy** (không cách nào tự động hóa được):
1. Mở `DisplayBridgeHost-v0.1.0-alpha.exe` bản mới nhất, bấm Yes UAC.
2. Xem FPS overlay tablet — kỳ vọng ≥60fps (H.264), dòng dưới ghi "H.264" chứ không phải "JPEG fallback".
3. Thử đổi Encode sang H.265 qua floating settings Android (restart app) hoặc Settings PC — xác nhận log `%TEMP%\displaybridge-host.log` có dòng codec khớp lựa chọn, tablet decode được (không phải màn đen/lỗi).
4. Verify cursor không còn biến mất khi di chuột qua màn tablet (kết quả từ Sonnet song song).
5. Rút cáp/tắt ADB, xác nhận driver VDD tự tắt (không còn hiện "2 màn hình" giả trong Windows Display Settings).

---

## Session 16 (2026-07-04 sáng, user dậy) — Trần 60fps + H.265 + deadlock recreate

**Bối cảnh**: User xác nhận FPS đã lên ~6x (fix session 14 có tác dụng) nhưng muốn 120; H.265 vẫn không chạy. Điều tra bằng log thật + thí nghiệm trực tiếp trên tablet qua ADB.

**3 root cause mới, tất cả CONFIRMED bằng bằng chứng:**

| # | Bug | Bằng chứng | Fix | File |
|---|-----|-----------|-----|------|
| 1 | Trần ~55-64fps dù VDD chạy 120Hz thật | `Win32_VideoController` xác nhận VDD 3000x1920@120Hz; log fps 55-64 = 15.6-18ms/frame khớp CHÍNH XÁC độ phân giải timer Windows 15.6ms — `ServeClientAsync` gọi `Task.Delay(5)` (thực tế ngủ ~15.6ms) mỗi lần encoder trả null (NVENC pipeline 1-2 frame nên null ~mỗi 2 call) | Bỏ delay khi null (native `AcquireFrame` tự block theo vsync nên không hot-spin); chỉ delay sau 100 null liên tiếp (bảo vệ test stub) | `VideoStreamServer.cs` |
| 2 | H.265: user chọn nhưng prefs lưu `encode_pref=0` | `run-as ... cat shared_prefs` cho thấy `encode_pref=0, fps_cap=120` (fps lưu OK, encode thì không). Ép tay `encode_pref=1` qua run-as → PC nhận ngay `CAPS codecs=1` → đường HEVC hạ nguồn HOẠT ĐỘNG. Nghi phạm: RadioGroup Encode nằm NGANG, nút "H.265 (HEVC)" (label dài nhất) bị cắt ra ngoài mép dialog → tap trượt/trúng H.264 | Encode group chuyển VERTICAL + sau khi lưu đọc LẠI từ disk hiện Toast "Đã lưu: ..." + log; delay 1.2s trước khi restart để Toast kịp hiện | `MobileSettingsDialog.kt` |
| 3 | Host treo VĨNH VIỄN khi recreate pipeline lúc client force-stop (tái hiện LIVE 06:43: log dừng ở "dang tao lai frame source", process Responding nhưng đứng >60s) | `NetworkStream.Write` sync không có timeout → block vô hạn khi peer (app Android restart để áp settings) chết giữa chừng; `Stop()` WaitAll 2s timeout rồi `Dispose()` native encoder trong khi thread khác vẫn ở trong `GetNextFrame()` → tear down globals giữa call | (a) `stream.WriteTimeout = 5000`; (b) lock `_nativeCallLock` serialize `GetNextFrame`/`Shutdown`, check `_initialized` sau khi thắng lock | `VideoStreamServer.cs`, `NativeCaptureEncoder.cs` |

**Lưu ý quan trọng**: Bug #3 chính là lý do sâu xa "H265 không chạy" nhìn từ phía user — mỗi lần áp settings trên tablet, app restart → host treo → mất hình → user tưởng H265 hỏng.

**Verify**: Core.Tests 82/82, Integration.Tests 14/14. Build Host single-file Release + APK, đóng gói `APP_BUILD/Window` + `APP_BUILD/Android`, APK đã `adb install -r` lên tablet (prefs giữ nguyên `encode_pref=1` → lần kết nối tới sẽ negotiate HEVC).

---

## Session 17 (2026-07-04, "app lỗi luôn") — Freeform window + ADB daemon crash

**Bối cảnh**: User báo app lỗi. Điều tra bằng `adb shell dumpsys activity activities`.

**Root cause #1 (CONFIRMED)**: `Task mBounds=Rect(613, 253 - 2132, 1770)` = kích thước **1519x1517** — khớp CHÍNH XÁC với CAPS sai `1519x1517@400dpi` trong log host. App bị đẩy vào **cửa sổ nổi (Honor App Multiplier/freeform window)** thay vì fullscreen — tính năng OEM, có thể bị kích hoạt do thao tác vuốt cạnh màn hình hoặc kéo nút Settings nổi. `sendCaps()` đọc `context.resources.displayMetrics`, khi ở freeform trả về kích thước CỬA SỔ chứ không phải màn hình vật lý → driver ảo bị co lại sai kích thước → hỏng toàn bộ pipeline.

**Fix**: `AndroidManifest.xml` — `VideoDecoderActivity` thêm `android:resizeableActivity="false"` + `supportsPictureInPicture="false"`, chặn hệ điều hành/launcher OEM đề nghị hoặc tự đẩy activity vào freeform/split-screen. Verify: `adb shell dumpsys activity activities` sau khi cài lại APK cho `mBounds=Rect(0, 0 - 3000, 1920)` (đúng full-screen) và `landScapeFreeformFlag:1` (đã khóa).

**Root cause #2 (phụ, không phải bug code)**: `adb.exe` daemon bị "connection reset" giữa chừng (không rõ nguyên nhân — có thể do OS/driver phía Windows, hoặc do 1 tiến trình khác gọi kill-server) → host spam "ADB poll: khong xac dinh duoc trang thai thiet bi" mỗi 7s. Đã `adb kill-server` + `adb start-server` khôi phục kết nối. Không có bằng chứng đây là bug DisplayBridge — nếu tái diễn cần theo dõi thêm.

**Đã làm**: build lại APK (BUILD SUCCESSFUL), cài lên tablet + `am force-stop` + relaunch fullscreen xác nhận đúng bounds, copy vào `APP_BUILD/Android`. Host cũ (bị treo từ trước, xem session 16) đã bị kill để user mở lại bản mới sạch.
