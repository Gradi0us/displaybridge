# TEST-v1-connection-ordering-matrix

**Created**: 2026-07-04
**Test Target**: Vòng đời kết nối DisplayBridge (PC Host ↔ Android qua adb reverse) — mọi thứ tự khởi động / mất-hồi kết nối có thể phá kết nối, + 2 feature mới (Task Scheduler auto-start, Android PC-presence watcher)
**Mode**: Planning | **Level**: L3-Analyze | **Type**: TEST | **Language**: vi
**Status**: Draft — test design, chưa execute (một số case chỉ chạy được khi có phần cứng thật)
**Author**: Claude (test designer, session 19)
**Depends on**: [RCA-v2-android-connect-adb-reverse-lifecycle.md](RCA-v2-android-connect-adb-reverse-lifecycle.md) (RC1-FIX + v2.1 addendum RC-A/RC-B/RC-C)

---

## Executive Summary

```
┌──────────────────────────────────────────────────────────────────────┐
│  Ma trận này verify rằng SAU KHI RC-A/RC-B/RC-C + RC1-FIX land, mọi   │
│  thứ tự khởi động và mọi sự cố vòng đời (mất tunnel, kill adb,        │
│  rút cáp, reboot, reconnect storm) đều TỰ HỒI, KHÔNG wedge Host,      │
│  KHÔNG bão devcon-restart, và CAPS trùng resolution KHÔNG restart      │
│  driver. Cộng thêm test cho 2 feature đang build song song.           │
│                                                                        │
│  Coverage: 14 case ordering/lifecycle · 8 case Task Scheduler ·        │
│            10 case PC-presence watcher · 6 smoke (<10 phút).           │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 0. Quy ước & Harness

### 0.1 Cổng & lệnh chuẩn (dùng lại xuyên suốt)

| Hằng số | Giá trị | Ghi chú |
|---|---|---|
| VideoPort | `29500` | `VideoStreamServer`, Android dial `127.0.0.1:29500` ON DEVICE |
| ControlPort | `29501` | `ControlSocketServer`, single-active-client |
| Reverse tunnels | `adb reverse tcp:29500 tcp:29500` + `tcp:29501 tcp:29501` | Host tự apply trong `Start()` + mỗi poll tick 7s |
| adb serial (ví dụ) | `AL9SBB4622000114` | thay bằng serial máy test |
| Host exe | `DisplayBridge.Host.exe` | `app.manifest requireAdministrator` → cần UAC/elevated |
| Host log | `%TEMP%\displaybridge-host.log` | evidence nguồn của mọi assertion phía PC |
| App package | `com.displaybridge.probe` | `applicationId` (build.gradle.kts) |
| Streaming activity | `com.displaybridge.video.VideoDecoderActivity` | mở qua `am start -n` |

```powershell
# --- Snippet dùng lại nhiều case (PowerShell) ---
$log = "$env:TEMP\displaybridge-host.log"
Get-Content $log -Wait -Tail 40                 # theo dõi log Host trực tiếp
Start-Process ".\DisplayBridge.Host.exe"        # khởi động Host (bung UAC)
Stop-Process -Name DisplayBridge.Host -Force    # tắt Host cứng (mô phỏng crash)
```

```bash
# --- Snippet adb dùng lại nhiều case ---
adb devices                                             # trạng thái thiết bị
adb reverse --list                                      # 2 rule phải có khi Host chạy
adb reverse --remove-all                                # mô phỏng mất tunnel
adb kill-server ; adb start-server                      # mô phỏng adb restart
adb shell am start -n com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity
adb shell am force-stop com.displaybridge.probe         # kill app cứng
adb logcat -s VideoDecoderActivity VideoStreamClient ControlSocketClient
```

### 0.2 Nhắc lại các fix đang verify (SUT = state-under-test SAU khi land)

- **RC1-FIX (session 18)**: `AdbReverseManager.EnsureReverse` re-apply reverse tunnels **1 lần ngay trong `Start()`** (fire-and-forget) + **mỗi `OnAdbPollTick` (7s) khi `Connected`**. Poll đầu tiên sau `AdbPollFirstDelay=20s`.
- **RC-A (P0)**: `OnCapsReceived`/`ApplyVirtualDisplayResolution` **BỎ QUA** `EnsureReady` (devcon restart ~4s) + `RecreateFrameSourceForNewResolution` khi `(width,height,hz,codec)` **KHÔNG đổi** và pipeline còn sống → chỉ gửi `CONFIG`.
- **RC-B (P0)**: `RecreateFrameSourceForNewResolution`/`CreateFrameSource` chạy trong **worker-thread + timeout 15s**; nếu native `Init()` **treo** → vẫn dựng lại `VideoStreamServer` (GDI/stub) để **video port sống**, không chết vĩnh viễn.
- **RC-C (P1)**: `OnCapsReceived` có **gate/lock chống reentrancy** — CAPS trùng đang xử lý thì bỏ qua, không để 2 luồng devcon/native chồng nhau.

### 0.3 Phân loại khả năng tự động hoá

| Tag | Nghĩa |
|---|---|
| `[HEADLESS]` | Chạy được không cần tablet/driver thật — qua `tests/Integration.Tests` (`tools/fake-device`, `OnAdbPollTick` internal, `FakeAdbDeviceChecker`, fake `IAdbReverseManager`, `RecordingDriverManager`, `frameSourceFactory` override). CI/agent chạy được. |
| `[ON-DEVICE]` | Cần tablet thật + adb nhưng **script hoá** được (`adb shell/am/logcat`, `schtasks`, `Get-PnpDevice`) — không cần bấm tay. Host vẫn cần elevated → chạy 1 lần trong desktop session. |
| `[HW-MANUAL]` | Cần thao tác vật lý (rút/cắm cáp, reboot, bấm UAC) hoặc quan sát mắt (con trỏ, khung hình). Không tự động hoá được. |

**Severity**: `P0` = chặn kết nối / wedge Host / port chết vĩnh viễn · `P1` = suy giảm nặng, tự hồi chậm · `P2` = khó chịu, có workaround · `P3` = cosmetic.

---

## 1. Ma trận Startup-Ordering & Lifecycle (TC-001 → TC-014)

### 1.1 Bảng tổng

| ID | Tên | Severity | Loại | Kỳ vọng (post-fix) 1 dòng |
|----|-----|----------|------|----------------------------|
| TC-001 | PC-first (happy path) | P0 | `[ON-DEVICE]` | Connect ≤ vài giây, đúng native resolution, 1 lần driver setup |
| TC-002 | Mobile-first (case đã hỏng) | P0 | `[ON-DEVICE]` | Host KHÔNG wedge; connect sau khi Host lên; đúng 1 driver restart |
| TC-003 | Simultaneous (song song) | P1 | `[ON-DEVICE]` | Handshake hội tụ, không stacked CAPS, không double restart |
| TC-004 | Force-stop + relaunch giữa stream (reconnect storm) | P0 | `[HEADLESS]`+`[ON-DEVICE]` | Mỗi reconnect KHÔNG restart driver (RC-A); không wedge (RC-C) |
| TC-005 | PC Host restart giữa stream | P1 | `[ON-DEVICE]` | App retry 1s tự reconnect sau khi Host lên lại; tunnel tự apply |
| TC-006 | `adb kill-server` giữa stream | P1 | `[ON-DEVICE]` | Poll kế tiếp tự start server + re-apply reverse ≤7s → app reconnect |
| TC-007 | Rút/cắm USB giữa stream | P1 | `[HW-MANUAL]` | Sau cắm lại: reverse tự hồi ≤7s, driver enable lại, app reconnect |
| TC-008 | Reboot tablet | P1 | `[HW-MANUAL]` | Sau boot + mở app: reverse tự hồi, connect lại sạch |
| TC-009 | Reboot PC | P1 | `[HW-MANUAL]` | Sau boot + Host chạy: reverse apply từ `Start()`, app connect |
| TC-010 | `adb reverse --remove-all` giữa stream | P0 | `[ON-DEVICE]` | Tunnel tự hồi ≤7s (poll), app reconnect, không cần thao tác tay |
| TC-011 | CAPS gửi lại, resolution KHÔNG đổi | P0 | `[HEADLESS]` | KHÔNG devcon restart, KHÔNG recreate pipeline — chỉ CONFIG (RC-A) |
| TC-012 | Đổi codec giữa stream (H264→HEVC) | P1 | `[ON-DEVICE]` | MODE_CHANGE gửi trước, native re-init đúng HEVC, decoder flush đúng |
| TC-013 | Driver ở trạng thái disabled lúc Host start | P1 | `[ON-DEVICE]` | ADB Connected → poll enable lại "VDD by MTT", capture chạy |
| TC-014 | Freeform/floating window CAPS | P1 | `[HW-MANUAL]` | CAPS = kích thước panel VẬT LÝ (getRealMetrics), không theo window |

### 1.2 Chi tiết từng case

#### TC-001 — PC-first (happy path baseline)
- **Precondition**: Tablet cắm USB, `adb devices` = `device`; app chưa mở; không tunnel nào (`adb reverse --remove-all` để bắt đầu sạch).
- **Steps**:
  ```powershell
  adb reverse --remove-all
  Start-Process ".\DisplayBridge.Host.exe"     # bấm Yes UAC
  ```
  ```bash
  # ≤ vài giây sau khi Host lên:
  adb reverse --list          # PHẢI thấy 2 rule 29500/29501
  adb shell am start -n com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity
  ```
- **Expected (post-fix)**: `adb reverse --list` có đủ 2 rule (apply ngay trong `Start()`, không chờ 20s). App qua waiting-overlay → khung hình thật; log Host: 1 lần `DriverManager.EnsureReady(...)` + `CONFIG sent`, đúng native resolution (vd 3000x1920). FPS overlay Android hiển thị.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]` (đo mắt FPS overlay = `[HW-MANUAL]`).

#### TC-002 — Mobile-first (case đã hỏng ở v2.1 addendum)
- **Precondition**: Host TẮT; tablet cắm; `adb reverse --remove-all`.
- **Steps**:
  ```bash
  adb shell am start -n com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity
  # App bắt đầu retry mỗi 1s (VideoStreamClient + ControlSocketClient), ECONNREFUSED lặp
  ```
  ```powershell
  Start-Sleep 10 ; Start-Process ".\DisplayBridge.Host.exe"   # Host lên SAU
  Get-Content "$env:TEMP\displaybridge-host.log" -Wait -Tail 60
  ```
- **Expected (post-fix)**: Host KHÔNG dừng vĩnh viễn tại `"dang tao lai frame source..."`. CAPS đầu tiên xử lý đúng 1 lần: đúng 1 `EnsureReady`/restart driver, không có CAPS thứ 2 chạy chồng (RC-C gate). Nếu native `Init()` treo, video port vẫn sống ≤15s (RC-B). App reconnect (retry 1s) và lên khung hình.
- **Pre-fix (để đối chiếu)**: log kẹt tại tạo-lại-frame-source 8+ phút, control port vẫn nhận CAPS mới → devcon restart chồng lần 2.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`. Proxy `[HEADLESS]`: fake-device connect → gửi CAPS trong khi CAPS trước còn "in flight" (fake DriverManager chậm) → assert `EnsureReady` gọi đúng 1 lần.

#### TC-003 — Simultaneous (Host + app khởi động gần như cùng lúc)
- **Precondition**: Host tắt, app tắt, `adb reverse --remove-all`.
- **Steps**: mở Host và `am start` app trong cửa sổ ~1s của nhau (chạy 2 lệnh song song ở 2 shell).
- **Expected (post-fix)**: Bất kể ai lên trước, kết cục = TC-001 (hội tụ). Reverse apply từ `Start()`; nếu app dial trước khi server bound → retry 1s bắc cầu. Chỉ 1 CAPS→CONFIG handshake hiệu lực, không stacked restart (RC-C). Log Host không có 2 luồng `RecreateFrameSource...` xen kẽ.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-004 — Force-stop + relaunch giữa stream (reconnect storm)
- **Precondition**: Đang stream ổn định (TC-001 xong), resolution KHÔNG đổi giữa các lần reconnect.
- **Steps**:
  ```bash
  for i in 1 2 3 4 5; do \
    adb shell am force-stop com.displaybridge.probe ; sleep 1 ; \
    adb shell am start -n com.displaybridge.probe/com.displaybridge.video.VideoDecoderActivity ; sleep 3 ; \
  done
  ```
- **Expected (post-fix)**: Mỗi relaunch gửi lại CAPS cùng resolution → **KHÔNG** trigger devcon restart (RC-A: caps không đổi, pipeline sống → chỉ CONFIG). `ControlSocketServer` single-active-client: client cũ EOF được dọn, client mới thay chỗ; không kẹt reentrancy (RC-C). Không có "bão restart", Host ổn định suốt storm. App lên khung hình lại sau mỗi lần trong vài giây.
- **Pre-fix**: mỗi reconnect = 1 driver restart (bão restart), + reentrancy 2 CAPS chồng.
- **Severity**: P0 · **Loại**: `[HEADLESS]` (fake-device connect/disconnect loop + `RecordingDriverManager` assert 0 lần `EnsureReady` sau lần đầu) + `[ON-DEVICE]`.

#### TC-005 — PC Host restart giữa stream
- **Precondition**: Đang stream.
- **Steps**:
  ```powershell
  Stop-Process -Name DisplayBridge.Host -Force
  Start-Sleep 5
  Start-Process ".\DisplayBridge.Host.exe"
  ```
- **Expected (post-fix)**: Khi Host chết, app thấy video EOF + control EOF → waiting-overlay "Mất kết nối, đang thử lại...", retry 1s. Host mới `Start()` → re-apply reverse ngay → app reconnect, CAPS→CONFIG lại, khung hình trở lại. (Lưu ý: reverse rule sống trong adb server nên có thể vẫn còn từ Host cũ — Host mới `EnsureReverse` idempotent, không lỗi.)
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-006 — `adb kill-server` giữa stream
- **Precondition**: Đang stream.
- **Steps**:
  ```bash
  adb kill-server
  # đợi ≤ 1 poll interval (7s). AdbDeviceChecker chạy `adb devices` sẽ auto start-server lại.
  adb reverse --list        # kiểm tra tunnel đã được re-apply chưa
  ```
- **Expected (post-fix)**: adb server restart flush toàn bộ reverse rule → app ECONNREFUSED tạm. Poll tick kế tiếp: `CheckConnectionState` shell `adb devices` (auto khởi động lại server) → `Connected` → `EnsureReverseTunnels` re-apply 2 rule. Trong ≤7s tunnel hồi, app retry 1s reconnect. Log Host: dòng `ADB reverse: ...` (chỉ log khi thực sự apply).
- **Edge cần chú ý**: nếu `CheckConnectionState` trả `Indeterminate` (adb.exe timeout đúng lúc restart) thì tick đó KHÔNG re-apply — phải hồi ở tick sau. Assert hồi trong ≤2 tick (≤14s).
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-007 — Rút/cắm USB giữa stream
- **Precondition**: Đang stream.
- **Steps**: rút cáp USB tablet vật lý → đợi 10s → cắm lại → `adb devices` xác nhận `device` lại.
- **Expected (post-fix)**: Rút → adb mất device, reverse flush; poll thấy `Disconnected` → auto-disable "VDD by MTT" (session 15). Cắm lại → poll thấy `Connected` → (a) enable lại driver, (b) re-apply reverse ≤7s. App retry 1s reconnect. Không cần thao tác tay `adb reverse`.
- **Severity**: P1 · **Loại**: `[HW-MANUAL]` (rút/cắm vật lý).

#### TC-008 — Reboot tablet
- **Precondition**: Đang stream hoặc đã pair.
- **Steps**: `adb reboot` → chờ boot xong → `adb devices` = `device` → mở lại app.
- **Expected (post-fix)**: Reboot flush reverse + kết nối. Sau boot, poll `Connected` → re-apply reverse; mở app → connect sạch như TC-001. Nếu watcher (feature b) đã cài → có thể tự bắn notification (xem TC-201).
- **Severity**: P1 · **Loại**: `[HW-MANUAL]`.

#### TC-009 — Reboot PC
- **Precondition**: Host đang chạy (lý tưởng đã cài Task Scheduler auto-start — xem TC-102).
- **Steps**: reboot Windows → đăng nhập → (auto-start hoặc mở tay Host) → `adb devices`.
- **Expected (post-fix)**: Sau logon, Host `Start()` apply reverse ngay. Nếu auto-start (TC-102) hoạt động thì không cần thao tác tay. App connect như TC-001.
- **Severity**: P1 · **Loại**: `[HW-MANUAL]`.

#### TC-010 — `adb reverse --remove-all` giữa stream (mô phỏng mất tunnel)
- **Precondition**: Đang stream ổn định.
- **Steps**:
  ```bash
  adb reverse --remove-all       # xoá tunnel giữa phiên
  adb reverse --list             # rỗng ngay sau lệnh
  # đợi ≤7s (1 poll interval)
  adb reverse --list             # PHẢI có lại 2 rule
  ```
- **Expected (post-fix)**: Ngay sau remove: app ECONNREFUSED, waiting-overlay reconnect. Trong ≤7s (poll tick, `Connected`) → `EnsureReverseTunnels` re-apply → `adb reverse --list` có lại 2 rule → app retry 1s tự reconnect. Không cần thao tác tay. Đây là Validation Plan #2 của RCA-v2.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`.

#### TC-011 — CAPS gửi lại với resolution KHÔNG đổi (guard RC-A)
- **Precondition**: Đang stream ở resolution X (vd 3000x1920@120 codec HEVC), pipeline sống.
- **Steps** (`[HEADLESS]` — Integration.Tests):
  1. fake-device hoàn tất CAPS handshake lần 1 (X).
  2. fake-device gửi CAPS lần 2 với **đúng** `(width,height,hz,codec)=X`.
  3. Assert qua `RecordingDriverManager`: `EnsureReady` gọi **đúng 1 lần** (lần 1); lần 2 **0 lần**. Assert `VideoStreamServer` KHÔNG bị Stop()/recreate (BoundPort giữ nguyên, `FrameWritten` không đứt). Host chỉ gửi lại `CONFIG` (hoặc no-op) cho lần 2.
- **Expected (post-fix)**: Không devcon restart, không `Thread.Sleep(3000)`, không recreate frame source cho CAPS trùng → video KHÔNG giật/đứt. Đây là chốt chặn regression của RC-A (log 19:57: cùng 3000x1920 vẫn restart).
- **Pre-fix**: mọi CAPS đều `EnsureReady`+recreate kể cả trùng.
- **Severity**: P0 · **Loại**: `[HEADLESS]` (đây là case dễ tự động hoá nhất — nên nằm trong smoke).

#### TC-012 — Đổi codec giữa stream (H264 → HEVC qua Mobile Settings)
- **Precondition**: Đang stream H264 (`mimeType=video/avc`); tablet hỗ trợ HEVC decode.
- **Steps**: mở floating settings button → chọn Encode = H.265 → "Áp dụng" (app restart, gửi CAPS mới với `supportedCodecs=[1]`).
- **Expected (post-fix)**: CAPS mới đổi codec → `ChooseCodec`=HEVC. Vì đây là restart app (reconnect) nên caps "đổi" (codec khác) → được phép re-init native đúng HEVC; `SendModeChangeBeforeVideoPipelineRecreate` gửi MODE_CHANGE TRƯỚC khi rebuild pipeline; Android `onModeChange` set `mimeType=video/hevc` rồi `recoverDecoderAfterError()` tạo lại decoder HEVC. Không mismatch codec. (Nếu resolution cũng không đổi, RC-A chỉ skip khi codec CŨNG không đổi — ở đây codec đổi nên KHÔNG skip, đúng.)
- **Severity**: P1 · **Loại**: `[ON-DEVICE]` (chọn menu = `[HW-MANUAL]` nhẹ; có thể `am start` với extra thay cho tap).

#### TC-013 — Driver ở trạng thái disabled lúc Host start (giao thoa auto-disable session 15)
- **Precondition**: "VDD by MTT" đang disabled (vd Host phiên trước tắt driver do không có ADB); tablet CẮM lại rồi mới mở Host.
- **Steps**:
  ```powershell
  Get-PnpDevice -Class Display | Where-Object FriendlyName -match 'Virtual Display'  # Status = Error/Disabled trước
  Start-Process ".\DisplayBridge.Host.exe"
  ```
- **Expected (post-fix)**: `adb devices`=Connected → tại poll transition (`_lastKnownAdbConnected` flip) → `EnableDevice()` bật lại "VDD by MTT"; `EnsureExtendTopology` + CAPS handshake → capture chạy trên VDD (không mirror màn chính). `Get-PnpDevice` sau đó Status=`OK`.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]` (enable/disable cần Admin thật; verify logic `[HEADLESS]` qua `AdbAutoDisableTests` + `RecordingDriverManager`).

#### TC-014 — Freeform / floating window CAPS (Honor "App Multiplier")
- **Precondition**: `resizeableActivity` không bị pin fullscreen; mở app trong cửa sổ freeform/floating.
- **Steps**: mở `VideoDecoderActivity` ở chế độ floating window (kích thước cửa sổ ≠ panel, vd 1519x1517) → quan sát log CAPS.
- **Expected (post-fix)**: CAPS report **kích thước panel VẬT LÝ** (`Display.getRealMetrics`, đã normalize landscape), vd `3000x1920`, KHÔNG phải kích thước cửa sổ. PC cấu hình virtual display đúng native, không resize theo window (regression session 17→18). Log Android: `Sending CAPS: 3000x1920@...`.
- **Severity**: P1 · **Loại**: `[HW-MANUAL]` (cần Honor App Multiplier thật; logic getRealMetrics có thể instrument test on-device).

---

## 2. Feature mới — Test cases

### 3a. PC Host auto-start tại Windows logon (Task Scheduler, admin app)

> Bối cảnh: Host cần Administrator (`app.manifest requireAdministrator`). Auto-start dùng scheduled task **"Run with highest privileges"** + trigger **At log on** để launch elevated mà KHÔNG bung UAC lúc logon. Một "admin app"/installer đăng ký task này. Tên task giả định: `DisplayBridgeHostAutostart`.

#### Bảng tổng

| ID | Tên | Severity | Loại |
|----|-----|----------|------|
| TC-101 | Đăng ký task (happy path) | P1 | `[ON-DEVICE]` |
| TC-102 | Logon thực sự launch Host elevated | P0 | `[HW-MANUAL]` |
| TC-103 | Đăng ký lặp lại (idempotent) | P2 | `[ON-DEVICE]` |
| TC-104 | Gỡ/tắt task | P2 | `[ON-DEVICE]` |
| TC-105 | Single-instance (task fire khi Host đã chạy) | P0 | `[ON-DEVICE]` |
| TC-106 | Không đăng nhập / user khác | P2 | `[HW-MANUAL]` |
| TC-107 | Đăng ký khi KHÔNG có quyền admin | P1 | `[ON-DEVICE]` |
| TC-108 | Đường dẫn exe stale sau khi di chuyển | P2 | `[ON-DEVICE]` |

#### TC-101 — Đăng ký task (happy path)
- **Precondition**: Chạy admin app/installer với quyền Administrator.
- **Steps**:
  ```powershell
  # sau khi admin app đăng ký:
  Get-ScheduledTask -TaskName DisplayBridgeHostAutostart | Format-List *
  schtasks /Query /TN DisplayBridgeHostAutostart /V /FO LIST
  ```
- **Expected**: Task tồn tại; Trigger = `At log on` (current user hoặc BUILTIN\Users tuỳ thiết kế); `Principal.RunLevel = Highest`; Action = đường dẫn tuyệt đối `DisplayBridge.Host.exe`; `Settings` không có "Stop if runs longer than" cắt ngang (hoặc để mặc định dài). State = `Ready`.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-102 — Logon thực sự launch Host elevated (không UAC)
- **Precondition**: TC-101 đã đăng ký.
- **Steps**: đăng xuất/đăng nhập lại (hoặc reboot) → không bấm UAC → kiểm tra:
  ```powershell
  Get-Process DisplayBridge.Host -ErrorAction SilentlyContinue
  Test-Path "$env:TEMP\displaybridge-host.log"   # log mới tạo sau logon
  ```
- **Expected**: Host chạy tự động elevated (log xuất hiện, servers listen 29500/29501, reverse apply nếu có tablet) mà KHÔNG hiện UAC prompt (đặc tính "highest privileges" của scheduled task bỏ qua UAC cho task này). Đây là điều kiện tiên quyết cho TC-009.
- **Severity**: P0 · **Loại**: `[HW-MANUAL]` (logon/reboot thật).

#### TC-103 — Đăng ký lặp lại (idempotent)
- **Steps**: chạy admin-app đăng ký **2 lần** liên tiếp.
- **Expected**: Không tạo task trùng lặp (`DisplayBridgeHostAutostart (1)`...). Lần 2 update-in-place hoặc no-op, thoát sạch (`schtasks /Query` chỉ 1 entry). Không lỗi "task already exists" chưa xử lý.
- **Severity**: P2 · **Loại**: `[ON-DEVICE]`.

#### TC-104 — Gỡ / tắt task
- **Steps**: admin app "Disable autostart" → `Get-ScheduledTask -TaskName DisplayBridgeHostAutostart`.
- **Expected**: Task bị xoá (`schtasks /Query` không còn) hoặc State=`Disabled`; logon kế tiếp KHÔNG auto-start Host. Không để lại task mồ côi.
- **Severity**: P2 · **Loại**: `[ON-DEVICE]`.

#### TC-105 — Single-instance (task fire khi Host đã chạy tay) — NEGATIVE
- **Precondition**: Host đã chạy tay (đã bind 29500/29501); task logon fire (mô phỏng: `schtasks /Run /TN DisplayBridgeHostAutostart`).
- **Steps**:
  ```powershell
  Start-Process ".\DisplayBridge.Host.exe"            # instance 1
  schtasks /Run /TN DisplayBridgeHostAutostart        # mô phỏng logon trigger
  Start-Sleep 3
  (Get-Process DisplayBridge.Host).Count              # PHẢI = 1
  ```
- **Expected**: KHÔNG có 2 Host cùng chạy. Instance thứ 2 phải phát hiện port 29500/29501 đã bị bind (hoặc single-instance mutex) và thoát sạch với log rõ ("Host da chay san, thoat"). Nếu KHÔNG có guard → instance 2 sẽ `SocketException` khi bind + có thể apply reverse chồng → **fail case**.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`. **Unresolved**: cơ chế single-instance (mutex vs port-probe) chưa được định nghĩa — xem §5.

#### TC-106 — Không đăng nhập / user khác đăng nhập
- **Steps**: đăng nhập bằng user KHÁC (không phải user đã đăng ký task, nếu task scope per-user).
- **Expected**: Định nghĩa rõ scope: nếu task per-user → chỉ auto-start cho user đã đăng ký (đúng thiết kế); nếu machine-wide → cân nhắc rủi ro chạy Host khi user không dùng tablet. Verify không crash, không double-launch giữa 2 user session.
- **Severity**: P2 · **Loại**: `[HW-MANUAL]`.

#### TC-107 — Đăng ký khi KHÔNG có quyền admin — NEGATIVE
- **Steps**: chạy admin-app registration từ process **không** elevated.
- **Expected**: Đăng ký thất bại SẠCH với thông báo rõ ("Cần chạy bằng quyền Administrator để tạo scheduled task highest-privileges") — KHÔNG tạo task nửa vời (task tồn tại nhưng RunLevel=Limited → logon sau vẫn bung UAC). Assert: `Get-ScheduledTask` không có entry sau lần fail.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]` (chạy non-elevated verify được bằng `WindowsPrincipal.IsInRole(Administrator)==false`, cùng pattern `DriverManagerDisableEnableRealDevconTests`).

#### TC-108 — Đường dẫn exe stale sau khi di chuyển thư mục — NEGATIVE
- **Precondition**: TC-101 đăng ký với path A; sau đó move thư mục cài sang path B.
- **Steps**: `schtasks /Run /TN DisplayBridgeHostAutostart` → kiểm tra.
- **Expected**: Task báo lỗi launch (exit code ≠ 0 trong `schtasks /Query /V`), Host không chạy. Thiết kế nên: admin-app re-register khi phát hiện path đổi, HOẶC log cảnh báo. Verify không "chạy im lặng nhầm exe" nào khác.
- **Severity**: P2 · **Loại**: `[ON-DEVICE]`.

---

### 3b. Android background PC-presence watcher

> Bối cảnh: một background component (WorkManager/foreground service) định kỳ **probe** xem PC Host có reachable qua reverse tunnel device-local không. Khi PC "xuất hiện" → post notification "PC sẵn sàng — nhấn để kết nối"; tap = mở `VideoDecoderActivity`. Ràng buộc từ brief: (1) stale tunnel (PC absent) → probe thấy **instant-EOF** → KHÔNG notify; (2) KHÔNG spam notification; (3) watcher **pause khi đang stream**.

#### Heuristic probe (định nghĩa để test bám vào)

```
connect(127.0.0.1:29501, timeout=1s)
 ├─ ECONNREFUSED            → KHÔNG có tunnel (adb reverse trống)      → ABSENT
 ├─ connect OK, rồi read()  → EOF/-1 trong <300ms  = stale tunnel      → ABSENT
 │                            (adb reverse còn nhưng Host chết → host
 │                             side refuse → adb đóng socket device-side)
 └─ connect OK, read()      → timeout (không data, socket còn mở >300ms)→ PRESENT
                              (ControlSocketServer accept & chờ CAPS,
                               không đóng, không gửi gì trước)
```

#### Bảng tổng

| ID | Tên | Severity | Loại |
|----|-----|----------|------|
| TC-201 | PC reachable → notify + tap mở activity | P1 | `[ON-DEVICE]` |
| TC-202 | Stale tunnel (PC chết, reverse còn) → instant-EOF → KHÔNG notify | P0 | `[ON-DEVICE]` |
| TC-203 | Không tunnel (ECONNREFUSED) → KHÔNG notify | P1 | `[ON-DEVICE]` |
| TC-204 | PC reachable liên tục → chỉ 1 notification (không spam) | P0 | `[ON-DEVICE]` |
| TC-205 | Đang stream → watcher pause, KHÔNG notify | P0 | `[ON-DEVICE]` |
| TC-206 | Kết thúc stream → watcher resume | P1 | `[ON-DEVICE]` |
| TC-207 | PC flap (mất→có lại) → clear rồi re-notify, debounce | P1 | `[ON-DEVICE]` |
| TC-208 | Tap notification → Intent extras đúng + single instance | P1 | `[ON-DEVICE]` |
| TC-209 | Sống sót Doze / MagicOS aggressive kill | P1 | `[HW-MANUAL]` |
| TC-210 | Phân biệt instant-EOF vs handshake chậm | P2 | `[ON-DEVICE]` |

#### TC-201 — PC reachable → notify + tap mở activity (happy path)
- **Precondition**: Watcher đang chạy nền; app KHÔNG ở foreground (không stream); Host TẮT ban đầu.
- **Steps**:
  ```bash
  adb reverse --remove-all
  # (Host tắt) — chưa có gì
  # Bật Host (reverse apply 29500/29501, ControlSocketServer accept & chờ CAPS)
  # đợi 1 chu kỳ probe của watcher
  adb logcat -s PcPresenceWatcher    # tên logtag giả định
  ```
- **Expected**: Watcher probe 29501 → PRESENT (socket mở, không EOF) → post notification "PC sẵn sàng — nhấn để kết nối". Tap notification → `VideoDecoderActivity` mở với extras đúng (xem TC-208) → connect stream.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-202 — Stale tunnel, PC absent → instant-EOF → KHÔNG notify (NEGATIVE, cốt lõi)
- **Precondition**: Tạo stale tunnel: bật Host (reverse apply) → tắt Host **cứng** nhưng reverse rule vẫn còn (rule sống trong adb server, không bị xoá khi Host exit).
- **Steps**:
  ```powershell
  Start-Process ".\DisplayBridge.Host.exe" ; Start-Sleep 4   # apply reverse
  Stop-Process -Name DisplayBridge.Host -Force               # Host chết, reverse còn
  ```
  ```bash
  adb reverse --list         # 29500/29501 VẪN còn (stale)
  # đợi vài chu kỳ probe
  adb logcat -s PcPresenceWatcher
  ```
- **Expected**: Probe connect OK (adb chấp nhận) nhưng read() trả EOF <300ms (host side refuse) → phân loại ABSENT → **KHÔNG** post notification. Đây là case brief nhấn mạnh — reverse rule tồn tại KHÔNG được coi là "PC có mặt".
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`. Proxy on-device-instrumentation: mở socket tới 29501 khi Host down, assert `read()` == -1 nhanh; watcher không đổi state sang PRESENT.

#### TC-203 — Không tunnel (ECONNREFUSED) → KHÔNG notify (NEGATIVE)
- **Precondition**: `adb reverse --remove-all`, Host tắt.
- **Steps**: đợi vài chu kỳ probe, xem logcat.
- **Expected**: connect → ECONNREFUSED → ABSENT → không notify. Không crash watcher, không spam log lỗi.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-204 — PC reachable liên tục → chỉ 1 notification (NO SPAM)
- **Precondition**: Host chạy ổn định, watcher chạy, app không foreground.
- **Steps**: để yên qua **nhiều** chu kỳ probe (vd 5 chu kỳ).
- **Expected**: Chỉ **1** notification tồn tại (cùng notification id, không stack). Watcher chỉ post khi state chuyển ABSENT→PRESENT (edge-triggered), không re-post mỗi chu kỳ khi vẫn PRESENT. Assert: `adb shell dumpsys notification | grep displaybridge` chỉ 1 entry.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`.

#### TC-205 — Đang stream → watcher pause, KHÔNG notify (NEGATIVE)
- **Precondition**: `VideoDecoderActivity` đang foreground streaming (Host present); watcher chạy.
- **Steps**: trong lúc stream, để watcher chạy qua nhiều chu kỳ; xem logcat + notification shade.
- **Expected**: Watcher **suspend** probe (hoặc bỏ qua post) khi đang stream — không post notification "PC sẵn sàng" vì user hiển nhiên đang dùng. Cơ chế pause gợi ý: theo `VideoStreamService` active / activity foreground flag. Assert: 0 notification mới trong lúc stream.
- **Severity**: P0 · **Loại**: `[ON-DEVICE]`.

#### TC-206 — Kết thúc stream → watcher resume
- **Steps**: thoát `VideoDecoderActivity` (back/finish) → `VideoStreamService` dừng → đợi 1 chu kỳ probe.
- **Expected**: Watcher resume probe; nếu PC vẫn PRESENT có thể notify lại cho phiên mới (state đã reset khi rời stream). Không kẹt ở trạng thái paused vĩnh viễn.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-207 — PC flap (mất rồi có lại) → clear + re-notify có debounce
- **Steps**: Host up (notify) → user bỏ qua notification → Host down (`Stop-Process`) → Host up lại.
- **Expected**: Khi PC ABSENT → clear/thu hồi notification cũ (không để notification "sẵn sàng" trong khi PC đã chết). Khi PRESENT lại → notify lại, nhưng có **debounce** (không bắn liên tục nếu PC nhấp nháy nhanh trong vài giây — vd chỉ notify sau khi PRESENT ổn định ≥1 chu kỳ).
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-208 — Tap notification → Intent extras đúng + single instance
- **Steps**: tap notification → kiểm tra activity mở.
- **Expected**: `VideoDecoderActivity` mở với `EXTRA_HOST=127.0.0.1`, `EXTRA_PORT=29500`, `EXTRA_CONTROL_PORT=29501` (khớp default). Nếu activity đã mở sẵn → không tạo instance chồng (singleTop/reorder). Assert: `adb shell dumpsys activity activities | grep VideoDecoderActivity` chỉ 1.
- **Severity**: P1 · **Loại**: `[ON-DEVICE]`.

#### TC-209 — Sống sót Doze / HONOR MagicOS aggressive kill
- **Precondition**: watcher đã cài; để máy idle vào Doze / khoá màn hình lâu (CONTEXT-BRIEF §5: HONOR kill background mạnh).
- **Steps**: idle tablet ≥30 phút màn tắt → bật Host → xem watcher có còn probe & notify không.
- **Expected**: Watcher vẫn hoạt động (dùng foreground service hoặc periodic WorkManager với constraint hợp lý) — không bị MagicOS giết im lặng khiến notification không bao giờ đến. Nếu chỉ dùng probe định kỳ nền thuần → có rủi ro bị kill; verify cơ chế chọn chịu được Doze.
- **Severity**: P1 · **Loại**: `[HW-MANUAL]`.

#### TC-210 — Phân biệt instant-EOF vs handshake chậm (EDGE)
- **Precondition**: Host chạy nhưng phản hồi chậm (vd đang `Thread.Sleep(3000)` trong recreate pipeline) — socket accept nhưng chưa trao đổi gì.
- **Expected**: Watcher phân loại PRESENT (socket mở, không EOF) — KHÔNG nhầm thành stale chỉ vì chưa có data. Ngưỡng grace (300ms) đủ ngắn để nhạy nhưng đủ dài để không nhầm host-đang-bận thành stale. Verify: probe với Host present-nhưng-bận vẫn ra PRESENT.
- **Severity**: P2 · **Loại**: `[ON-DEVICE]`.

---

## 4. Smoke Test Subset (≤ 6 case, < 10 phút / build)

Chạy sau **mỗi build**. Thứ tự tối ưu: 2 case `[HEADLESS]` chạy trước (nhanh, không cần tablet) để fail-fast, rồi 4 case `[ON-DEVICE]` với tablet cắm sẵn.

| # | ID | Vì sao trong smoke | Loại | ~Thời gian |
|---|----|--------------------|------|-----------|
| 1 | **TC-011** | Chốt regression RC-A (CAPS trùng KHÔNG restart driver) — case dễ hỏng lại nhất, test headless nhanh | `[HEADLESS]` | ~30s |
| 2 | **TC-004** | Reconnect storm không stacked-restart / không wedge (RC-A+RC-C) — headless loop | `[HEADLESS]` | ~1 phút |
| 3 | **TC-002** | Mobile-first KHÔNG wedge Host — bug đầu bảng của cả RCA-v2 | `[ON-DEVICE]` | ~2 phút |
| 4 | **TC-010** | Mất tunnel giữa stream tự hồi ≤7s (RC1-FIX poll) — Validation Plan #2 | `[ON-DEVICE]` | ~1 phút |
| 5 | **TC-001** | Happy path PC-first vẫn nguyên vẹn (không regress khi thêm guard) | `[ON-DEVICE]` | ~2 phút |
| 6 | **TC-202** | Watcher KHÔNG notify trên stale tunnel — negative cốt lõi của feature b | `[ON-DEVICE]` | ~2 phút |

**Smoke subset IDs**: `TC-011, TC-004, TC-002, TC-010, TC-001, TC-202`

Tiêu chí PASS toàn smoke: (a) `adb reverse --list` luôn có 2 rule khi Host chạy; (b) log Host KHÔNG kẹt tại `"dang tao lai frame source..."`; (c) `RecordingDriverManager` không gọi `EnsureReady` cho CAPS trùng; (d) watcher không post notification khi stale.

---

## Document Lineage

| Version | Document | Focus | Status |
|---------|----------|-------|--------|
| v1 | RCA-v1-resolution-stuck-800x600.md | Resolution handshake | Done |
| v2 | RCA-v2-android-connect-adb-reverse-lifecycle.md | ADB reverse lifecycle + RC-A/B/C wedge | Active (fix đang land) |
| — | **TEST-v1-connection-ordering-matrix.md** (this) | Test matrix verify RC-A/B/C + 2 feature mới | Draft |

---

## 5. Unresolved Questions

1. **Single-instance Host** (TC-105): cơ chế chống 2 Host cùng chạy (named Mutex vs port-bind probe vs task-condition "chỉ chạy nếu chưa chạy") chưa được định nghĩa — cần chốt trước khi ship auto-start, nếu không TC-105 sẽ fail (2 instance đụng port + reverse chồng).
2. **Task scope** (TC-106): auto-start per-user hay machine-wide? Ảnh hưởng hành vi đa-user và rủi ro chạy Host khi user không dùng tablet.
3. **Watcher probe port**: probe 29501 (control) hay 29500 (video)? Control server accept-và-chờ (không gửi gì) hợp với heuristic PRESENT hơn; cần xác nhận video server cũng không gửi byte nào trước frame đầu để dùng chung ngưỡng 300ms.
4. **Ngưỡng grace instant-EOF** (300ms ở TC-202/TC-210): là giá trị đề xuất, cần đo thực tế độ trễ đóng socket của adb reverse khi host-side refuse để chỉnh (có thể cần 100–500ms tuỳ máy).
5. **Watcher survive Doze** (TC-209): chọn foreground service (tốn 1 notification thường trực) hay periodic WorkManager (có thể bị MagicOS trễ/kill)? Trade-off UX vs độ tin cậy chưa chốt.
6. **RC-A định nghĩa "unchanged"**: so sánh `(width,height)` hay đủ `(width,height,hz,codec)`? TC-011/TC-012 giả định đủ 4 field — cần khớp với implementation cuối cùng của RC-A để test assertion không lệch.
