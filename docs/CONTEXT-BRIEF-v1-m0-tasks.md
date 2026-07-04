# CONTEXT-BRIEF-v1-m0-tasks

**Purpose**: Quick reference untuk coding agent — hardware specs, profiles, protocol, M0 tasks, folder layout.  
**Date**: 2026-07-03  
**Prepared for**: DisplayBridge development team (M0–M5 implementation)

---

## 1. Hardware Profile (Chốt)

| Item | Spec |
|------|------|
| **Tablet** | HONOR ROD2-W09 (MagicPad), Android 16 (SDK 36) |
| **Panel** | 3000×1920 (native landscape), 400dpi, 60/90/120/144Hz modes |
| **SoC** | Qualcomm SM8635 (Snapdragon 8s Gen 3) |
| **Decoder** | HEVC: 260–1200fps@1080p; AVC: 120–500fps@1080p (từ media_codecs_performance_cliffs_v1.xml) |
| **PC** | AMD Ryzen 9 8945HX + NVIDIA RTX 5060 Laptop (Blackwell NVENC, driver 32.0.15.8205) |

---

## 2. Video Profiles (Từ PLAN-v2 §5)

| Profile | Resolution × FPS | Codec | Bitrate (preset High) | Throughput | Ghi chú |
|---------|------------------|-------|-------|-----------|---------|
| **P-Ultra** | 3000×1920 @ 144Hz | HEVC L5.2 | ~83 Mbps (0.10bpp) | 10.4 MB/s | Nếu M0.2 pass ≥15MB/s |
| **P-High** (default) | 3000×1920 @ 120Hz | HEVC L5.1/5.2 | ~69 Mbps (0.09bpp) | 8.6 MB/s | Cân bằng latency/smooth |
| **P-Mid** | 3000×1920 @ 90Hz | HEVC hoặc H.264 L5.2 | ~52 Mbps (0.07bpp) | 6.5 MB/s | Fallback PC yếu |
| **P-Safe** | 3000×1920 @ 60Hz | H.264 L5.1 | ~35 Mbps (0.05bpp) | 4.4 MB/s | Tương thích max |
| P-Eco | 2250×1440 @ 60Hz | H.264 | ~19 Mbps (0.03bpp) | 2.4 MB/s | Pin/USB2 yếu |

**Key**: H.264 Max@3K = 90Hz (MaxMBPS limit 2,073,600); ≥120Hz bắt buộc HEVC.

---

## 3. Protocol Structure

### Sockets
- **Video socket**: `:29500` (tablet lắng nghe, nhận H.265/H.264 NAL units)
- **Control socket**: `:29501` (2 chiều, handshake + settings + stats)

### Control Messages (PLAN-v2 §4.3)

| Type ID | Name | Chiều | Payload | Mục đích |
|---------|------|-------|---------|---------|
| `0x10` | `SETTING_REQUEST` | tablet → PC | key(u8), value(varint) | Tablet xin đổi setting (floating button) |
| `0x11` | `CONFIG_UPDATE` | PC → tablet | list<key,value> | Apply ngay, không ngắt video ⚡ live |
| `0x12` | `MODE_CHANGE` | PC → tablet | w,h,hz,codec,csd | Flush decoder, chờ IDR mới ⟳ re-mode |
| `0x13` | `MODE_ACK` | tablet → PC | status | Decoder sẵn sàng nhận stream |
| `0x14` | `STATS` | tablet → PC | fps,decodeMs,queueDepth,dropped | 1Hz, nuôi ABR + HUD |

---

## 4. Settings Catalog (Rút gọn, từ PLAN-v2 §4.1)

| Setting | Giá trị | Default | Type Apply |
|---------|--------|---------|-----------|
| **Display** |
| Resolution | Native 3K / 2250×1440 / 1500×960 / Custom | Native | ⟳ re-mode |
| Refresh rate | 60 / 90 / 120 / 144 Hz | 120 | ⟳ re-mode |
| Orientation | Landscape / Portrait / Auto-rotate | Landscape | ⟳ re-mode |
| Monitor position | left/right/top/bottom | right | ✎ SetDisplayConfig |
| **Streaming** |
| Codec | Auto (HEVC→H.264) / force HEVC / force H.264 | Auto | ⟳ re-mode |
| Quality preset | Low 0.04bpp / Balanced 0.07 / High 0.10 / Ultra 0.14 / Custom Mbps | High | ⚡ live |
| FPS cap | ≤ refresh rate | = refresh | ⚡ live |
| Adaptive bitrate | on/off | on | ⚡ live |
| Latency priority | Latency-first / Smooth-first | Latency | ⚡ live |
| **Input** |
| Touch → PC | on/off | on | ⚡ live |
| Pen pressure | on/off + curve (γ 0.5–2.0) | on, γ=1 | ⚡ live |
| Keyboard forward | on/off | on | ⚡ live |
| **Connection** |
| Transport | Auto (NCM→ADB) / force ADB / force NCM | Auto | ⟳ reconnect |
| Auto-connect on plug | on/off | on | ✎ host |
| Ports (video/control) | custom | 29500/29501 | ⟳ reconnect |
| **Diagnostics** |
| Stats overlay | off/mini/full | off | ⚡ live |
| Latency HUD | on/off | off | ⚡ live |
| Log level | error/info/debug | info | ⚡ live |

Legend: ⚡ live = apply ngay; ⟳ re-mode = mode switch (decoder flush ~1–2s); ✎ = PC-side OS setting.

---

## 5. M0 Tasks (Definition of Done, từ PLAN-v2 §8)

| Task | Nội dung | Test đi kèm | DoD |
|------|---------|-------------|-----|
| **M0.1** | Repo scaffold 4 project (DisplayBridge.Host, Core, Native; tests; windows-driver; android-client; tools) | build script xanh | CI build pass |
| **M0.2** | **bench-transport**: adb raw socket + iperf3 NCM, đo MB/s thực tế | báo cáo ADB vs NCM | chốt throughput T1 vs T2 (gate R11) |
| **M0.3** | **codec-probe app**: dump `getSupportedPerformancePoints()` HEVC/AVC | assert HEVC ≥3K@120 | go/no-go decode |
| **M0.4** | Protocol schema v0 (YAML) + codegen C#/Kotlin từ 1 nguồn | unit roundtrip 100% message type | protocol lib 2 đầu pass fuzz |
| | | | |
| **DoD M0 gate** | Throughput số ghi docs / T1/T2 quyết định / protocol lib pass basic test | | Sẵn sàng bắt M1 |

---

## 6. Folder Structure (Vừa tạo)

```
APP_share/
├── docs/                                        ← plans, specs, trackers
├── pc-host/                                     ← Visual Studio solution (C#/C++)
│   ├── src/
│   │   ├── DisplayBridge.Host/                  ← WPF app, tray, UI
│   │   ├── DisplayBridge.Core/                  ← C#: protocol, adb, ABR, settings
│   │   └── DisplayBridge.Native/                ← C++: MF HEVC encoder, IddCx, input
│   └── tests/
│       ├── Core.Tests/                          ← xUnit
│       ├── Native.Tests/                        ← GoogleTest
│       └── Integration.Tests/                   ← fake-device tests
├── windows-driver/                              ← Fork Virtual-Display-Driver (IddCx)
│   └── (EDID builder, mode config 3K@60/90/120/144, shared texture export)
├── android-client/                              ← Gradle project (Kotlin)
│   └── app/src/
│       ├── main/                                ← App source
│       ├── test/                                ← Unit tests
│       └── androidTest/                         ← Instrumented tests
└── tools/
    ├── latency-harness/                         ← Embedded timestamp, E2E latency measurement
    ├── bench-transport/                         ← ADB raw socket + iperf3 throughput test
    └── fake-device/                             ← TCP simulator (unit/integration without tablet)
```

---

## 7. Key Decisions (Log)

| Ngày | Quyết định | Căn cứ |
|------|-----------|--------|
| 2026-07-03 | Native 3K@120Hz (default P-High), skip 4K | Panel thật 3000×1920, không phải 4K |
| 2026-07-03 | HEVC bắt buộc ≥120Hz | H.264 L5.2 max ~90Hz @3K (MaxMBPS math) |
| 2026-07-03 | PC-authoritative settings, 2 loại apply | Bài học Spacedesk: tránh confusion 2 source |
| 2026-07-03 | Codegen protocol từ 1 schema (không viết tay 2 bản) | Tránh drift C# ↔ Kotlin |

---

## 8. Risk Gates (Khống chế M0–M5)

| ID | Mô tả | Gate task |
|----|-------|-----------|
| R2 | IddCx IPC (critical path M2) | M2.1 + buffer 1 tuần |
| R8 | Decoder không đạt 3K@120 | M0.3 codec-probe (low risk: SD 8s Gen 3 dư sức) |
| R10 | MagicOS kill background app | M4.5 foreground service + D-04 soak test |
| R11 | USB speed thật (USB 2.0 vs 3.0) | M0.2 bench-transport (chốt T0.5) |

---

## 9. Next Steps

1. **M0.1**: Tạo 4 VS solution/Gradle project, CI build.
2. **M0.2**: Chạy `bench-transport` (adb raw socket + iperf3 NCM) → chốt throughput.
3. **M0.3**: APK codec-probe → verify HEVC support 3K@120.
4. **M0.4**: Schema YAML + code generator (C#/Kotlin) → unit test roundtrip.

**Sẽ cần**: adb (Android SDK), NVIDIA NVENC docs, Snapdragon 8s Gen 3 decoder specs (đã có từ firmware).

---

## References

- **PLAN-v2-tablet-second-display-adb.md**: Full plan, settings design, test pyramid.
- **TASK-v1-tablet-display-tracker.md**: Master task tracker.
- PLAN-v1 + addendum: Architecture layer, transport 3-tier, risk catalog.
