# CONTEXT-BRIEF-v2-m1-m3-m4-tasks

**Created**: 2026-07-03  
**Purpose**: Agent context brief cho M1 (video pipeline), M3 (input 2 chiều), M4 (settings module) — giảm context lag, tránh code trùng  
**Scope**: Inventory code đã có + protocol + settings + input design + task breakdown  
**Read time**: ~3 phút  

---

## 1. Inventory Code Đã Có (M0)

### PC Host (.NET/WPF)

| File | Class/Method | Mục đích |
|------|-------------|---------|
| `pc-host/src/DisplayBridge.Host/App.xaml.cs` | `App : Application` | Tray icon system entry point (scaffold M0.1) |
| `pc-host/src/DisplayBridge.Host/MainWindow.xaml.cs` | `MainWindow : Window` | Placeholder UI (M0.1) |
| `pc-host/src/DisplayBridge.Core/DisplayBridge.Core.csproj` | (project) | C# library, Protocol.Generated subfolder |

### PC Native (C++)

| File | Content | Mục đích |
|------|---------|---------|
| `pc-host/src/DisplayBridge.Native/DisplayBridge.Native.vcxproj` | (project) | C++ module — **NOT BUILD-ABLE YET** (M0.1 xác nhận: máy thiếu MSVC v143 + Windows SDK) |
| `pc-host/src/DisplayBridge.Native/DisplayBridgeNative.cpp` | (skeleton) | Placeholder |

### Android Client (Kotlin)

| File | Class/Method | Mục đích |
|------|-------------|---------|
| `android-client/app/src/main/java/com/displaybridge/protocol/generated/` | (6 files: Messages, ControlMessages, HandshakeMessages, MessageFraming, ProtocolCommon, WireIO) | Auto-generated từ schema.yaml (M0.4) |
| `android-client/app/src/test/java/com/displaybridge/protocol/MessagesRoundtripTest.kt` | `MessagesRoundtripTest` | Protocol roundtrip test (M0.4) |
| `android-client/app/build.gradle.kts` | (project) | Gradle config, **Kotlin plugin added M0.4 fix** |

### Tools (Existing Infrastructure)

| Folder | Content | Mục đích |
|--------|---------|---------|
| `tools/protocol-schema/` | `schema.yaml`, `generate.py`, `codegen_csharp.py`, `codegen_kotlin.py` | Protocol codegen (M0.4) — **Python là nguồn sự thật, đã fix drift bug** |
| `tools/bench-transport/` | `bench-adb.ps1`, `bench-ncm.ps1`, `android-echo-server.sh`, `RESULTS.md` | Throughput benchmark (M0.2) — **ADB 31–33 MB/s verified** |
| `tools/fake-device/` | (empty) | TCP simulator — ready for M4 integration tests |
| `tools/latency-harness/` | (empty) | E2E latency measurement — ready for M1 instrumentation |
| `windows-driver/` | (empty) | IddCx driver — will be forked Virtual-Display-Driver |

**Key**: M0 scaffold ONLY — tất cả logic chính M1–M5 chưa viết.

---

## 2. Protocol Đã Định Nghĩa (schema.yaml)

### Message Types (Control Socket)

| Type (hex) | Name | Chiều | Payload | Semantics |
|-----------|------|-------|---------|-----------|
| 0x01 | CAPS | tablet→PC | width(u16), height(u16), dpi(u16), supportedHz(list<u8>), supportedCodecs(list<u8>), maxTouchPoints(u8) | Handshake step 1: tablet announces capabilities |
| 0x02 | CONFIG | PC→tablet | codec(u8), width(u16), height(u16), hz(u8), bitrateKbps(u32) | Handshake step 2: PC chosen config |
| 0x10 | SETTING_REQUEST | tablet→PC | key(u8), value(varint) | Tablet asks PC to change 1 setting (floating button) |
| 0x11 | CONFIG_UPDATE | PC→tablet | settings(list<keyvalue>) | PC live-apply settings (no decoder flush) |
| 0x12 | MODE_CHANGE | PC→tablet | width(u16), height(u16), hz(u8), codec(u8), csd(bytes) | Force re-mode: tablet flush decoder, await IDR |
| 0x13 | MODE_ACK | tablet→PC | status(u8) | Tablet confirms decoder ready (0=ok, 1=unsupported, 2=error) |
| 0x14 | STATS | tablet→PC | fps(u8), decodeMs(u16), queueDepth(u8), dropped(u32) | 1Hz telemetry for ABR + HUD |

### Video Frame Header (Not Type-Prefixed)

| Field | Type | Meaning |
|-------|------|---------|
| len | u32 | Payload byte length |
| flags | u8 | bit0=IDR/keyframe, bits1-7=reserved |
| ptsUs | u64 | Presentation timestamp, microseconds |
| (payload) | bytes | H.264/HEVC NAL units |

**Status**: Messages for SETTING_REQUEST, CONFIG_UPDATE, MODE_CHANGE, MODE_ACK, STATS **already defined in schema** (M0.4). Touch/pointer events **NOT YET** in protocol — **NEED TO ADD in M3** (will extend SETTING_REQUEST or add new TOUCH_EVENT type).

---

## 3. Settings Catalog Chốt (From PLAN-v2 §4.1 + §5)

### 3.1 Display Group

| Setting | Values | Default | Apply Type |
|---------|--------|---------|-----------|
| **Resolution** | Max(native) · 75% · 50% · Custom W×H | Native | ⟳ re-mode |
| **Refresh rate** | 60 / 90 / 120 Hz — **120Hz MAX (144Hz dropped 2026-07-03)** | 120 | ⟳ re-mode |
| **Orientation** | Landscape / Portrait / Auto | Landscape | ⟳ re-mode |
| **Monitor position** | Left/Right/Top/Bottom | Right | ✎ Windows API |
| **Scale hint** | 100–300% | 250% (290dpi) | ✎ Windows setting |

### 3.2 Streaming Group

| Setting | Values | Default | Apply Type |
|---------|--------|---------|-----------|
| **Codec** | Auto (HEVC→H.264) / force HEVC / force H.264 | Auto | ⟳ re-mode |
| **Quality preset** | Low 0.04bpp · Balanced 0.07 · High 0.10 · Ultra 0.14 · Custom Mbps | High | ⚡ live |
| **FPS cap** | ≤ refresh rate chosen | = refresh | ⚡ live |
| **Adaptive bitrate** | on/off | on | ⚡ live |
| **Priority** | Latency-first / Smooth-first | Latency | ⚡ live |

### 3.3 Input Group (M3 will implement)

| Setting | Values | Default | Apply Type |
|---------|--------|---------|-----------|
| **Touch to PC** | on/off | on | ⚡ live |
| **Input mode** | Cursor-only / Touch-only / **Hybrid** | **Hybrid** | ⚡ live |
| **Pen pressure** | on/off + curve (γ 0.5–2.0) | on, γ=1 | ⚡ live |
| **Keyboard forward** | on/off | on | ⚡ live |

### 3.4 Connection / Diagnostics / Tablet-local

| Group | Settings | Apply |
|-------|----------|-------|
| **Connection** | Transport (Auto/ADB/NCM), Auto-connect, Ports | ⟳ reconnect |
| **Diagnostics** | Stats overlay (off/mini/full), Latency HUD, Log level | ⚡ live |
| **Tablet-local** | Keep-on, Brightness, Immersive fullscreen, Floating button | local |

### 3.5 Apply Types

| Type | Meaning | Flow |
|------|---------|------|
| **⚡ live** | Take effect immediately, don't interrupt stream | `CONFIG_UPDATE` sent mid-stream, no decoder flush |
| **⟳ re-mode** | Requires decoder reconfigure, ~1-2s downtime | `MODE_CHANGE` → decoder flush + reconfigure → wait `MODE_ACK` → stream resume from IDR |
| **✎ Windows API** | PC-side only, no protocol message | Direct `SetDisplayConfig()` or settings-store update |
| **local** | Tablet-only, not synced to PC | Stored in tablet DataStore, no cross-send |

---

## 4. Thiết Kế Hybrid Input (From RESEARCH-v1)

### 4.1 Windows Input Stack (3 Tầng)

```
TẦNG 3: WM_GESTURE (OS suy ra: pinch/zoom/rotate/2-finger-tap/edge-swipe)
  ↑
TẦNG 2: WM_POINTER (hiện đại, per-contact, InteractionContext)
  ↑
TẦNG 1: POINTER_TOUCH_INFO (contact thô) ← DisplayBridge inject vào đây
```

**Key insight**: DisplayBridge injects at **Tầng 1** → Tầng 2–3 (gesture recognition) miễn phí từ Windows. Không cần code gesture riêng cho pinch/zoom.

### 4.2 Hybrid Mode Logic (M3 sẽ code InputModeClassifier)

```
1 ngón chạm + giữ yên/chậm  → CURSOR mode
                              (SendInput(MOUSEINPUT), dễ xem, chính xác)
                              
2+ ngón cùng lúc             → TOUCH mode  
                              (InjectSyntheticPointerInput(PT_TOUCH))
                              
Vuốt từ mép (<20px edge)     → TOUCH mode bắt buộc
                              (để Action Center/Task View nhận edge-swipe)
```

### 4.3 APIs

| Mode | API | Header | Notes |
|------|-----|--------|-------|
| **Cursor** | `SendInput(INPUT_MOUSE)` | `<winuser.h>` | Standard Win32, P/Invoke via user32.dll from C# |
| **Touch** | `InjectSyntheticPointerInput(PT_TOUCH, multi-point)` | `<winuser.h>` | Max 256 points, P/Invoke from C# via user32.dll |

**Implementation**: Both available from **C# via P/Invoke** `user32.dll` — **NOT require C++** (misunderstanding in PLAN-v1, clarified in RESEARCH-v1).

---

## 5. Build Environment Status

| Component | Status | Action |
|-----------|--------|--------|
| **.NET 8 (C# WPF)** | ✅ Build OK — M0.1 host project builds clean | No action needed |
| **Gradle/Kotlin (Android)** | ✅ Build OK — M0.4 protocol OK, Kotlin plugin added | No action needed |
| **MSVC C++ (driver)** | ❌ **CANNOT BUILD** — M0.1 reports: missing MSVC v143 + Windows SDK | **User must run**: `Visual Studio Installer` → workload "Desktop development with C++" → before M2 driver work |
| **Python (codegen)** | ✅ Works — M0.4 fixed, `generate.py` verified byte-for-byte | No action needed |
| **Windows Driver Kit (WDK)** | ❓ **UNCHECKED** — needed for M2 IddCx driver | User to verify after MSVC install |

---

## 6. M1 / M3 / M4 Task Breakdown (From PLAN-v2 §8)

### M1 — Video Pipeline PoC Mirror (weeks 2–4)

| ID | Task | Deliverables | DoD |
|----|----|--------------|-----|
| M1.1 | PC: DDA capture → MF HEVC encoder (NVENC, AVLowLatencyMode) | C#: DDA source wrapper, HEVC encoder init + live frame push | golden: pattern → ffprobe valid, PSNR ≥40dB |
| M1.2 | Android: MediaCodec decoder + SurfaceView + KEY_LOW_LATENCY | Kotlin: decoder setup, SurfaceView render, low-latency flag | instrumented: 3K120 decode, P95 decodeMs log |
| M1.3 | Ghép stream qua adb forward (mirror primary desktop) | E2E: CAPS→CONFIG→stream, 30+ FPS sustained | E2E: latency-harness 1st run, <60ms target |
| M1.4 | STATS 1Hz + drop-to-latest render loop | Kotlin: STATS collection, render FSM | instrumented: inject stall → no backlog |
| **DoD** | Mirror 3K@60 for 30min, memory flat (no leak), latency P50 recorded | (all sub-items) | **Memory RSS stable, P50 <60ms** |

### M3 — Input 2 Chiều (weeks 6–7)

| ID | Task | Deliverables | DoD |
|----|----|--------------|-----|
| M3.1 | Android: capture MotionEvent (multi-touch 10pt, stylus pressure/hover) | Kotlin: event serializer, 10-point concurrent tracking | unit: serializer; instrumented: `adb shell input` replay match |
| M3.2 | PC: coordinate mapper (rotation 0/90/180/270 × DPI × monitor pos) | C#: MapTouchCoord class, 12-case matrix unit test | unit: 48 cases (4 rot × 3 DPI × 4 pos), ≤1px error |
| M3.3 | PC: InjectSyntheticPointerInput + SendInput keyboard | C#/P/Invoke: CursorInjector + TouchInjector + InputModeClassifier | manual matrix: Paint pressure, pinch-zoom, right-click long-press |
| **DoD** | Paint brush force correct all 4 rotations; touch accurate all angles | (all sub-items) | **MT-03 paint pass, pressure correct** |

### M4 — Settings Module + UX (weeks 7–9)

| ID | Task | Deliverables | DoD |
|----|----|--------------|-----|
| M4.1 | Settings store PC (JSON) + UI WPF per catalog §3 | C#: SettingsStore class (load/save/migrate), WPF UI bindings | unit: config migration v0→v1; UI smoke test |
| M4.2 | CONFIG_UPDATE (live) + MODE_CHANGE (re-mode) full flow | C#: settings applier FSM, live vs re-mode router | integration: fake-device bitrate 80→20→80, 0 frame error |
| M4.3 | Floating button tablet + SETTING_REQUEST | Kotlin: floating button UI, send SETTING_REQUEST; C#: PC handler | E2E: tablet FPS change → PC applied → log |
| M4.4 | Auto-connect wizard + reconnect FSM | C#: USB plug/unplug detect, auto-APK install flow, FSM | scripted: 20 cycles plug/unplug, auto-reconnect ≤5s; Android Studio recovery |
| M4.5 | HONOR-specific: foreground service + whitelist guidance | Kotlin: ForegroundService type connectedDevice, user guidance UI | soak: 2h screen-off, service alive |
| **DoD** | Non-ADB user can self-setup via wizard; all settings apply correctly per type | (all sub-items) | **MT-01–MT-10 checklist 100%** |

---

## 7. APP_BUILD/ Directory

| Item | Status | Purpose |
|------|--------|---------|
| `APP_BUILD/` (parent folder) | ✓ Exists, empty | **M5.5 output only** — will hold: `DisplayBridgeHost-vX.Y.Z.exe` (PC installer/portable) + `DisplayBridgeClient-vX.Y.Z.apk` (Android release) |
| `.gitignore` | TBD | Should ignore `APP_BUILD/` artifact binaries (discuss with user) |

**NOT a task for M1–M4** — reserved for M5.5 final packaging.

---

## Summary Stats

- **Code inventory**: 2 C# WPF files (scaffold) + 6 Kotlin generated files (protocol) + empty Native folder (blocked on toolchain)
- **Protocol**: 7 message types defined, 1 video frame header — **touch/pointer event NOT YET** in schema (add in M3)
- **Settings**: 20+ individual settings cataloged, 2 apply types (live/re-mode) chốt
- **Input design**: Hybrid Cursor/Touch via Windows Tầng 1 injection — pinch/zoom automatic (Windows handles it)
- **Build**: .NET/Kotlin/Python ready; C++ blocked on toolchain (user action needed before M2)
- **Tasks**: M1 (5 sub-tasks) + M3 (3 sub-tasks) + M4 (5 sub-tasks) — detailed DoD for each

**Next session**: Start M1 from M1.1 (PC DDA→NVENC encoder pipeline).

