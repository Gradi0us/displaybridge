# PLAN-v2-tablet-second-display-adb

**Created**: 2026-07-03
**Research Target**: Kế hoạch chi tiết xây dựng app (PC Windows + Android) — settings resolution/FPS + full test plan; tham khảo Spacedesk mobile; hiệu chỉnh theo THIẾT BỊ THẬT đã probe qua ADB
**Mode**: Planning
**Level**: L3-Analyze
**Language**: vi
**Context**: PLAN-v1 (+addendum v1.1) · live device probe qua ADB · Spacedesk manual
**Status**: Completed
**Researcher**: Claude Agent (deep-research skill)

---

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Thiết bị thật: HONOR ROD2-W09, panel 3K 144Hz — KHÔNG phải 4K** | 3000×1920, refresh 60/90/120/144Hz, density 400, Android 16 (SDK 36) | `adb shell wm size` + `dumpsys display` (live probe 2026-07-03) | CAO |
| 2 | **Bài toán băng thông nhẹ hơn v1.1 dự tính ~40%** | 3K144 HEVC ≈ 75 Mbps ≈ 9.3 MB/s — nằm gọn trong cả T1 (ADB burst) lẫn T2 (NCM) | Fermi §6 trên số panel thật | CAO |
| 3 | **H.264 chỉ lên được ~90Hz ở 3K — 120/144Hz BẮT BUỘC HEVC** | 3K@90 = 2.03M MB/s ≤ giới hạn L5.2 (2.07M); 3K@120 = 2.7M vượt chuẩn | Tính theo MaxMBPS H.264 spec (ITU-T H.264 Annex A) | CAO |
| 4 | **Decoder tablet dư sức** | Qualcomm SM8635 (SD 8s Gen 3, "cliffs"): HEVC đo 260–1200fps @1080p → quy đổi ~3K ≈ 90–410fps | `/vendor/etc/media_codecs_performance_cliffs_v1.xml` (đọc từ máy thật) | CAO |
| 5 | **PC encoder dư sức** | RTX 5060 Laptop (Blackwell NVENC) encode HEVC/AV1 4K120+ low-latency | `Win32_VideoController` probe + NVIDIA NVENC support matrix | CAO |
| 6 | **Spacedesk settings model = chuẩn tham khảo tốt** | Resolution (native + custom list) · FPS custom · Quality slider (default 70) · Rotation · on-the-fly qua floating button | manual.spacedesk.net (ScreenResolution, Frameratepersecond, ClientDisplaySettings) | CAO |
| 7 | **Settings chia 2 loại: live-apply vs re-mode** | Bitrate/quality/fps-cap đổi ngay giữa stream; resolution/refresh/codec cần mode-switch (EDID update hoặc decoder reset ~1–2s) | IddCx mode model + MediaCodec reconfigure requirement | CAO |
| 8 | **HONOR MagicOS kill background hung hãn** | Cần foreground service + user whitelist battery; risk R10 mới | HONOR/Huawei dontkillmyapp reports | TRUNG BÌNH |

---

## Table of Contents

1. [Scan Scope & Stats](#1-scan-scope--stats)
2. [Live Device Profile (probe thật)](#2-live-device-profile-probe-thật)
3. [Spacedesk Reference — mổ xẻ Settings](#3-spacedesk-reference--mổ-xẻ-settings)
4. [Thiết kế Settings Module](#4-thiết-kế-settings-module)
5. [Video Profiles chốt theo thiết bị](#5-video-profiles-chốt-theo-thiết-bị)
6. [Fermi & INVERSION](#6-fermi--inversion)
7. [Kiến trúc repo & module](#7-kiến-trúc-repo--module)
8. [Kế hoạch chi tiết M0→M5 (WBS + DoD)](#8-kế-hoạch-chi-tiết-m0m5-wbs--dod)
9. [TEST PLAN đầy đủ](#9-test-plan-đầy-đủ)
10. [RICE — quyết định còn mở](#10-rice--quyết-định-còn-mở)
11. [Risks cập nhật](#11-risks-cập-nhật)
12. [UC5 Viewpoints + Maturity Ladder](#12-uc5-viewpoints--maturity-ladder)
13. [Kết luận + Synthesis Diagram](#13-kết-luận--synthesis-diagram)
14. [Sources & References](#14-sources--references)

---

## 1. Scan Scope & Stats

- **Target**: Spacedesk mobile (settings reference) + kế hoạch build chi tiết app 2 chiều kèm test plan
- **Mode/Level/Type**: Planning / L3 / PLAN · **Lang**: vi
- **Sources**: Source 1 = PLAN-v1(+v1.1) · **Source 2 = LIVE DEVICE qua ADB (5 lệnh probe)** · Source 3 = 2 web searches (spacedesk manual)
- Kế thừa từ v1/v1.1: kiến trúc 5 tầng, transport 3 tầng T1/T2/T3, protocol §6, risk R1–R9 — KHÔNG lặp lại ở đây, chỉ delta.

---

## 2. Live Device Profile (probe thật)

> Đây là điểm khác biệt lớn nhất với v1: mọi con số dưới đây là **evidence CONFIG/METRIC-based từ máy thật**, không còn là giả định.

| Thuộc tính | Giá trị | Nguồn lệnh |
|-----------|---------|-----------|
| Model | HONOR ROD2-W09 (MagicPad series), serial AL9SBB4622000114 | `adb devices -l` |
| OS | Android 16, SDK 36, build 10.0.0.151(C00E135R103P3) | `getprop` |
| Panel | **1920×3000 portrait-native** (landscape 3000×1920), density 400, ~290 dpi | `wm size` / `wm density` |
| Refresh modes | **60 / 90 / 120 / 144 Hz** (4 mode, cùng resolution), HDR10/HLG support | `dumpsys display` |
| SoC | Qualcomm **SM8635 = Snapdragon 8s Gen 3** ("cliffs"/pineapple platform) | `getprop ro.soc.model` |
| HEVC decoder | `c2.qti.hevc.decoder`: measured 1080p = 260–1200 fps | `media_codecs_performance_cliffs_v1.xml` |
| AVC decoder | `c2.qti.avc.decoder`: measured 1080p (1920×1088) = 120–500 fps | cùng file |
| USB negotiated speed | **CHƯA ĐỌC ĐƯỢC** (permission denied `/sys/class/udc/*/current_speed`) → đo bằng benchmark T0.5 | probe fail |
| PC | AMD Ryzen 9 8945HX + **NVIDIA RTX 5060 Laptop** (driver 32.0.15.8205) | `Win32_VideoController` |

**Hệ quả trực tiếp lên spec** (chi tiết §5):
1. Mục tiêu "4K120" của v1.1 đổi thành **native 3K@144** — panel không phải 4K, và 3K144 còn "ngon" hơn cho màn phụ (đúng pixel-perfect, không scale).
2. Decoder + encoder cả 2 đầu đều dư → gate go/no-go duy nhất còn lại là **throughput USB thực tế** (T0.5).
3. Android 16 → `FEATURE_LowLatency` chắc chắn có; dùng được cả `MediaCodecInfo.getSupportedPerformancePoints()` để tự probe trong app.

---

## 3. Spacedesk Reference — mổ xẻ Settings

Spacedesk (datronicsoft) là app màn-phụ phổ biến nhất về mặt cấu hình được. Catalog settings của nó (từ manual chính thức):

```
SPACEDESK SETTINGS MAP (từ manual.spacedesk.net)
│
├── Android VIEWER (client)
│   ├── Resolution
│   │   ├── "Use native android device resolution" (checkbox)
│   │   └── Custom Resolution (combo list, tối đa 2 resolution active)
│   ├── Quality / Performance
│   │   ├── Image quality slider (default 70)          ← JPEG-style quality
│   │   └── Custom FPS rate
│   ├── Rotation (auto / lock)
│   ├── Brightness slider (default 0 offset)
│   ├── Floating button → đổi settings ON-THE-FLY giữa phiên
│   └── Touchscreen / keyboard input toggle
│
└── Windows DRIVER CONSOLE (server)
    ├── Display Mode per client (attach/detach)
    ├── Resolutions… → Other/Custom (tối đa 4096×2160)
    └── Compression quality
```

**Bài học rút ra (Socratic — cái gì đáng bắt chước, cái gì nên làm khác?):**

| Spacedesk làm | Đánh giá | Ta làm |
|---------------|----------|--------|
| Settings ở CẢ 2 đầu, có thể lệch nhau | ❌ gây confusion (quality trên viewer ≠ compression trên console) | **Single source of truth ở PC Host**, tablet chỉ hiển thị + vài local setting (brightness, keep-on) |
| Floating button đổi on-the-fly | ✅ UX tốt | Có — nút nổi trên tablet + tray menu PC, phân loại live-apply vs re-mode (§4.3) |
| Quality slider 0–100 (JPEG-era) | ⚠️ mơ hồ với video codec | Đổi thành **preset bitrate** (Low/Balanced/High/Ultra) + custom Mbps, hiển thị bpp tương ứng |
| Custom FPS tự do | ✅ | FPS chọn theo **mode panel thật** (60/90/120/144) — không cho giá trị vô nghĩa |
| Resolution list tự do (kể cả không khớp panel) | ⚠️ gây blur do scale | Default **native 3000×1920**; các preset thấp hơn giữ đúng tỉ lệ 25:16 (2250×1440, 1500×960) |
| Không có latency/stats HUD | ❌ thiếu | Có Stats overlay (fps, bitrate, latency P50/P95, dropped) — phục vụ cả test plan §9 |

---

## 4. Thiết kế Settings Module

### 4.1 Catalog settings (MECE: Display / Streaming / Input / Connection / Diagnostics)

| Nhóm | Setting | Giá trị | Default | Loại apply |
|------|---------|--------|---------|-----------|
| **Display** | Resolution | **[CẬP NHẬT lần 2] KHÔNG còn chọn lựa — 100% động, luôn = native resolution của thiết bị đang kết nối** (tự phát hiện qua CAPS handshake). Bỏ hẳn preset 75%/50% và Custom W×H — không hiện UI chọn resolution nữa | (tự động, không có default để chọn) | ⟳ re-mode (tự trigger khi CAPS đổi) |
| | Refresh rate | **60 / 90 / 120 Hz — 120Hz LÀ MAX, bỏ hẳn 144Hz** (v2 addendum §15 hạ 144Hz xuống gated đã lỗi thời — nay bỏ hẳn theo yêu cầu, không giữ toggle experimental) | 120 | ⟳ re-mode |
| | Orientation | Landscape / Portrait / Auto-rotate | Landscape | ⟳ re-mode (đổi WxH EDID) |
| | Vị trí monitor | trái/phải/trên/dưới màn chính | Phải | ✎ Windows API (SetDisplayConfig) |
| | Scale gợi ý | 100–300% | 250% (290dpi) | ✎ Windows setting |
| **Streaming** | Codec | Auto (HEVC→H.264) / force HEVC / force H.264 | Auto | ⟳ re-mode |
| | Quality preset | Low 0.04bpp · Balanced 0.07 · High 0.10 · Ultra 0.14 · Custom Mbps | High | ⚡ live |
| | FPS cap | ≤ refresh đã chọn | = refresh | ⚡ live |
| | Adaptive bitrate | on/off | on | ⚡ live |
| | Ưu tiên | Latency-first (drop-to-latest) / Smooth-first | Latency | ⚡ live |
| **Input** | Touch → PC | on/off | on | ⚡ live |
| | **Input mode** | **Cursor-only / Touch-only / Hybrid (auto)** — xem [RESEARCH-v1-windows-touch-gesture-mechanism.md](RESEARCH-v1-windows-touch-gesture-mechanism.md) | **Hybrid** | ⚡ live |
| | Pen pressure | on/off + pressure curve (γ 0.5–2.0) | on, γ=1 | ⚡ live |
| | Keyboard forward | on/off | on | ⚡ live |
| **Connection** | Transport | Auto (NCM→ADB) / force ADB / force NCM | Auto | ⟳ reconnect |
| | Auto-connect khi cắm cáp | on/off | on | ✎ host |
| | Ports | video/control | 29500/29501 | ⟳ reconnect |
| **Diagnostics** | Stats overlay | off/mini/full | off | ⚡ live |
| | Latency HUD (embedded timestamp) | on/off | off | ⚡ live |
| | Log level | error/info/debug | info | ⚡ live |
| **Tablet-local** | Keep screen on · Brightness override · Immersive fullscreen · Floating button | — | on/—/on/on | local |

### 4.2 Phân phối settings (single source of truth)

```
┌─ PC Host App ────────────────┐                 ┌─ Android App ───────────────┐
│ settings.json (persisted)    │                 │ DataStore (chỉ tablet-local)│
│   │                          │                 │                             │
│   ├─⚡ live ──► CONFIG_UPDATE ═══ control ═══► apply ngay (bitrate/fps/HUD) │
│   │            (không ngắt stream)             │                             │
│   └─⟳ re-mode ─► MODE_CHANGE ═══ control ═══► decoder flush+reconfigure    │
│                │                               │  → ACK → stream lại từ IDR │
│                └─► IddCx UpdateModes / re-plug monitor (PC side)            │
└──────────────────────────────┘                 └─────────────────────────────┘
Tablet floating button đổi setting → gửi SETTING_REQUEST về PC → PC là người
quyết định + phát CONFIG_UPDATE/MODE_CHANGE ngược lại (không bao giờ 2 nguồn lệnh)
```

### 4.3 Protocol messages bổ sung (mở rộng §6.2 của PLAN-v1)

| Type (control socket) | Chiều | Payload | Semantics |
|----------------------|-------|---------|-----------|
| `SETTING_REQUEST=0x10` | tablet→PC | key(u8), value(varint) | tablet xin đổi (từ floating button) |
| `CONFIG_UPDATE=0x11` | PC→tablet | list<key,value> | live-apply, không ngắt video |
| `MODE_CHANGE=0x12` | PC→tablet | w,h,hz,codec,csd | tablet flush decoder, chờ IDR mới |
| `MODE_ACK=0x13` | tablet→PC | status | sẵn sàng nhận stream mới |
| `STATS=0x14` | tablet→PC | fps,decodeMs,queueDepth,dropped | 1Hz, nuôi ABR + HUD |

---

## 5. Video Profiles chốt theo thiết bị

> **[CẬP NHẬT 2026-07-03 lần 2 — sau feedback user]**: **Resolution KHÔNG còn preset (75%/50%/Custom đã bỏ hẳn)** — chỉ còn **1 mốc DUY NHẤT: động 100% theo độ phân giải native của thiết bị đang kết nối**, đọc từ `CAPS` handshake lúc runtime. Không có UI chọn resolution nữa — app tự nhận và tự áp dụng. **FPS giữ nguyên quyết định trước: trần 120Hz, bỏ hẳn 144Hz** (không đổi). Bảng dưới dùng số HONOR ROD2-W09 làm ví dụ — công thức áp dụng cho MỌI thiết bị Android kết nối vào.

| Profile | Res@Hz | Codec (bắt buộc) | Bitrate preset High | Băng thông | Ghi chú |
|---------|--------|------------------|--------------------|-----------|---------|
| **Native (mốc duy nhất, VD: 3000×1920@120)** | native@min(120, HzMax thiết bị) | HEVC L5.1/5.2 (hoặc H.264 nếu ≤90Hz, xem chứng minh dưới) | ~69 Mbps | 8.6 MB/s | **Mốc DUY NHẤT** — CONFIRMED GO trên ROD2-W09 (M0.3). Không còn Standard-75%/50%/Custom |

Đã bỏ khỏi thiết kế: P-Ultra 144Hz, Standard-75%, Standard-50%, Custom W×H tự nhập — theo đúng yêu cầu user 2026-07-03 "resolution nên là 1 setting động ở mốc độ phân giải của màn hình thiết bị kết nối, các mốc preset khác không cần".

**Chứng minh "≥120Hz@3K bắt buộc HEVC, không dùng được H.264"** (vẫn đúng, quyết định FPS không đổi):
```
H.264 Level 5.2 MaxMBPS = 2,073,600 macroblock/s (ITU-T H.264 Table A-1)
3008×1920 → (188×120) = 22,560 MB/frame
  @90Hz  : 22,560×90  = 2,030,400 ≤ 2,073,600  ✓ (vừa khít, H.264 dùng được)
  @120Hz : 22,560×120 = 2,707,200 > 2,073,600  ✗ VƯỢT CHUẨN → bắt buộc HEVC
HEVC Level 5.1/5.2 đủ margin cho 3K@120 (đã CONFIRMED GO thật qua M0.3 PerformancePoint probe)
```

**Logic tính resolution động (áp dụng cho cả M4 Settings và M2 virtual display driver)**:
```
1. Handshake CAPS → đọc (nativeWidth, nativeHeight, supportedHz[]) thật của thiết bị đang cắm
2. targetHz = min(120, max(supportedHz[]))   // trần cứng 120, không phụ thuộc thiết bị hỗ trợ cao hơn
3. Duy nhất 1 profile = (nativeWidth, nativeHeight, targetHz) — KHÔNG có mốc nào khác
4. M2: Host app tự ghi lại <resolutions> trong C:\VirtualDisplayDriver\vdd_settings.xml
   thành đúng (nativeWidth, nativeHeight) mỗi khi CAPS đổi (thiết bị khác kết nối),
   trigger restart driver (không phải reboot Windows) để áp dụng
5. Không còn khái niệm Custom user tự nhập W/H — nếu cần thử nghiệm resolution khác,
   sửa tay file XML (out of scope UI v1.0)
```

---

## 6. Fermi & INVERSION

### 6.1 Fermi — latency budget @120Hz (cập nhật từ v1 với panel thật)

```
compose-wait ½ frame @120Hz : ~4.2 ms   (v1 @60Hz là 8ms → LỢI khi tăng Hz)
NVENC HEVC low-latency      : 2–4 ms    (RTX 5060 Blackwell)
transport 8.6MB/s stream    : 2–3 ms    (frame ~72KB @ ≥30MB/s hiệu dụng)
decode SD 8s Gen 3          : 5–10 ms   (1080p đo 260-1200fps → 3K ~4-11ms/frame)
present ½ vsync @120 + panel: ~4.2 + 5 ms
────────────────────────────────────────
TỔNG P50 ước tính           : 22–30 ms  (tốt hơn mục tiêu 45ms của v1 ~40%)
```

### 6.2 INVERSION (3 giả định bị lật bằng dữ liệu thật)

**#1 "Thiết bị cần 4K120 như user mô tả"** → Probe thật: panel 3000×1920. → Chân lý: stream native 3K là pixel-perfect, mọi tính toán 4K của v1.1 trở thành ceiling dư dả; KHÔNG thêm mode 4K vào EDID (scale xuống gây blur + tốn 30% băng thông vô ích).

**#2 "Quality slider 0–100 như Spacedesk là chuẩn tốt"** → Spacedesk dùng JPEG-style quality (manual: default 70) vì nó nén ảnh tĩnh; với video codec, "quality 70" không map sang tham số encoder nào có nghĩa. → Chân lý: settings phải nói ngôn ngữ codec: **bpp/bitrate preset** (bảng §4.1), kèm hiển thị Mbps thực tế.

**#3 "Cứ để user chỉnh resolution tự do như Spacedesk (tới 4096×2160)"** → Panel thật chỉ có 1 mode gốc 3000×1920; mọi resolution khác đều bị scale (blur chữ — tệ nhất cho use-case đọc code/document trên màn phụ). → Chân lý: default native, preset khác phải giữ đúng aspect 25:16, và UI ghi rõ "non-native, sẽ mềm chữ".

---

## 7. Kiến trúc repo & module

```
APP_share/                                  ← root (theo chỉ định của user)
├── docs/                                   ← PLAN/SPEC/TASK/RCA...
├── pc-host/                                ← Visual Studio solution
│   ├── src/DisplayBridge.Host/             ← C# WPF: tray, settings UI, wizard
│   ├── src/DisplayBridge.Core/             ← C#: protocol, adb manager, ABR, settings store
│   ├── src/DisplayBridge.Native/           ← C++: MF encoder (NVENC), IddCx IPC, InjectSyntheticPointerInput
│   └── tests/  (Core.Tests xUnit · Native.Tests GoogleTest · Integration.Tests)
├── windows-driver/                         ← fork Virtual-Display-Driver (IddCx)
│   └── (EDID theo native resolution thiết bị đang kết nối, mode list ≤120Hz + shared-texture export + plug API)
├── android-client/                         ← Gradle project, Kotlin
│   └── app/src/ main / test / androidTest
├── tools/
│   ├── latency-harness/                    ← embedded-timestamp đo E2E tự động
│   ├── bench-transport/                    ← adb raw socket + iperf3 NCM (T0.5)
│   └── fake-device/                        ← TCP simulator chạy unit/integration không cần tablet
└── APP_BUILD/                              ← [MỚI] output build cuối cùng, KHÔNG chứa source
    ├── DisplayBridgeHost-vX.Y.Z.exe         ← PC installer/portable exe (M5.5)
    └── DisplayBridgeClient-vX.Y.Z.apk       ← Android release APK, ký release (M5.5)
```

Nguyên tắc module (KISS + modularization rule): file >200 LOC tách; protocol định nghĩa **1 nơi duy nhất** (schema constants sinh code C# + Kotlin bằng script nhỏ, tránh drift 2 đầu). **`APP_BUILD/` chỉ chứa artifact build ra, version number của `.exe` và `.apk` phải khớp nhau trong cùng 1 lần release (M5.5).**

---

## 8. Kế hoạch chi tiết M0→M5 (WBS + DoD)

> Đổi từ "P-phase" (v1) sang milestone M0–M5 có kèm **test tương ứng từng task** (cột T→). Tổng ~10–12 tuần, 1 dev.

### M0 — Foundation + Benchmark gate (tuần 1–2)
| Task | Nội dung | T→ Test đi kèm |
|------|---------|----------------|
| M0.1 | Repo scaffold 4 project trên (build xanh CI local) | build script chạy sạch |
| M0.2 | **bench-transport**: raw socket qua `adb forward` đo MB/s; iperf3 qua NCM (`svc usb setFunctions ncm`) | báo cáo số ADB vs NCM trên đúng combo máy |
| M0.3 | **codec-probe app** (Android tối giản): dump `getSupportedPerformancePoints()` HEVC/AVC | assert ≥3000×1920@120 HEVC (go/no-go) |
| M0.4 | Protocol schema v0 + codegen C#/Kotlin | unit: roundtrip encode/decode 100% message types |
| **DoD** | Số throughput thật ghi vào docs; quyết định T1/T2; protocol lib 2 đầu pass fuzz cơ bản | |

### M1 — Video pipeline PoC mirror (tuần 2–4)
| Task | Nội dung | T→ Test |
|------|---------|---------|
| M1.1 | PC: DDA capture → MF HEVC encoder (NVENC, AVLowLatencyMode) | golden: encode pattern → ffprobe hợp lệ, PSNR≥40dB sau decode |
| M1.2 | Android: MediaCodec decoder + SurfaceView + KEY_LOW_LATENCY | instrumented: decode asset stream 3K120, đếm frame đủ, P95 decodeMs log |
| M1.3 | Ghép stream qua adb forward (mirror màn chính) | E2E: latency-harness lần đầu, mục tiêu <60ms |
| M1.4 | STATS 1Hz + drop-to-latest render loop | instrumented: inject stall giả → không tích lũy backlog |
| **DoD** | Mirror 3K@60 chạy 30 phút không leak (memory flat), latency P50 ghi nhận | |

### M2 — IddCx extend display (tuần 4–6, CRITICAL PATH)
| Task | Nội dung | T→ Test |
|------|---------|---------|
| M2.1 | Fork Virtual-Display-Driver, EDID 3000×1920 ×{60,90,120,144} | cài test-signed, 4 mode hiện đủ trong Display Settings |
| M2.2 | Shared D3D11 texture export driver→encoder (keyed mutex) | integration: frame counter liên tục, zero tear 10k frames |
| M2.3 | Plug/unplug API từ Host App | scripted: 50 chu kỳ plug/unplug không BSOD/leak |
| M2.4 | Chuyển pipeline M1 sang nguồn IddCx (bỏ DDA) | E2E latency lại: kỳ vọng ↓ (bỏ 1 hop copy) |
| **DoD** | Kéo cửa sổ sang tablet như monitor thật @120Hz; rút cáp monitor biến mất <2s | |

### M3 — Input 2 chiều (tuần 6–7)
| Task | Nội dung | T→ Test |
|------|---------|---------|
| M3.1 | Android capture MotionEvent (multi-touch 10 điểm, stylus pressure/hover) | unit: serializer; instrumented: `adb shell input` replay so khớp |
| M3.2 | PC coordinate mapper (rotation 0/90/180/270 × DPI × vị trí monitor) | unit: 12 case ma trận biến đổi, sai số ≤1px |
| M3.3 | InjectSyntheticPointerInput PEN/TOUCH + SendInput keyboard | manual matrix: Paint pressure, pinch-zoom browser, right-click long-press |
| **DoD** | Vẽ nét lực trong Krita; touch chính xác ở cả 4 hướng xoay | |

### M4 — Settings module + UX (tuần 7–9)
| Task | Nội dung | T→ Test |
|------|---------|---------|
| M4.1 | Settings store PC (JSON) + UI WPF theo catalog §4.1 | unit: migrate/validate config; UI smoke |
| M4.2 | CONFIG_UPDATE (live) + MODE_CHANGE (re-mode) full flow | integration fake-device: đổi bitrate không mất frame; đổi 144Hz→60Hz stream hồi phục <2s |
| M4.3 | Floating button tablet + SETTING_REQUEST | E2E: đổi FPS từ tablet, PC ghi nhận + apply |
| M4.4 | Auto-connect wizard: detect cắm cáp → install APK → launch → connect; reconnect FSM | scripted: 20 lần rút/cắm cáp, tự nối lại ≤5s; adb bị Android Studio chiếm → recovery |
| M4.5 | HONOR-specific: foreground service + hướng dẫn whitelist battery (MagicOS) | soak: 2h màn hình tablet không tự tắt/kill |
| **DoD** | Người không biết ADB tự setup được theo wizard; mọi settings hoạt động đúng loại apply | |

### M5 — Hardening + release nội bộ (tuần 9–11)
| Task | Nội dung | T→ Test |
|------|---------|---------|
| M5.1 | ABR theo STATS queueDepth | test kịch bản: bóp băng thông giả lập → bitrate hạ, không đứt hình |
| M5.2 | Intra-refresh thay keyframe định kỳ; tune profile Max@120Hz | E2E: latency P95 + dropped% cho Max/Standard-75/Standard-50 |
| M5.3 | Latency HUD + stats export CSV | so sánh các profile Max/Standard-75/Standard-50 |
| M5.4 | Soak 4h + full manual matrix (§9.5) | pass toàn bộ |
| **M5.5 (mới)** | **Build packaging**: PC → `.exe` installer (hoặc portable exe), Android → `.apk` release-signed; output cả 2 vào `APP_BUILD/` | build script chạy 1 lệnh ra đủ 2 file, version number khớp cả 2 phía |
| **DoD v1.0**: P95 ≤ 35ms @native-120Hz (mục tiêu kéo dãn: 30ms), 0 crash trong soak 4h, checklist §9.5 pass 100%, artifact build sẵn trong `APP_BUILD/` | | |

```
Tuần :  1    2    3    4    5    6    7    8    9    10   11
M0   : ████████
M1   :      ████████████
M2   :                ████████████        ← critical path (IddCx)
M3   :                          ████████
M4   :                               █████████████
M5   :                                ((        ████████████
Gate :  ▲T0.5 bench        ▲extend OK    ▲pen OK      ▲v1.0
```

---

## 9. TEST PLAN đầy đủ

### 9.1 Unit tests (chạy mỗi commit, không cần thiết bị)

| ID | Đối tượng | Case chính | Tool |
|----|-----------|-----------|------|
| U-01 | Protocol codec (C# + Kotlin sinh từ cùng schema) | roundtrip mọi message; partial-read; malformed → error không crash; version mismatch | xUnit / JUnit5 + jqwik fuzz |
| U-02 | Coordinate mapper | 4 rotation × 3 DPI × 4 vị trí monitor = 48 case, sai số ≤1px; normalized 0/65535 biên | xUnit theory |
| U-03 | EDID builder | checksum đúng chuẩn; 4 mode 3000×1920; parse lại bằng lib độc lập | GoogleTest |
| U-04 | ABR controller | chuỗi queueDepth tăng → hạ bitrate ≤1 bước/s; hồi phục có hysteresis | xUnit |
| U-05 | Settings store | load/save/migrate v0→v1; giá trị ngoài miền bị clamp | xUnit |
| U-06 | Reconnect FSM (2 đầu) | bảng chuyển trạng thái đầy đủ; timeout heartbeat 3s | JUnit + Turbine |
| U-07 | Touch serializer | pressure quantize 12-bit; 10 pointer đồng thời; hover/exit order | JUnit |

### 9.2 Integration tests (fake-device / loopback, CI được)

| ID | Kịch bản | Pass criteria |
|----|---------|---------------|
| I-01 | Handshake CAPS→CONFIG→stream với fake-device TCP | đúng thứ tự, timeout đúng |
| I-02 | Encoder→file→decoder (ffmpeg) golden | PSNR ≥40dB, đúng số frame |
| I-03 | CONFIG_UPDATE giữa stream (bitrate 80→20→80) | 0 frame lỗi, bitrate đo thay đổi ≤1s |
| I-04 | MODE_CHANGE 120→60→144 | stream hồi phục ≤2s, decoder không crash |
| I-05 | Ngắt socket đột ngột giữa NAL | 2 đầu về trạng thái chờ, reconnect sạch |
| I-06 | Fuzz control socket (random bytes) | không crash, log error đúng |

### 9.3 Instrumented tests (chạy trên ROD2-W09 thật)

| ID | Kịch bản | Pass criteria |
|----|---------|---------------|
| D-01 | PerformancePoint probe | HEVC ≥3K@120 (gate M0.3) |
| D-02 | Decode asset 3K120 60s | 7200 frame đủ, P95 decode ≤10ms |
| D-03 | KEY_LOW_LATENCY on/off so sánh | on ≤ off (ghi số vào docs) |
| D-04 | Foreground service sống qua screen-off 30' (MagicOS) | service alive, reconnect ngay |
| D-05 | Touch replay 1000 events | thứ tự + tọa độ khớp 100% |

### 9.4 E2E performance harness (tools/latency-harness)

**Cơ chế đo tự động** (áp dụng phương pháp Chen et al. 2011, tự động hoá bằng embedded timestamp):
```
PC vẽ vào góc màn ảo 1 dải barcode chứa (frameId, t_pc_us)
→ tablet decode xong, đọc pixel barcode trong onFrameRendered callback
→ latency = t_tablet_now − t_pc_us − clockOffset
   (clockOffset ước lượng NTP-style qua control socket, sai số <1ms)
→ log CSV: P50/P95/P99, dropped, duplicated — chạy 5 phút mỗi profile
```

| ID | Kịch bản | Target v1.0 |
|----|---------|-------------|
| E-01 | Latency P-High 3K120 static desktop | P50 ≤25ms, P95 ≤35ms |
| E-02 | Latency khi phát video fullscreen trên màn phụ | P95 ≤45ms, dropped ≤0.5% |
| E-03 | 144Hz P-Ultra | P95 ≤35ms nếu T0.5 ≥15MB/s |
| E-04 | Input lag touch→con trỏ PC (đo bằng camera 240fps, 20 mẫu) | trung bình ≤30ms |
| E-05 | Soak 4h stream + memory/CPU sampling 10s | RSS drift <5%, 0 crash, 0 permanent freeze |
| E-06 | Chaos: rút cáp 20 lần ngẫu nhiên trong 30' | 100% tự hồi phục ≤5s |
| E-07 | Throughput regression (bench-transport) mỗi release | không giảm >10% giữa version |

### 9.5 Manual test matrix (trước mỗi release)

| # | Kịch bản | Kỳ vọng |
|---|---------|---------|
| MT-01 | Kéo cửa sổ code editor sang tablet, đọc chữ nhỏ | chữ sắc nét (native res) |
| MT-02 | Krita/Photoshop vẽ pen pressure | nét thanh-đậm đúng lực |
| MT-03 | Xoay tablet 4 hướng | orientation đổi đúng, touch không lệch |
| MT-04 | PC sleep → wake | monitor ảo hồi phục, stream lại |
| MT-05 | Đổi DPI scale Windows 100→250% | touch vẫn chính xác |
| MT-06 | Mở Android Studio (chiếm adb) giữa phiên | app cảnh báo + hướng dẫn, recovery sau khi đóng |
| MT-07 | Battery saver MagicOS bật | cảnh báo whitelist hiện ra |
| MT-08 | 2 màn: cửa sổ video YouTube tablet + làm việc màn chính | không giật màn chính |
| MT-09 | Floating button đổi FPS/quality giữa phiên | apply đúng loại (live/re-mode) |
| MT-10 | Unplug driver (uninstall) → cài lại | không rác display config |

### 9.6 CI gate

- Mỗi commit: U-* + I-01/02/06 (fake-device) — bắt buộc xanh.
- Nightly (khi tablet cắm máy dev): D-* + E-01.
- Release: toàn bộ E-* + MT-* checklist ký tay.

---

## 10. RICE — quyết định còn mở

| Quyết định | Options | RICE verdict |
|-----------|---------|--------------|
| UI framework PC | WPF (R9·I2·C0.9·E2=8.1) vs WinUI3 (R9·I2·C0.6·E3=3.6) vs tray-only C++ (R9·I1·C0.9·E1.5=5.4) | ✅ **WPF .NET 8** — chín, interop C++ dễ |
| Settings sync | PC-authoritative (8.9) vs 2-way merge (2.1) | ✅ **PC-authoritative** (§4.2) |
| Codegen protocol | script Python sinh C#/Kotlin (7.5) vs viết tay 2 bản (4.0) vs protobuf (5.5 — thêm dependency + varint overhead nhỏ) | ✅ **script codegen** từ 1 schema YAML |
| AV1 (RTX 5060 + SD 8s Gen 3 đều decode được) | thêm ngay (3.2) vs defer sau v1.0 (8.0) | ✅ **defer** — HEVC đã dư headroom |

---

## 11. Risks cập nhật (delta so với v1/v1.1)

| # | Rủi ro | Δ | Mitigation |
|---|--------|---|-----------|
| R2 | IddCx IPC (critical path M2) | giữ nguyên CAO | buffer 1 tuần + fallback DDA-on-virtual |
| R8 | ~~decoder không đạt 4K120~~ | **HẠ → THẤP**: panel 3K + số đo cliffs_v1.xml dư | vẫn giữ gate D-01 |
| R9 | NCM bị chặn | giữ TRUNG | fallback T1; số M0.2 quyết định có cần NCM không (3K144 chỉ cần 10.4MB/s — khả năng T1 đủ) |
| **R10 (mới)** | **MagicOS kill app / hạn chế background** | **CAO** | foreground service + wizard whitelist + test D-04, MT-07 |
| **R11 (mới)** | USB port thật của ROD2 có thể chỉ USB 2.0 (chưa xác nhận được, probe bị chặn) | TRUNG | T0.5/M0.2 đo thật; USB2 (~35MB/s lý thuyết, ~25 hiệu dụng) vẫn đủ P-Ultra 10.4MB/s |
| R12 (mới) | Android 16 đổi hành vi foreground service type `connectedDevice` | THẤP | target SDK 36 ngay từ đầu, test trên máy thật |

---

## 12. UC5 Viewpoints + Maturity Ladder

| Perspective | Key Concern | Verdict |
|-------------|-------------|---------|
| Engineering | Codegen protocol 1-nguồn tránh drift 2 đầu; IddCx vẫn là critical path | ✅ M2 sớm + fake-device test |
| Product | Settings model PC-authoritative đơn giản hơn Spacedesk → ít confusion; wizard che ADB khỏi user thường | ✅ |
| QA/SRE | Test pyramid đủ 5 tầng (U/I/D/E/MT) + latency harness tự động → không phụ thuộc "cảm giác mượt" | ✅ harness là điều kiện tiên quyết M1 |
| Security | Token handshake (R7 v1) + localhost bind + không TLS là chấp nhận được cho USB local | ✅ |
| Business (user cá nhân) | 10–12 tuần công cho 1 sản phẩm tự chủ, học sâu 4 domain (driver/codec/protocol/Android) | ✅ nếu mục tiêu là năng lực + tùy biến |

**Tổng hợp**: Mọi góc nhìn hội tụ: dữ liệu probe thật đã khử gần hết uncertainty của v1.1 (panel 3K, decoder dư, encoder dư) — rủi ro kỹ thuật còn lại tập trung ở M2 (IddCx) và 2 rủi ro môi trường (MagicOS killer, USB speed thật). Kế hoạch M0 đặt benchmark gate TRƯỚC khi viết code lớn là đúng thứ tự đầu tư.

**Maturity**: Personal (test-signing, 1 combo máy — chính là scope hiện tại) → Public (EV cert, ma trận thiết bị, installer) → Commercial (multi-tablet, licensing). Kế hoạch này dừng ở mốc Personal-hoàn-chỉnh; các bảng v1 §13 vẫn áp dụng khi scale.

---

## 13. Kết luận + Synthesis Diagram

**Kết luận**: Thiết bị thật (HONOR 3K144 + RTX 5060) làm bài toán DỄ hơn kế hoạch v1.1: profile chốt **3000×1920@120 HEVC ~69Mbps (default)** / 144Hz Ultra, latency ước tính 22–30ms. Settings module theo mô hình PC-authoritative với 2 loại apply (live vs re-mode) — rút kinh nghiệm trực tiếp từ điểm yếu của Spacedesk. Lộ trình M0–M5 (~10–12 tuần) gắn test vào TỪNG task với 5 tầng test + latency harness tự động; gate đầu tiên (M0.2/M0.3 benchmark + codec probe) chạy được NGAY vì thiết bị đã kết nối.

```
┌────────────── SYNTHESIS — TỪ PROBE THẬT ĐẾN v1.0 ──────────────────────────────┐
│                                                                                │
│ [ADB probe: ROD2-W09 3K144, SD8sGen3, A16] ──┐                                 │
│ [PC probe: RTX 5060 NVENC]───────────────────┼──▶ [Profiles P-Ultra…P-Eco §5]  │
│ [H.264 MaxMBPS math]─────────────────────────┘        │ HEVC≥120Hz             │
│                                                       ▼                        │
│ [Spacedesk settings mổ xẻ] ──bài học──▶ [Settings catalog §4.1]                │
│      quality-slider JPEG ✗                    │ 2 loại apply                   │
│      PC-authoritative ✓                       ▼                                │
│ [Protocol +5 messages §4.3] ◀────────── [CONFIG_UPDATE ⚡ | MODE_CHANGE ⟳]     │
│                                               │                                │
│ [M0 bench gate] → [M1 mirror] → [M2 IddCx★] → [M3 input] → [M4 settings]      │
│        │                                                       → [M5 v1.0]     │
│        └──▶ [Test pyramid: U-07 → I-06 → D-05 → E-07 → MT-10]                  │
│              latency-harness (embedded timestamp) đo P50/P95 TỰ ĐỘNG           │
│ Risks: R2 IddCx★ · R10 MagicOS killer · R11 USB speed thật (gate M0.2)         │
└────────────────────────────────────────────────────────────────────────────────┘
```

---

## 14. Sources & References

### Citation Mandate (L3)

> "The server streams H.264 video of the device screen … without buffering, in order to minimize latency." — **Romain Vimont**, tác giả scrcpy, Genymobile (kế thừa v1)

- **Paper**: Chen, K.-T., et al. (2011). *Measuring the Latency of Cloud Gaming Systems.* ACM Multimedia — nền tảng cho latency-harness §9.4.
- **Spec**: ITU-T H.264 Annex A Table A-1 (MaxMBPS levels) — chứng minh Finding #3.
- **Tech docs**: [spacedesk manual — Screen Resolution](https://manual.spacedesk.net/ScreenResolution.html), [Framerate per second](https://manual.spacedesk.net/Frameratepersecond.html), [Client Display Settings](https://manual.spacedesk.net/ClientDisplaySettings.html).
- **Case study**: Spacedesk — settings model 2 đầu gây confusion (quality viewer ≠ compression console) → thiết kế PC-authoritative của ta.

### Source 2: Live System (ADB — thay cho MCP)

| Query | Data |
|-------|------|
| `adb devices -l` | ROD2-W09, transport USB |
| `getprop` (model/brand/release/sdk) | HONOR, Android 16, SDK 36 |
| `wm size; wm density` | 1920×3000, 400dpi |
| `dumpsys display` | 4 modes 60/90/120/144Hz, HDR caps |
| `getprop ro.soc.model` | SM8635 (SD 8s Gen 3) |
| `grep media_codecs_performance_cliffs_v1.xml` | HEVC 1080p: 260–1200fps đo thật |
| `Win32_VideoController` (PC) | RTX 5060 Laptop, Ryzen 9 8945HX |

### Source 3: External

| # | Source | URL |
|---|--------|-----|
| 1 | spacedesk manual — ScreenResolution | https://manual.spacedesk.net/ScreenResolution.html |
| 2 | spacedesk manual — Frameratepersecond | https://manual.spacedesk.net/Frameratepersecond.html |
| 3 | spacedesk manual — ClientDisplaySettings | https://manual.spacedesk.net/ClientDisplaySettings.html |
| 4 | spacedesk manual — Limitations | https://manual.spacedesk.net/Limitations.html |
| 5 | (kế thừa 20 nguồn của PLAN-v1/v1.1) | xem PLAN-v1 §16 |

---

## Addendum — Kết quả M0 thật (2026-07-03, sau code + Opus review)

M0 đã hoàn thành qua pipeline multi-agent (chi tiết + bằng chứng đầy đủ ở [TASK-v1-tablet-display-tracker.md](TASK-v1-tablet-display-tracker.md)). 3 thay đổi cần biết trước khi bắt M1:

1. **P-Ultra 144Hz hạ cấp thành experimental/gated, KHÔNG còn là mục tiêu chính thức.** Đo thật `getSupportedPerformancePoints()` trên ROD2-W09: toàn bộ decoder phần cứng Qualcomm cap ở 120fps dù trần lý thuyết macroblock-rate đủ cho ~185fps — đây là giới hạn khai báo OEM, không phải giới hạn vật lý. **P-High 3K@120Hz HEVC là default chính thức, đã CONFIRMED GO.**
2. **Transport T1 (ADB forward) đủ dùng, không cần chờ NCM.** Đo thật 31–33 MB/s (2 lần độc lập, Sonnet + Opus), dư ~3x nhu cầu P-High/P-Ultra. NCM (T2) vẫn giữ làm hướng tối ưu dài hạn nhưng không còn là điều kiện chặn M1.
3. **M2 (driver C++ IddCx) đang bị chặn một phần bởi môi trường dev**: máy thiếu MSVC v143 toolchain + Windows SDK đầy đủ (xác minh độc lập, không phải lỗi cấu hình project). Cần cài workload "Desktop development with C++" qua Visual Studio Installer trước khi code M2.

Risk register cập nhật: R8/R11 đã RESOLVED; thêm R13 (thiếu toolchain C++), R14 (chưa soak-test transport dài hạn), R15 (NCM throughput chưa đo được do thiếu quyền Admin) — xem đầy đủ trong TASK tracker.

---

## Document Lineage (P4)

| Version | Document | Focus | Status |
|---------|----------|-------|--------|
| v1 | PLAN-v1-tablet-second-display-adb.md | Kiến trúc + 5 phase (giả định 1080p60) | Superseded |
| v1.1 | PLAN-v1 §15 Addendum | Profile 4K120 giả định, NCM transport | Superseded (panel thật = 3K) |
| **v2** | PLAN-v2-tablet-second-display-adb.md | **Live device data + Settings module + M0–M5 + full test plan** | ✅ Current |

## Related Documents (P6)

| Document | Relationship | Status |
|----------|-------------|--------|
| PLAN-v1-tablet-second-display-adb.md | nền kiến trúc (§3–§6 vẫn hiệu lực) | Reference |
| TASK-v1-tablet-display-tracker.md | Master Task File theo dõi M0–M5 | Created cùng lúc |

**Suggested Next**: `SPEC-v1-tablet-display-wire-protocol.md` (schema YAML codegen — làm trong M0.4) · `GUIDE-v1-iddcx-dev-environment.md` (trước M2).

---

## Token Summary
```
Skill prompt:     ~4,500 tokens
Live probes:      5 adb + 1 PowerShell queries
External queries: 0 MCP + 2 web searches
Output:           ~8,500 tokens
TOTAL:            ~30,000 tokens | Context usage: ~35% | Budget: OK
```

**Unresolved questions**:
1. USB port ROD2-W09 thực tế gen nào — probe bị chặn quyền → chốt bằng M0.2 (không blocker, USB2 vẫn đủ P-Ultra).
2. Tablet có bút stylus (HONOR Magic-Pencil) không? → quyết định độ ưu tiên pressure-curve trong M3 (touch vẫn làm đủ).
