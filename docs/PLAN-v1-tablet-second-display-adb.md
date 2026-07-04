# PLAN-v1-tablet-second-display-adb

**Created**: 2026-07-02
**Research Target**: Hệ thống app 2 chiều (Android + Windows) biến tablet Android thành màn hình phụ cho PC qua ADB/USB
**Mode**: Planning
**Level**: L3-Analyze
**Language**: vi
**Context**: None (research từ web sources + kiến trúc scrcpy/SuperDisplay)
**Status**: Completed
**Researcher**: Claude Agent (deep-research skill)

---

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Kiến trúc đã được thị trường validate** | SuperDisplay dùng đúng mô hình: virtual display driver trên Windows + app Android + USB (ADB), latency 20–40ms, hỗ trợ pen 2048 mức lực | windowsnews.ai review SuperDisplay; XDA forums | CAO |
| 2 | **ADB đủ băng thông ×4–20 lần nhu cầu** | 1080p60 H.264 cần ~1.7 MB/s; ADB qua USB đạt 7–40 MB/s tùy thiết bị (USB 2.0 tệ nhất ~7 MB/s vẫn dư) | XDA benchmarks; Fermi §8 | CAO |
| 3 | **IddCx (Indirect Display Driver) là mảnh ghép then chốt** | Driver USER-MODE (không cần kernel), tạo màn hình EXTEND thật mà Windows nhận như monitor vật lý; có sẵn open-source để fork (VirtualDrivers/Virtual-Display-Driver, còn active 02/2026) | Microsoft Learn IddCx docs; GitHub VirtualDrivers | CAO |
| 4 | **Hardware codec 2 đầu là bắt buộc** | Windows: Media Foundation MFT (NVENC/QSV/AMF) encode <5ms, mặc định slice-encoding low-latency. Android: MediaCodec + FEATURE_LowLatency (Android 11+) decode 10–20ms | MS Learn H.264 encoder; source.android.com low-latency-media | CAO |
| 5 | **Chiều ngược (touch/pen → PC) dùng InjectSyntheticPointerInput** | API Win10 1809+ hỗ trợ PT_PEN (có pressure) và PT_TOUCH, inject được ra toàn desktop kể cả màn ảo → tablet thành bảng vẽ Wacom-like | MS Learn winuser.h docs | CAO |
| 6 | **Driver signing là rủi ro #1** | Dev dùng test-signing OK; phân phối public cần EV certificate + attestation signing qua MS Hardware Dev Center | MS driver signing policy | TRUNG BÌNH |
| 7 | **Latency end-to-end khả thi 30–45ms (~2–3 frame)** | Tổng budget: compose 8 + encode 4 + transport 3 + decode 12 + render 12 ms — ngang SuperDisplay | Fermi estimation §8 | TRUNG BÌNH |

---

## Table of Contents

1. [Scan Scope & Stats](#1-scan-scope--stats)
2. [SCQA — Đặt vấn đề](#2-scqa--đặt-vấn-đề)
3. [Tổng quan hệ thống & Kiến trúc](#3-tổng-quan-hệ-thống--kiến-trúc)
4. [Component Map (MECE)](#4-component-map-mece)
5. [Cơ chế chi tiết từng tầng](#5-cơ-chế-chi-tiết-từng-tầng)
6. [Sequence Diagram & Protocol Spec](#6-sequence-diagram--protocol-spec)
7. [INVERSION — Lật ngược giả định](#7-inversion--lật-ngược-giả-định)
8. [Fermi Estimation — Latency & Bandwidth](#8-fermi-estimation--latency--bandwidth)
9. [RICE Scoring — Các quyết định kiến trúc](#9-rice-scoring--các-quyết-định-kiến-trúc)
10. [Kế hoạch triển khai — Phases & Timeline](#10-kế-hoạch-triển-khai--phases--timeline)
11. [Rủi ro & Giảm thiểu](#11-rủi-ro--giảm-thiểu)
12. [UC5 Multi-Viewpoint](#12-uc5-multi-viewpoint)
13. [Maturity Ladder](#13-maturity-ladder)
14. [Kết luận + Synthesis Diagram](#14-kết-luận--synthesis-diagram)
15. [Addendum v1.1 — Profile 4K@120fps](#15-addendum-v11--profile-4k120fps)
16. [Sources & References](#16-sources--references)

---

## 1. Scan Scope & Stats

- **Target**: Hệ thống 2 app — `PC Host (Windows)` ↔ `Display Client (Android tablet)` — kết nối USB qua ADB
- **Mode/Level/Type**: Planning / L3-Analyze / PLAN
- **Sources**: 6 nhánh web research (IddCx, scrcpy, capture API, ADB throughput, MediaCodec, input injection, sản phẩm đối chứng) — 0 file local liên quan (repo chưa có code cho dự án này)
- **"2 chiều" được định nghĩa**: (1) Video PC → tablet; (2) Input touch/pen/keyboard tablet → PC

---

## 2. SCQA — Đặt vấn đề

- **Situation**: Có 1 tablet Android + 1 PC Windows. Muốn tablet thành màn hình phụ thật sự (EXTEND, không chỉ mirror), kèm touch/stylus điều khiển ngược lại PC.
- **Complication**: Giải pháp Wi-Fi (spacedesk) latency thất thường do nghẽn mạng; giải pháp thương mại (SuperDisplay ~10$, Duet subscription) đóng nguồn. Kết nối USB qua ADB cho latency ổn định + sạc pin luôn cho tablet, nhưng đòi hỏi giải quyết 4 bài toán khó: màn hình ảo, capture, streaming low-latency, input injection.
- **Question**: Kiến trúc và lộ trình nào để tự xây hệ thống này hoạt động "ngon" (60fps, <50ms latency, pen pressure)?
- **Answer**: Mô hình 5 tầng — **IddCx virtual display → hardware encode (MF) → TCP-over-ADB → MediaCodec decode → SurfaceView**, chiều ngược **MotionEvent → control socket → InjectSyntheticPointerInput**. Triển khai 5 phase, ~9–11 tuần cho 1 dev. Chi tiết bên dưới.

---

## 3. Tổng quan hệ thống & Kiến trúc

### 3.1 Nguyên lý cốt lõi (First Principles)

Một "màn hình phụ" thực chất chỉ cần 3 điều:
1. **Windows tin rằng có thêm 1 monitor** → OS sẽ compose desktop lên đó (IddCx driver giải quyết — nó báo cáo 1 monitor giả với EDID tùy chỉnh, Windows gửi frame cho driver thay vì cho cáp HDMI).
2. **Frame của monitor đó đến được tablet đủ nhanh** → video pipeline (encode → transport → decode).
3. **(2 chiều) Chạm trên tablet biến thành pointer event trên vùng desktop của monitor đó** → input pipeline ngược.

### 3.2 Architecture Overview

```
┌─────────────────────── PC WINDOWS ────────────────────────┐      ┌────────────── TABLET ANDROID ─────────────┐
│                                                           │      │                                           │
│  ┌─────────────────────────────────────────────┐          │      │  ┌─────────────────────────────────────┐  │
│  │ IddCx Virtual Display Driver (user-mode)    │          │      │  │ Foreground Service (Kotlin)         │  │
│  │  • EDID ảo WxH@Hz (theo panel tablet)       │          │      │  │  ┌───────────────────────────────┐  │  │
│  │  • Nhận D3D swapchain frame từ compositor   │          │      │  │  │ MediaCodec H.264/HEVC decoder │  │  │
│  └──────────────┬──────────────────────────────┘          │      │  │  │ (FEATURE_LowLatency)          │  │  │
│                 │ D3D11 texture (zero-copy)               │      │  │  └──────────────┬────────────────┘  │  │
│  ┌──────────────▼──────────────────────────────┐          │      │  │                 ▼                   │  │
│  │ Encoder: Media Foundation MFT               │          │      │  │       SurfaceView (fullscreen)      │  │
│  │ NVENC / QuickSync / AMF, LowLatencyMode     │          │      │  │                                     │  │
│  └──────────────┬──────────────────────────────┘          │      │  │  MotionEvent capture (touch/pen)    │  │
│                 ▼                                         │      │  └───────┬──────────────▲──────────────┘  │
│  ┌─────────────────────────────────────────────┐  USB     │      │          │              │                 │
│  │ Host App (tray/UI)                          │  cable   │      │          │              │                 │
│  │  • adb forward tcp:29500,29501              │◀═════════╪══════╪═▶ video socket :29500 ──┘                 │
│  │  • video socket ──▶  control socket ◀──     │ (ADB     │      │    control socket :29501                  │
│  │  • InjectSyntheticPointerInput (PEN/TOUCH)  │  tunnel) │      │                                           │
│  └─────────────────────────────────────────────┘          │      └───────────────────────────────────────────┘
└───────────────────────────────────────────────────────────┘
   Chiều 1 (video):  IddCx → Encode → ADB → Decode → SurfaceView
   Chiều 2 (input):  MotionEvent → ADB → Inject vào vùng desktop của màn ảo
```

### 3.3 So sánh nhanh với giải pháp hiện có

| Tiêu chí | **App tự xây (plan này)** | SuperDisplay | spacedesk | Duet Display | scrcpy (tham chiếu) |
|----------|--------------------------|--------------|-----------|--------------|---------------------|
| Transport | USB/ADB | USB/ADB + WiFi | WiFi/LAN/USB | WiFi/USB | USB/ADB (chiều ngược lại) |
| Extend display thật | ✅ IddCx | ✅ | ✅ | ✅ | ❌ (mirror Android→PC) |
| Latency | mục tiêu 30–45ms | 20–40ms | 50–150ms (WiFi) | ~50ms | 35–70ms |
| Pen pressure | ✅ InjectSyntheticPointerInput | ✅ 2048 mức | ❌/hạn chế | ✅ | ❌ |
| Chi phí / nguồn | tự chủ 100% | ~$10 đóng nguồn | free đóng nguồn | subscription | OSS (Apache-2.0) |

---

## 4. Component Map (MECE)

```
HỆ THỐNG (2 executable + 1 driver — không chồng lấn, phủ đủ)
│
├── A. WINDOWS SIDE
│   ├── A1. Virtual Display Driver (C++, IddCx/UMDF)
│   │   ├── EDID ảo + mode list (khớp resolution/DPI/Hz của tablet)
│   │   ├── Monitor arrival/departure (plug/unplug động khi tablet connect)
│   │   └── SwapChain processing → xuất D3D11 texture cho encoder
│   ├── A2. Streaming Engine (C++/C# native interop)
│   │   ├── Encoder MFT (NVENC→QSV→AMF→software fallback)
│   │   ├── Frame pacing + adaptive bitrate
│   │   └── Video socket writer (length-prefixed NAL units)
│   ├── A3. Input Injector
│   │   ├── Control socket reader
│   │   ├── Coordinate mapper (normalized → desktop rect của màn ảo)
│   │   └── InjectSyntheticPointerInput (PT_PEN/PT_TOUCH) + SendInput (keyboard)
│   └── A4. Host App / UX
│       ├── ADB manager (bundled adb.exe: detect device, forward ports, install APK)
│       ├── Settings (resolution, fps, bitrate, codec, vị trí monitor)
│       └── Tray UI + auto-reconnect state machine
│
└── B. ANDROID SIDE (Kotlin, minSdk 26, target 34+)
    ├── B1. Connection Service (Foreground Service + wakelock)
    │   ├── ServerSocket :29500/:29501 (localhost — ADB forward trỏ vào)
    │   └── Handshake + heartbeat + reconnect
    ├── B2. Video Renderer
    │   ├── MediaCodec decoder (H.264 baseline → HEVC nâng cấp)
    │   ├── FEATURE_LowLatency + vendor low-latency keys fallback
    │   └── SurfaceView fullscreen immersive (không dùng TextureView — tránh 1 lần copy)
    └── B3. Input Capturer
        ├── onTouchEvent/onGenericMotionEvent (multi-touch, stylus + pressure, hover)
        ├── Serializer (normalized 0..65535 coords)
        └── Control socket writer
```

**Kiểm tra MECE**: A1–A4 chia theo *chức năng OS-level* (display / video-out / input-in / orchestration), B1–B3 chia theo *vòng đời dữ liệu* (kết nối / hiển thị / thu input) — không thành phần nào chồng lấn, gộp lại phủ toàn bộ luồng 2 chiều.

---

## 5. Cơ chế chi tiết từng tầng

### 5.1 Tầng màn hình ảo — IddCx (quyết định quan trọng nhất)

- IddCx = **Indirect Display Driver Class eXtension**, mô hình driver **user-mode** của Microsoft dành riêng cho monitor "không cắm qua GPU" (dock USB, wireless display). Driver khai báo adapter + monitor với EDID tùy ý; DWM compose desktop rồi **đưa thẳng frame vào swapchain của driver** — nghĩa là **không cần Desktop Duplication API riêng**: driver chính là điểm capture, zero-copy D3D11 texture đi thẳng vào encoder.
- **Lối đi tắt**: fork [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (active, commit 02/2026, hỗ trợ Win10/11, custom EDID, HDR) hoặc học từ [Microsoft IddSampleDriver](https://github.com/roshkins/IddSampleDriver). Việc cần thêm: pipe frame từ swapchain ra encoder process (shared texture / IPC handle) + API plug/unplug monitor theo trạng thái kết nối tablet.
- **Capture fallback (Phase 0, trước khi có driver)**: dùng **Desktop Duplication API** mirror màn hình chính — cho phép test toàn pipeline không đụng driver. Lưu ý DDA yêu cầu chạy cùng GPU với display; Windows.Graphics.Capture là lựa chọn thay thế cross-GPU.

### 5.2 Tầng encode — Media Foundation MFT

- Enumerate hardware MFT theo thứ tự **NVENC → QuickSync → AMF**, fallback software x264-style MFT. H.264 Encoder MFT của Windows **mặc định slice encoding để minimize latency**; giữ `CODECAPI_AVLowLatencyMode = TRUE`, GOP dài + intra-refresh (tránh I-frame spike làm giật), rate control CBR/VBR peak-constrained.
- Cấu hình khởi điểm: **H.264 High, 1080p60 (hoặc native tablet), 12–20 Mbps, no B-frames**. Hardware encode <5ms/frame là chuẩn ngành cho real-time.

### 5.3 Tầng transport — TCP over ADB (mô hình scrcpy đảo chiều)

- scrcpy đã chứng minh mô hình này 8 năm production: socket qua ADB tunnel, stream H.264 **"without buffering in order to minimize latency"**. Khác biệt của ta: video chạy chiều PC→tablet.
- Cơ chế: Android app mở `ServerSocket` trên localhost; PC chạy `adb forward tcp:29500 tcp:29500` (+ `29501` control) rồi connect như socket thường — ADB làm tunnel qua USB. Dùng 2 socket tách biệt để control event không bao giờ xếp hàng sau video frame (head-of-line blocking).
- PC side dùng bundled `adb.exe` hoặc thư viện `AdvancedSharpAdbClient` (C#) để tự nói chuyện với adb server — tự động hoá detect device, cài APK, forward port, phát hiện rút cáp.

### 5.4 Tầng decode + render — Android

- `MediaCodec` hardware decoder, cấu hình `KEY_LOW_LATENCY=1` khi `FEATURE_LowLatency` khả dụng (Android 11+); fallback vendor keys (`vendor.qti-ext-dec-low-latency`, v.v.). Decode trực tiếp ra **Surface của SurfaceView** — không qua TextureView/GLSurfaceView để tránh copy + composition pass thừa. Thực nghiệm cộng đồng: 10–20ms decode-to-display khi cấu hình đúng.
- Render loop: `releaseOutputBuffer(index, true)` ngay khi có frame — không sync theo PTS (màn phụ ưu tiên latency hơn smooth playback).

### 5.5 Tầng input ngược — tablet → PC

- Android capture `MotionEvent`: multi-touch (pointerId, action, x, y), stylus (`TOOL_TYPE_STYLUS`, `getPressure()`, hover, side-button), gửi qua control socket dạng normalized (0..65535).
- Windows nhận và map vào **desktop rect của màn hình ảo** (query qua `EnumDisplayMonitors` / `QueryDisplayConfig` theo device path của driver mình), rồi:
  - Touch → `InjectSyntheticPointerInput(PT_TOUCH)` (multi-touch tối đa theo khai báo)
  - Stylus → `InjectSyntheticPointerInput(PT_PEN)` với pressure — Windows Ink/Photoshop nhận như bút thật (Win10 1809+)
  - Keyboard (bàn phím ảo/vật lý cắm tablet) → `SendInput`

---

## 6. Sequence Diagram & Protocol Spec

### 6.1 Connection + Streaming Sequence

```
 PC HostApp          adb server            Tablet App (Service)
     │  device attached?  │                        │
     │──devices/track────▶│                        │
     │  (serial found)    │                        │
     │──forward 29500/1──▶│                        │
     │────────connect video socket────────────────▶│ (listening)
     │────────connect control socket──────────────▶│
     │◀───CAPS{w,h,dpi,hz,codecs,maxTouch}─────────│
     │──CONFIG{codec,res,fps,bitrate}─────────────▶│
     │ [IddCx: plug-in virtual monitor WxH@Hz]     │
     │                                             │
     │══ loop mỗi frame ═══════════════════════════│
     │  swapchain acquire → MFT encode             │
     │──[len|pts|flags|NALs]──────────────────────▶│ decode → SurfaceView
     │◀──INPUT{touch/pen/key events}───────────────│ (bất đồng bộ, socket riêng)
     │  map coords → InjectSyntheticPointerInput   │
     │══ heartbeat 1s/lần trên control socket ═════│
     │                                             │
     │  (rút cáp / mất heartbeat 3s)               │
     │ [IddCx: unplug monitor] → chờ reconnect     │
```

### 6.2 Wire Protocol (draft v0)

| Socket | Frame format | Message types |
|--------|--------------|---------------|
| Video :29500 | `[u32 len][u8 flags][u64 ptsUs][payload]` | `CONFIG_ACK`, `VIDEO_FRAME` (flags: keyframe, config-NAL) |
| Control :29501 | `[u16 len][u8 type][payload]` | `CAPS`, `CONFIG`, `TOUCH` (pointerId,action,x,y,pressure,toolType), `KEY`, `HEARTBEAT`, `ROTATE`, `CLIPBOARD` (v2) |

Nguyên tắc: little-endian, không TLS (localhost-over-USB, ADB đã authenticate bằng RSA key), version byte trong handshake để nâng cấp protocol không gãy.

---

## 7. INVERSION — Lật ngược giả định

**Giả định sai #1: "ADB là kênh debug, quá chậm/không đủ tin cậy để stream video 60fps."**
→ Lật ngược: scrcpy stream 1080p60 H.264 qua ADB tunnel cho hàng triệu user từ 2018 (Genymobile, Apache-2.0); benchmark XDA đo ADB-USB đạt 7–40 MB/s trong khi 1080p60 chỉ cần ~1.7 MB/s.
→ Chân lý: ADB tunnel = TCP socket forward qua USB, băng thông dư 4–20×; điểm yếu thật sự là *jitter khi adb server bị process khác chiếm* (Android Studio), không phải throughput.

**Giả định sai #2: "Muốn Windows nhận thêm monitor phải viết kernel driver — quá nguy hiểm/khó."**
→ Lật ngược: Microsoft Learn ghi rõ IddCx là mô hình **user-mode**, "doesn't support kernel-mode components", driver crash không BSOD; cộng đồng đã có driver open-source hoàn chỉnh (VirtualDrivers, commit gần nhất 02/2026) mà Sunshine/OBS/VR users dùng hàng ngày.
→ Chân lý: rào cản không phải kỹ thuật kernel mà là **chữ ký driver khi phân phối** (EV cert + attestation) — rủi ro quản trị, không phải rủi ro code.

**Giả định sai #3: "Wi-Fi tiện hơn nên làm Wi-Fi trước, USB tính sau."**
→ Lật ngược: chính các review SuperDisplay vs spacedesk chỉ ra USB thắng tuyệt đối cho use-case màn phụ: latency 20–40ms ổn định (vs 50–150ms + jitter theo sóng), không nghẽn theo mạng gia đình, **kiêm luôn sạc pin** — tablet làm màn phụ 8h/ngày bắt buộc phải cắm dây.
→ Chân lý: USB-first là quyết định sản phẩm đúng, Wi-Fi chỉ là tính năng phụ (P2, sau này).

**Giả định sai #4: "Cần protocol phức tạp kiểu RDP/VNC (damage regions, caching, compression tiles)."**
→ Lật ngược: scrcpy chứng minh raw H.264 elementary stream + control protocol vài chục byte/event là đủ — hardware encoder hiện đại đã làm việc "chỉ gửi phần thay đổi" tốt hơn mọi damage-region logic tự viết (P-frame của màn hình tĩnh gần như 0 byte).
→ Chân lý: độ phức tạp nên dồn vào **frame pacing + adaptive bitrate**, không phải vào protocol.

**Giả định sai #5: "Latency chủ yếu nằm ở dây cáp/transport."**
→ Lật ngược: Fermi §8 cho thấy transport chỉ chiếm ~3ms/40ms (7%); 75% nằm ở compose-wait + decode + display pipeline. Kinh nghiệm cộng đồng NVIDIA forums: NVENC MFT latency *tăng khi frame rate bị giới hạn* — tức pacing sai mới là thủ phạm phổ biến.
→ Chân lý: tối ưu latency = tối ưu **pipeline scheduling** (đừng đợi vsync 2 lần, đừng buffer), không phải mua cáp xịn.

---

## 8. Fermi Estimation — Latency & Bandwidth

### 8.1 Bandwidth (chiều video)

```
Câu hỏi: 1080p60 H.264 cần bao nhiêu MB/s? ADB chịu nổi không?
─ Bits-per-pixel screencontent H.264 hardware: ~0.10–0.15 bpp
─ 1920 × 1080 × 60fps × 0.12bpp ≈ 15 Mbps ≈ 1.9 MB/s
─ ADB-USB thực đo: 7 MB/s (USB2/thiết bị yếu) … 40 MB/s (USB3)
→ Headroom: 3.7× (tệ nhất) đến 21× (tốt) → CÓ, kể cả 1600p90 (~4 MB/s) vẫn ổn
→ Sanity check: SuperDisplay chạy 120Hz qua đúng kênh này ✓
```

### 8.2 Latency budget end-to-end (mục tiêu ≤ 45ms)

```
 Stage                                   Ước lượng   Ghi chú
 ─────────────────────────────────────── ─────────  ─────────────────────────────
 DWM compose → IddCx swapchain            ~8 ms     avg ½ frame @60Hz
 Encode (NVENC/QSV, low-latency)          2–5 ms    chuẩn ngành real-time
 Packetize + ADB tunnel + USB             2–4 ms    frame 30KB @ ≥7MB/s ≈ 4ms worst
 MediaCodec decode (FEATURE_LowLatency)   8–15 ms   10–20ms theo thực nghiệm
 SurfaceView present (vsync wait)         ~8 ms     avg ½ frame
 Display panel latency                    ~5 ms     tablet hiện đại
 ─────────────────────────────────────── ─────────
 TỔNG                                    33–45 ms   ≈ 2–3 frame @60Hz
 Đối chứng: SuperDisplay công bố 20–40ms → budget hợp lý ✓
 Chiều input (touch→PC): ~5–10ms (event 20 byte, không encode) → không đáng kể
```

### 8.3 Effort (người-tuần, 1 dev có kinh nghiệm C++/Kotlin trung bình)

```
P0 PoC mirror (DDA + MFT + socket + decode)   : 2 tuần
P1 IddCx driver integration (extend thật)      : 2–3 tuần  ← rủi ro cao nhất
P2 Input 2 chiều (touch/pen inject)            : 1–2 tuần
P3 UX (adb manager, reconnect, settings)       : 2 tuần
P4 Hardening (HEVC, ABR, pacing, multi-res)    : 2 tuần
──────────────────────────────────────────────
TỔNG                                           : 9–11 tuần (±30%)
```

---

## 9. RICE Scoring — Các quyết định kiến trúc

### 9.1 Transport (Reach=thiết bị hỗ trợ /10 · Impact /3 · Confidence /1 · Effort tuần)

| Option | R | I | C | E | RICE | Verdict |
|--------|---|---|---|---|------|---------|
| **TCP over ADB forward (USB)** | 9 | 3 | 0.9 | 1 | **24.3** | ✅ **CHỌN** — đúng yêu cầu, scrcpy-proven |
| AOA v2 (USB Accessory, không cần bật USB debugging) | 8 | 3 | 0.6 | 4 | 3.6 | Alternative v2 — UX tốt hơn (không cần Developer Options) nhưng phải tự viết bulk-transfer framing |
| TCP over Wi-Fi | 10 | 1.5 | 0.9 | 1.5 | 9.0 | Deferred P2 — thêm sau như tính năng phụ |
| WebRTC | 10 | 2 | 0.7 | 6 | 2.3 | ❌ Rejected — FEC/jitter-buffer thừa cho USB, cộng phức tạp |

### 9.2 Virtual display trên Windows

| Option | RICE-tổng | Verdict |
|--------|-----------|---------|
| **Fork VirtualDrivers/Virtual-Display-Driver + thêm frame-export IPC** | cao | ✅ **CHỌN** — codebase sống, cộng đồng lớn, đã xử lý EDID/HDR/multi-res |
| Viết mới từ IddSampleDriver | trung | Alternative — sạch hơn nhưng +2 tuần, tự gánh mọi edge-case |
| usbmmidd / driver đóng nguồn | thấp | ❌ Rejected — không tự chủ, license không rõ |
| Mirror-only (không driver) | thấp | ✅ nhưng chỉ là **Phase 0 stepping-stone** |

### 9.3 Codec

| Option | Verdict |
|--------|---------|
| **H.264/AVC** | ✅ **CHỌN launch** — decoder phổ quát 100% tablet, encoder MFT mọi GPU từ ~2014 |
| HEVC | P4 — giảm ~40% bitrate, nhưng phải probe cả 2 đầu; bật khi cả 2 hỗ trợ |
| AV1 | ❌ Defer — encode HW chỉ có trên GPU 2022+, decode HW hiếm trên tablet |

---

## 10. Kế hoạch triển khai — Phases & Timeline

### 10.1 WBS theo phase (mỗi phase có Definition of Done)

**P0 — PoC pipeline không driver (2 tuần)** — *mục tiêu: chứng minh transport + codec trước khi đầu tư driver*
- [ ] T0.1 Android app khung: Foreground Service + ServerSocket + SurfaceView fullscreen
- [ ] T0.2 PC console app: DDA capture màn hình chính → MFT H.264 encode
- [ ] T0.3 ADB manager tối thiểu: detect device, `adb forward`, connect 2 sockets
- [ ] T0.4 Handshake CAPS/CONFIG + stream + decode + render
- [ ] **DoD**: mirror màn hình chính lên tablet 1080p60, đo latency bằng camera quay đồng hồ ms trên 2 màn (mục tiêu <60ms ở bước này)

**P1 — Extend thật bằng IddCx (2–3 tuần)** — *rủi ro cao nhất, làm sớm*
- [ ] T1.1 Fork Virtual-Display-Driver, build + test-sign, cài trên máy dev (`bcdedit /set testsigning on`)
- [ ] T1.2 Thêm cơ chế export frame: shared D3D11 texture handle → encoder process (named mutex + keyed mutex)
- [ ] T1.3 API plug/unplug monitor động qua IOCTL/COM từ Host App (connect tablet = cắm monitor)
- [ ] T1.4 EDID generator theo CAPS tablet (resolution/DPI/Hz thật của panel)
- [ ] **DoD**: Windows Display Settings hiện monitor thứ 2 đúng native resolution tablet; kéo cửa sổ sang được; rút cáp → monitor biến mất sạch sẽ

**P2 — Input 2 chiều (1–2 tuần)**
- [ ] T2.1 Android: capture MotionEvent multi-touch + stylus (pressure/hover/buttons) → serialize
- [ ] T2.2 PC: coordinate mapper theo desktop rect thực tế của màn ảo (kể cả khi user đổi vị trí monitor, DPI scale)
- [ ] T2.3 InjectSyntheticPointerInput PT_TOUCH (10 điểm) + PT_PEN (pressure) + SendInput keyboard
- [ ] **DoD**: vẽ trong Photoshop/Krita trên tablet có lực nén; pinch-zoom hoạt động; click chính xác ở mọi DPI scale

**P3 — UX & độ bền kết nối (2 tuần)**
- [ ] T3.1 Host App UI (tray): trạng thái, settings resolution/fps/bitrate, chọn vị trí monitor
- [ ] T3.2 Auto-flow: cắm cáp → detect → (chưa có app thì `adb install` APK bundled) → auto-launch activity → connect
- [ ] T3.3 Reconnect state machine (rút cáp, app bị kill, adb server bị Android Studio chiếm → `adb kill-server` policy rõ ràng)
- [ ] T3.4 Xử lý rotate tablet, multi-DPI, keep-screen-on, chặn battery-optimization kill service
- [ ] **DoD**: người không biết ADB dùng được: cài PC app → cắm cáp → bật USB debugging theo hướng dẫn có hình → chạy

**P4 — Performance hardening (2 tuần)**
- [ ] T4.1 Adaptive bitrate (queue depth video socket làm tín hiệu congestion)
- [ ] T4.2 Frame pacing: skip-to-latest khi decoder chậm, intra-refresh thay I-frame
- [ ] T4.3 HEVC negotiation khi 2 đầu hỗ trợ; tùy chọn 90/120Hz
- [ ] T4.4 Đo đạc: overlay latency HUD, log P50/P95/P99 từng stage
- [ ] **DoD**: P95 latency ≤ 45ms @1080p60 trên máy tham chiếu; CPU PC < 5%, GPU encode < 10%

### 10.2 Timeline (Gantt)

```
Tuần        1  2  3  4  5  6  7  8  9  10 11
P0 PoC      ██ ██
P1 IddCx          ██ ██ ██
P2 Input                   ██ ██
P3 UX                            ██ ██
P4 Perf                                ██ ██
Buffer                                       ░░ (rủi ro P1 tràn)
Milestone   ▲M0: video chạy  ▲M1: extend  ▲M2: pen  ▲M3: dùng hằng ngày  ▲M4: v1.0
```

### 10.3 Tech stack chốt

| Layer | Công nghệ | Lý do |
|-------|-----------|-------|
| Driver | C++ / IddCx (fork Virtual-Display-Driver) | bắt buộc native; codebase có sẵn |
| PC engine (encode/socket/inject) | C++ (Media Foundation, Winsock, winuser) | latency-critical, tránh GC pause |
| PC Host App UI | C# .NET 8 WPF (interop C++ engine) hoặc full C++ | tốc độ dev UI; `AdvancedSharpAdbClient` cho ADB |
| Android | Kotlin, MediaCodec, SurfaceView, Foreground Service | chuẩn platform, không cần NDK ở v1 |
| Protocol | Custom binary (§6.2) | 2 socket, đơn giản, versioned |

---

## 11. Rủi ro & Giảm thiểu

| # | Rủi ro | Xác suất | Tác động | Giảm thiểu |
|---|--------|----------|----------|------------|
| R1 | **Driver signing khi phân phối** (test-signing chỉ ổn cho máy mình) | Cao (nếu public) | Cao | Dev: test-signing. Phát hành: EV cert (~$300–500/năm) + attestation qua MS Hardware Dev Center — hoặc hướng dẫn user tự bật test-signing (giảm audience) |
| R2 | IddCx frame-export IPC phức tạp hơn dự kiến | Trung | Cao (P1 tràn) | Đặt P1 sớm + 1 tuần buffer; fallback tạm: DDA capture chính màn ảo (chậm hơn ~5ms nhưng chạy) |
| R3 | adb server xung đột (Android Studio, scrcpy khác) | Cao | Trung | Dùng adb bundled riêng version, detect server đang chạy, docs rõ; xa hơn: nói ADB protocol trực tiếp không qua adb.exe |
| R4 | Thiết bị chỉ USB 2.0 / cáp rởm | Trung | Thấp | 7MB/s vẫn đủ 1080p60; ABR tự hạ bitrate; hiển thị cảnh báo tốc độ link |
| R5 | OEM kill background service / Doze | Trung | Trung | Foreground service + notification + hướng dẫn whitelist battery |
| R6 | MediaCodec low-latency không có trên tablet cũ (<Android 11) | Trung | Thấp | Vendor keys fallback; chấp nhận +10–20ms trên máy cũ |
| R7 | Bảo mật: cổng localhost trên tablet bị app khác connect | Thấp | Trung | Handshake token do PC sinh, truyền qua `adb shell am start --es token` khi launch app |

---

## 12. UC5 Multi-Viewpoint

| Perspective | Key Concern | Verdict |
|-------------|-------------|---------|
| Engineering | IddCx IPC + latency pipeline là 2 điểm khó thật; còn lại là lắp ráp API đã chín | ✅ Khả thi, P1 là critical path |
| Product/PM | UX "bật USB debugging" là rào cản người thường; SuperDisplay đã chứng minh thị trường chấp nhận | ✅ với onboarding có hình từng bước |
| SRE/Support | Ma trận thiết bị Android × GPU PC × cáp = nguồn ticket chính; cần latency HUD + log stage để chẩn đoán từ xa | ⚠️ Đầu tư T4.4 sớm |
| Security | ADB RSA-authenticated, USB local — bề mặt tấn công thấp; lỗ hổng chính là localhost port (R7) + quyền inject input của Host App | ✅ với token handshake |
| Business | Tự xây: ~10 tuần công vs mua SuperDisplay $10 — chỉ hợp lý nếu mục tiêu là học + tự chủ + có thể thương mại hoá/tuỳ biến (nhiều tablet, tính năng riêng) | ✅ như dự án đầu tư năng lực |
| Academic | Bài toán = cloud-gaming-latency thu nhỏ; phương pháp đo của Chen et al. (ACM MM 2011) áp dụng trực tiếp cho HUD đo latency | ✅ có nền lý thuyết |

**Tổng hợp**: Cả 6 góc nhìn đồng thuận dự án khả thi với điều kiện: (1) xử lý IddCx sớm vì là critical path kỹ thuật, (2) chấp nhận trade-off "bật USB debugging" như SuperDisplay, (3) driver signing chỉ trở thành vấn đề khi phân phối public — với mục đích cá nhân, test-signing đủ dùng và toàn bộ kế hoạch không có blocker nào.

---

## 13. Maturity Ladder

| Level | Quy mô | Thách thức chính | Failure mode thường gặp |
|-------|--------|------------------|--------------------------|
| **Startup / Personal** (bạn, 1 máy) | 1 dev | Test-signing driver; 1 cấu hình PC+tablet duy nhất; latency tuning cho đúng combo của mình | Hardcode resolution/serial; không xử lý rút cáp → phải restart cả 2 app |
| **Scale-up / Public release** | 2–5 dev, nghìn user | EV cert + attestation signing; ma trận GPU (NVENC/QSV/AMF) × Android OEM; installer + auto-update; crash telemetry | Driver không cài được trên Win11 Secure Boot; OEM kill service; adb conflict ticket tràn |
| **Enterprise / Commercial** | 10+ | Licensing, MDM deploy, ký driver WHQL đầy đủ, SLA support, multi-monitor (2–3 tablet/PC), Wi-Fi + AOA transport | Cạnh tranh trực tiếp SuperDisplay/Duet về giá; chi phí support vượt doanh thu nếu không giới hạn support matrix |

Ví dụ thực tế mỗi mức: cá nhân — các fork IddSampleDriver tự dùng đầy GitHub; scale-up — Virtual-Display-Driver có SignPath Foundation ký hộ mã nguồn mở; enterprise — spacedesk/SuperDisplay là 2 công ty sống bằng đúng sản phẩm này.

---

## 14. Kết luận + Synthesis Diagram

**Kết luận (Pyramid)**: Xây được, kiến trúc rõ, không có ẩn số công nghệ — chỉ có 1 critical path (IddCx frame-export) và 1 rủi ro quản trị (driver signing khi public). Đi theo 5 phase, validate transport trước (P0), đánh driver sớm (P1), tổng ~9–11 tuần.

```
┌────────────────── MỐI LIÊN KẾT CÁC KHÁI NIỆM — TABLET LÀM MÀN PHỤ QUA ADB ──────────────────┐
│                                                                                             │
│  [IddCx Virtual Display] ──frame D3D11──▶ [MFT HW Encode] ──NALs──▶ [TCP over ADB tunnel]   │
│      ▲ plug/unplug                            ▲ NVENC/QSV              │ 7–40MB/s ≫ 1.9MB/s │
│      │ theo kết nối                           │ <5ms                   ▼                    │
│  [Host App + ADB Manager] ◀──INPUT events── [Control socket] ◀── [MotionEvent capture]      │
│      │ InjectSyntheticPointerInput                                     ▲                    │
│      ▼ PT_PEN + pressure                                               │                    │
│  (2 CHIỀU: video xuống ── input lên)          [MediaCodec LowLatency]──┴─▶ [SurfaceView]    │
│                                                    8–15ms decode            tablet panel    │
│                                                                                             │
│  Latency budget 33–45ms  ═  compose 8 + encode 4 + transport 3 + decode 12 + present 13     │
│  Phases: P0 PoC-mirror → P1 IddCx-extend → P2 Input → P3 UX → P4 Perf                       │
│  Rủi ro chi phối: R1 driver-signing (public) · R2 IddCx IPC (critical path)                 │
│  Maturity: Personal(test-sign) → Public(EV cert) → Commercial(WHQL, multi-tablet)           │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 15. Addendum v1.1 — Profile 4K@120fps

**Bối cảnh mới**: user yêu cầu stream tới 4K@120fps; tablet hỗ trợ USB 3.0, laptop USB4 → "cần cách chạy hết công suất USB".

### 15.1 Fermi lại — 4K120 đổi bài toán ở đâu?

```
Uncompressed 4K120 RGBA : 3840×2160 × 4B × 120 ≈ 3.98 GB/s
USB 3.0 effective       : ~450–500 MB/s (5 Gbps)          → thiếu ~8 LẦN
→ Nén là BẮT BUỘC, không có phương án raw.
→ USB4 phía laptop KHÔNG giúp gì: link luôn negotiate theo đầu yếu hơn = USB 3.0 (5 Gbps).

HEVC 4K120 screen content (0.08–0.12 bpp) : ~100–200 Mbps ≈ 12–25 MB/s
ADB thực đo                                : 7–40 MB/s     → chạy được nhưng SÁT NÚT + jitter risk
                                                             (frame cadence 8.3ms, protocol ACK windowing)
USB 3.0 còn trống                          : ~450 MB/s     → nút cổ chai là GIAO THỨC ADB, không phải USB
```

**Kết luận đảo chiều so với v1**: ở 1080p60, ADB dư 4–20×; ở 4K120 high-quality, ADB chỉ còn dư ~1.5–3× → cần lối thoát transport để "hết công suất".

### 15.2 Chiến lược transport 3 tầng

| Tầng | Cơ chế | Throughput kỳ vọng | Verdict |
|------|--------|--------------------|---------|
| **T1 baseline** | ADB forward + platform-tools mới nhất + `ADB_BURST_MODE=1` (bỏ chờ ACK) + checksum-less (Android P+, +40%) | 30–40+ MB/s | ✅ đủ cho native-res@120, giữ làm mặc định |
| **T2 — khuyến nghị cho 4K120** | **USB tethering NCM**: biến cáp USB thành card mạng thật; video stream đi TCP socket TRỰC TIẾP qua NIC ảo, bypass hoàn toàn giao thức adb | trăm Mbps → Gbps-class (tận dụng USB 3.0 thật sự) | ✅ **CHỌN cho profile 4K120** |
| T3 | AOA v2 bulk transfer | ~35 MB/s | ❌ đa số thiết bị re-enumerate ở USB 2.0 (480 Mbps) khi vào accessory mode |

**T2 vận hành thế nào (không phá kiến trúc cũ)**:
- PC Host App vẫn dùng ADB để **orchestrate**: `adb shell svc usb setFunctions ncm` bật tethering tự động (không bắt user mò Settings), detect NIC mới xuất hiện phía Windows, rồi mở video socket qua IP link-local của interface đó.
- **Control channel giữ nguyên trên ADB** (bé, cần reliability + đã authenticated RSA).
- Kiến trúc module A2/B1 tách transport từ đầu → chỉ swap tầng socket, encoder/decoder/protocol không đổi.
- Fallback tự động: NCM fail (ROM lạ, MDM chặn) → rơi về T1 + hạ cấu hình stream.

### 15.3 Codec — điểm chí mạng của 4K120

- **H.264 hardware max Level 5.2 = 4K@60. KHÔNG THỂ decode 4K120 bằng H.264.** → **HEVC Main Level 5.2 (4K@120) hoặc AV1 là BẮT BUỘC** cho profile này — HEVC chuyển từ "nâng cấp P4" thành yêu cầu launch.
- Android phải probe `MediaCodecInfo.VideoCapabilities.getSupportedPerformancePoints()` tìm điểm `3840×2160@120` — **nhiều SoC tablet chỉ đạt 4K60**. Đây là gate go/no-go trước khi hứa 4K120 (thêm vào T0.5).
- PC encoder: NVENC 4K120 low-latency cần RTX 20-series+; QuickSync cần Iris Xe/Arc; AMF cần RDNA2+.

### 15.4 Budget 120fps & khuyến nghị thực dụng

- Frame budget 8.3ms → decode sustained phải <8ms. Nếu decoder theo kịp, latency tổng **tốt hơn** 60fps (vsync-wait giảm nửa còn ~4ms avg, tổng có thể ~25–35ms). Nếu decoder chỉ đạt ~100fps → drop-to-latest hoặc hạ resolution.
- **Thực tế panel**: đa số tablet Android 120Hz có panel ~2800–3000 × 1750–1900 (không phải 4K thật). Stream đúng native resolution @120 = ~55–60% pixel của 4K → chỉ ~60–90 Mbps HEVC → chạy thoải mái ngay cả trên T1. **Chốt spec sau khi biết model tablet.**

### 15.5 Plan delta (v1 → v1.1)

| Mục | Thay đổi |
|-----|----------|
| P0 | Thêm **T0.5**: benchmark throughput thực trên đúng combo thiết bị — (a) raw socket qua adb forward, (b) iperf3 qua NCM tethering; + probe PerformancePoints decoder tablet. Số thực tế quyết định T1/T2 |
| P1 | EDID generator hỗ trợ mode 4K120 + native-res@120 (2 mode list) |
| P4 | HEVC negotiation nâng cấp thành **bắt buộc** cho profile ≥4K60/native120; thêm NCM transport switch |
| Risks | **R8 mới**: decoder tablet không đạt 4K120 PerformancePoint (xác suất TRUNG–CAO) → mitigation: native-res@120 hoặc 4K@60. **R9 mới**: NCM tethering bị ROM/MDM chặn → fallback T1 |

---

## 16. Sources & References

### Citation Mandate (L3)

> "The server streams H.264 video of the device screen … without buffering, in order to minimize latency." — **Romain Vimont**, tác giả scrcpy, Genymobile ([scrcpy develop.md](https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md))

- **Paper**: Chen, K.-T., Chang, Y.-C., et al. (2011). *Measuring the Latency of Cloud Gaming Systems.* ACM Multimedia — phương pháp đo end-to-end latency áp dụng cho HUD đo đạc (T4.4).
- **Tech blog/docs**: [Indirect Display Driver Model Overview — Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview); [Remio — Hardware encoder comparison](https://remio.net/blog/hardware-encoder-comparison) (2026).
- **Case study**: SuperDisplay — sản phẩm thương mại dùng đúng kiến trúc IddCx + ADB/USB, latency công bố 20–40ms, pen 2048 mức lực ([WindowsNews review](https://windowsnews.ai/article/superdisplay-turns-android-tablets-into-usb-monitors-for-windows-setup-guide-and-performance-analysi.405279)).

### Source 3: External (đầy đủ)

| # | Source | URL | Relevance |
|---|--------|-----|-----------|
| 1 | Microsoft Learn — IddCx overview | https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview | Driver model màn hình ảo (user-mode) |
| 2 | VirtualDrivers/Virtual-Display-Driver | https://github.com/VirtualDrivers/Virtual-Display-Driver | Codebase driver để fork (active 02/2026) |
| 3 | roshkins/IddSampleDriver | https://github.com/roshkins/IddSampleDriver | Sample driver tham chiếu của MS |
| 4 | scrcpy develop.md | https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md | Kiến trúc socket-over-ADB, no-buffering |
| 5 | Introducing scrcpy — rom1v blog | https://blog.rom1v.com/2018/03/introducing-scrcpy/ | Nguyên lý low-latency streaming |
| 6 | AOSP — Low-latency decoding in MediaCodec | https://source.android.com/docs/core/media/low-latency-media | FEATURE_LowLatency (Android 11+) |
| 7 | MediaCodec API reference | https://developer.android.com/reference/android/media/MediaCodec | Decoder → Surface |
| 8 | Microsoft Learn — H.264 Video Encoder MFT | https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder | Slice encoding, AVLowLatencyMode |
| 9 | Microsoft Learn — InjectSyntheticPointerInput | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput | Inject PT_PEN/PT_TOUCH |
| 10 | Microsoft Learn — InjectTouchInput | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injecttouchinput | So sánh API inject |
| 11 | OBS Forums — WGC vs DXGI Desktop Duplication | https://obsproject.com/forum/threads/windows-graphics-capture-vs-dxgi-desktop-duplication.149320/ | Capture fallback P0 |
| 12 | XDA — adb USB throughput threads | https://xdaforums.com/t/q-why-is-adb-over-wireless-vs-usb-10-times-slower.1941201/ | Số liệu băng thông ADB |
| 13 | WindowsNews — SuperDisplay analysis | https://windowsnews.ai/article/superdisplay-turns-android-tablets-into-usb-monitors-for-windows-setup-guide-and-performance-analysi.405279 | Case study đối chứng |
| 14 | MyNextTablet — tablet-as-monitor apps | https://mynexttablet.com/use-android-tablet-as-monitor/ | So sánh thị trường |
| 15 | Remio — HW encoder comparison | https://remio.net/blog/hardware-encoder-comparison | NVENC/QSV/AMF latency |
| 16 | NVIDIA forums — NVENC MFT latency | https://forums.developer.nvidia.com/t/nvenc-h-264-encoder-mft-latency-increases-when-framerate-is-limited/63930 | Bẫy frame pacing |
| 17 | Android Developers — adb (Burst Mode) | https://developer.android.com/tools/adb | ADB_BURST_MODE=1 tăng throughput (v1.1 T1) |
| 18 | SDK Platform Tools release notes | https://developer.android.com/tools/releases/platform-tools | Checksum-less +40%, receive windowing (v1.1 T1) |
| 19 | Gentoo wiki — Android USB tethering | https://wiki.gentoo.org/wiki/Android_USB_tethering | NCM là chuẩn tethering hiện đại (v1.1 T2) |
| 20 | XDA — USB tethering benchmarks (iperf) | https://xdaforums.com/t/usb-tethering-benchmarks-latency-and-bandwidth.4092827/ | Số liệu network-over-USB (v1.1 T2) |

### Source 1: Local — không có file liên quan. · Source 2: MCP — không dùng.

---

## Document Lineage (P4)

| Version | Document | Focus | Status |
|---------|----------|-------|--------|
| v1 | PLAN-v1-tablet-second-display-adb.md | Kiến trúc + lộ trình 5 phase (baseline 1080p60) | Superseded by v1.1 |
| v1.1 | (in-place, §15 Addendum) | High-bandwidth profile 4K@120fps: NCM transport, HEVC bắt buộc, R8/R9 | Superseded by v2 |
| v2 | PLAN-v2-tablet-second-display-adb.md | Live device data (HONOR ROD2-W09 3K144) + Settings module + M0–M5 + full test plan | ✅ Current |

## Related Documents (P6)

| Document | Relationship | Status |
|----------|-------------|--------|
| (chưa có) | — | — |

**Suggested Next Documents**:
- `SPEC-v1-tablet-display-wire-protocol.md` — đặc tả protocol chi tiết (message layout, state machine) trước khi code P0
- `GUIDE-v1-iddcx-dev-environment.md` — setup WDK, test-signing, deploy driver lên máy dev
- `TASK-v1-tablet-display-tracker.md` — Master Task File theo dõi P0–P4

---

## Token Summary
```
Skill prompt:     ~4,500 tokens
Files scanned:    2 glob queries (local — không có file liên quan)
External queries: 0 MCP + 6 web searches
Output:           ~9,000 tokens
TOTAL:            ~25,000 tokens
Context usage:    ~25% of 200K window
Budget status:    OK
```

---

**Unresolved questions** (cần bạn chốt trước khi vào P0):
1. Tablet cụ thể là máy gì (model, Android version, có stylus không)? → quyết định resolution/Hz mục tiêu + có cần PT_PEN ở P2 không.
2. Mục tiêu cuối là dùng cá nhân hay phát hành public? → quyết định chiến lược driver signing (R1) ngay từ P1.
3. PC dùng GPU gì (NVIDIA/Intel/AMD)? → thứ tự ưu tiên encoder MFT khi dev.
