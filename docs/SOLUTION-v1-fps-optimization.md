# SOLUTION-v1-fps-optimization

**Created**: 2026-07-04 · **Mode**: Planning · **Type**: SOLUTION · **Level**: L3
**Context**: User báo cáo FPS ~20fps "rất rất tệ", yêu cầu tối thiểu 60fps lý tưởng 120fps, tham khảo cấu hình Spacedesk Android
**Status**: Completed — 3 bug thật đã tìm + sửa trong đêm, cần user verify tay lúc dậy

---

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Bug #1 (nghiêm trọng nhất): FPS/bitrate KHÔNG BAO GIỜ được truyền vào encoder** | Hàm khởi tạo native `DisplayBridge_CaptureInitWithCodec` chỉ nhận codec, dùng cứng `kDefaultFps=60`/`kDefaultBitrateKbps=12000` — bỏ qua hoàn toàn Hz thật (60/90/120) và bitrate đúng cho 3000×1920 (~69Mbps theo PLAN-v2) | Đọc trực tiếp `CaptureEncodeExports.cpp` dòng 18-19, xác nhận qua `grep` call site `native.Init(_activeCodec)` không truyền fps/bitrate | CAO |
| 2 | **Bug #2: `DuplicateOutput` fail 1 lần sau driver restart → rơi về JPEG ~4fps vĩnh viễn** | DXGI có hành vi đã biết: ngay sau khi đổi resolution/restart driver, `IDXGIOutput1::DuplicateOutput` fail tạm thời vì desktop đang tái cấu hình — code cũ thử đúng 1 lần rồi bỏ cuộc, không retry | **Tái hiện được LIVE trên máy user**: log thật cho thấy sau mỗi `DriverManager.EnsureReady` restart, `Native capture unavailable ... error code 6` rồi `GdiScreenCapture initialized -- JPEG fallback active (~4fps)` | CAO |
| 3 | **Bug #3: Đổi codec qua Settings PC (H.264→H.265) không thật sự áp dụng** | `PushSettingsChange` nhánh `ReMode` gửi `MODE_CHANGE` báo Android chuẩn bị đổi codec, nhưng KHÔNG gọi lại native encoder — Android chờ HEVC trong khi PC vẫn encode H.264 | Đọc code `PushSettingsChange`, đối chiếu đúng class bug đã tìm ở CAPS-path trước đó (session 12/13) | CAO |
| 4 | **GPU color-conversion (đã fix session 13) đạt 64-69fps cô lập** | `ID3D11VideoDevice/VideoProcessorBlt` + zero-copy D3D11 input cho MFT — đo bằng `NativeSmokeTest` mở rộng, native pipeline thuần túy (không qua socket/Android) | Native tool đo trực tiếp, 8s+15s sustained run ổn định | CAO |
| 5 | **Kết hợp 3 bug trên = giải thích đầy đủ "20fps rất tệ"** | User rất có thể đang chạy JPEG fallback (~4fps thật) phần lớn thời gian do Bug #2, và khi native chạy được thì bị giới hạn ở fps/bitrate sai do Bug #1 — 20fps là con số hỗn hợp giữa các lần rơi vào JPEG và lúc native chạy đúng | Suy luận có evidence từ log thật + code review | TRUNG BÌNH-CAO |

---

## Component Map — Toàn bộ pipeline (MECE, điểm nào từng là bottleneck)

```
Capture(DXGI) → Color-convert(GPU) → Encode(NVENC MFT) → Transport(TCP/ADB) → Decode(MediaCodec) → Present(SurfaceView)
     │                  │                    │                    │                    │                  │
  Bug #2 (mới)      Fixed session13      Bug #1 (mới)        Đã OK (TCP_NODELAY      Đã OK (async        Đã OK
  DuplicateOutput    GPU VideoProcessor   fps/bitrate         set cả 2 phía,          decode loop,        (drop-to-
  fail sau restart   Blt, zero-copy       hardcode 60fps/     31-33MB/s dư margin)    dequeue non-        latest, no
  → JPEG fallback    D3D11 input          12Mbps sai                                  blocking)           PTS pacing)
```

**Kết luận MECE**: 3/6 tầng từng có bottleneck thật (Capture, Color-convert TRƯỚC session 13, Encode) — 3/6 tầng còn lại (Transport, Decode, Present) đã đúng cấu hình từ trước, KHÔNG cần đổi thêm.

---

## Deep Internals — Cấu hình encoder tối ưu (NVENC qua Media Foundation)

### Bằng chứng từ research (NVIDIA + Microsoft docs)

> NVENC's Ultra-Low-Latency (ULL) tuning yields identical latency to High-Quality tuning despite disabling B-frames — nghĩa là **không cần đánh đổi chất lượng lấy độ trễ** trên NVENC hiện đại (RTX 5060 Blackwell). [NVIDIA Video Codec SDK Guide]

> H.264 Video Encoder MFT mặc định dùng slice encoding để giảm latency; `CODECAPI_AVLowLatencyMode` điều khiển hành vi này. [Microsoft Learn — H.264 Video Encoder]

**Đối chiếu code hiện tại** (`H264Encoder.cpp::ConfigureLowLatency`, đã đúng từ session 1, KHÔNG cần đổi):
- ✅ `CODECAPI_AVLowLatencyMode = TRUE`
- ✅ `CODECAPI_AVEncMPVDefaultBPictureCount = 0` (không B-frame)
- ✅ `CODECAPI_AVEncCommonRateControlMode = CBR`
- 🔶 `CODECAPI_AVEncMPVGOPSize = m_fps * 2` — ĐÚNG CÔNG THỨC nhưng `m_fps` trước Bug #1-fix luôn = 60 bất kể Hz thật → GOP sai nhịp khi chạy 90/120Hz. **Đã fix cùng Bug #1.**

### Bitrate đúng theo độ phân giải (không đổi công thức, chỉ cần TRUYỀN ĐÚNG)

Từ PLAN-v2 §5 (đã tính từ trước, giờ mới thực sự áp dụng nhờ Bug #1 fix):

| Profile | Bitrate đúng (bpp 0.09-0.10) | Trước fix (hardcode) | Chênh lệch |
|---------|------------------------------|----------------------|-----------|
| 3000×1920@120Hz | ~69,000 kbps | 12,000 kbps | **thiếu 5.75 lần** |
| 3000×1920@90Hz | ~52,000 kbps | 12,000 kbps | thiếu 4.3 lần |
| 3000×1920@60Hz | ~35,000 kbps | 12,000 kbps | thiếu 2.9 lần |

**Đây là phát hiện định lượng quan trọng nhất**: dù GPU color-conversion đã fix (session 13) giúp pipeline CÓ THỂ chạy 64-69fps, encoder trước đó luôn bị ép nén vào 12Mbps — ở 3000×1920, 12Mbps tương đương ~0.02 bpp (rất thấp), CBR ở mức này buộc encoder phải giảm chất lượng mạnh hoặc — với 1 số driver — bỏ khung hình để giữ bitrate target, cả 2 đều biểu hiện ra ngoài như "giật/lag" dù FPS đo được có thể không quá thấp.

---

## Hypothesis vs Evidence

| Giả thuyết | Kiểm chứng | Kết luận |
|---|---|---|
| "Đổi sang HEVC sẽ tự động nhanh hơn" | NVENC research: HEVC encode chậm hơn H.264 ~5-8% cùng preset/chất lượng (do thuật toán nén phức tạp hơn) — đã ghi rõ trong code comment session 13 | ❌ SAI — HEVC là tính năng thêm (nén tốt hơn → tiết kiệm băng thông), KHÔNG PHẢI cách tăng FPS. Fix FPS là Bug #1+#2, độc lập với việc chọn codec nào |
| "Cần giảm resolution để tăng FPS" (kiểu Spacedesk khuyến nghị "lower your display resolution") | Đúng về nguyên lý chung, nhưng KHÔNG PHẢI nguyên nhân ở đây — Bug #1/#2 là lỗi cấu hình/logic, không phải giới hạn phần cứng thật. Sau khi fix, 3000×1920 hoàn toàn khả thi ở 64-69fps đã đo (native, cô lập) | ❌ Không cần thiết như biện pháp đầu tiên — chỉ cân nhắc nếu SAU KHI fix 3 bug, FPS vẫn không đủ (không dự kiến) |
| "USB/ADB là nút cổ chai" | Đã bench từ session trước: 31-33 MB/s thật, dư 3x nhu cầu cao nhất (~10.4 MB/s cho HEVC 120Hz cao nhất) | ❌ Không phải nguyên nhân — transport đã dư margin lớn |

---

## RICE — Ưu tiên khắc phục

| Fix | Reach | Impact | Confidence | Effort | RICE | Đã làm đêm nay? |
|---|---|---|---|---|---|---|
| Bug #1 (fps/bitrate passthrough) | 10 | 9 | 0.9 | 1 | **81** | ✅ Fixed |
| Bug #2 (retry DuplicateOutputFailed) | 10 | 9 | 0.95 | 1 | **85.5** | ✅ Fixed |
| Bug #3 (codec ReMode re-init) | 6 (chỉ khi dùng PC Settings đổi codec) | 7 | 0.9 | 1 | 37.8 | ✅ Fixed |
| Đổi sang HEVC mặc định | 10 | 3 (tiết kiệm băng thông, không phải FPS) | 0.7 | 1 | 21 | Đã có sẵn từ session 13, không cần thêm |
| Giảm resolution mặc định | 10 | 2 | 0.4 | 2 | 4 | ❌ Không làm — chưa cần |

---

## UC5 Multi-Viewpoint

| Perspective | Key Concern | Verdict |
|---|---|---|
| Engineering | 3 bug đều thuộc loại "tính toán đúng nhưng không truyền/dùng tới nơi" — cùng 1 nhóm lỗi wiring đã lặp lại nhiều lần trong dự án (M0.4 protocol drift, M1 ChooseCodec, giờ là fps/bitrate) | ⚠️ Nên có checklist "mọi giá trị `Choose*`/`Estimate*` phải trace được tới đúng 1 nơi native/wire dùng nó" cho các session sau |
| Product/UX | User cảm nhận "rất tệ" dù hạ tầng (NVENC, GPU convert) đã đủ mạnh — đúng ngay bug config nhỏ có thể làm hỏng trải nghiệm hoàn toàn | ✅ Ưu tiên đúng, đây là bug ưu tiên P0 thật |
| SRE/Ops | Cần 1 dashboard/log rõ ràng hơn để tự chẩn đoán "đang chạy H.264 thật hay JPEG fallback, bitrate bao nhiêu" — hiện có nhưng rải rác trong log text, chưa có ở FPS overlay Android | 🔶 Đề xuất: thêm dòng bitrate/codec vào FPS overlay Android (đã có codec, thiếu bitrate) |

**Tổng hợp**: Đồng thuận — đây không phải vấn đề "cần tối ưu sâu hơn" mà là 3 bug wiring cụ thể, đã fix có bằng chứng code, cần verify tay 1 lần cuối vì môi trường tự động không click UAC được.

---

## Kết luận + Synthesis Diagram

```
┌──────────── TỪ "20FPS RẤT TỆ" ĐẾN FIX ────────────────────────────────────┐
│                                                                            │
│ [User: FPS rất tệ, muốn ≥60 lý tưởng 120]                                │
│         │                                                                 │
│         ▼                                                                 │
│ [Đọc code pipeline] ──▶ [Bug #1: fps/bitrate hardcode 60/12Mbps]         │
│         │                        │                                       │
│         │                        ▼ FIX: truyền _activeFps/_activeBitrateKbps│
│         │                                                                 │
│         ├──▶ [Chạy live thật] ──▶ [Bug #2: DuplicateOutputFailed         │
│         │                          sau driver restart → JPEG vĩnh viễn]  │
│         │                                │                                │
│         │                                ▼ FIX: retry 4 lần, 400ms delay │
│         │                                                                 │
│         └──▶ [Đọc PushSettingsChange] ──▶ [Bug #3: ReMode codec         │
│                                             không re-init native]        │
│                                                    │                      │
│                                                    ▼ FIX: _activeCodec +  │
│                                                       RecreateFrameSource │
│                                                                            │
│ Nền tảng đã đúng từ trước: GPU color-conversion (session13, 64-69fps đo  │
│ cô lập) + Transport (31-33MB/s dư 3x) + Decode/Present (đã OK)           │
│                                                                            │
│ Kỳ vọng sau fix: FPS thật ≈ FPS cô lập đã đo (64-69fps H.264,           │
│ ~62fps HEVC) tại 3000×1920 — vượt mục tiêu tối thiểu 60fps user yêu cầu  │
└────────────────────────────────────────────────────────────────────────────┘
```

---

## Sources & References

| # | Source | URL |
|---|--------|-----|
| 1 | NVIDIA — Introducing Video Codec SDK 10 Presets | https://developer.nvidia.com/blog/introducing-video-codec-sdk-10-presets/ |
| 2 | NVIDIA — NVENC Video Encoder API Programming Guide | https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html |
| 3 | Microsoft Learn — H.264 Video Encoder MFT | https://learn.microsoft.com/en-gb/windows/win32/medfound/h-264-video-encoder |
| 4 | Microsoft Learn — CODECAPI_AVLowLatencyMode | https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode |
| 5 | spacedesk — Performance Tuning | https://manual.spacedesk.net/PERFORMANCETUNING.html |
| 6 | spacedesk — Framerate per second | https://manual.spacedesk.net/Frameratepersecond.html |

## Document Lineage

| Version | Focus | Status |
|---------|-------|--------|
| SOLUTION-v1-fps-optimization.md | RCA + fix 3 bug FPS + research NVENC/spacedesk tuning | ✅ Current |

## Việc cần user verify khi dậy

1. Mở `DisplayBridgeHost-v0.1.0-alpha.exe` (bản build lại đêm nay, chưa kịp copy vào `APP_BUILD` — xem TASK tracker session 14 để build lại hoặc tôi sẽ làm khi bạn báo) — bấm Yes UAC.
2. Xem FPS overlay góc trên-trái tablet — kỳ vọng ≥60fps (H.264), không còn thấy "JPEG fallback".
3. Nếu vẫn thấy JPEG fallback lặp lại, gửi tôi log `%TEMP%\displaybridge-host.log` — có thể còn 1 bug khác chưa lộ ra được vì môi trường tôi không click UAC để tự chạy live được.

---

# PHỤ LỤC v1.1 (2026-07-04 sáng) — Root cause #4 & #5, tìm ra sau khi đạt ~60fps

Sau khi 3 bug ở trên được fix, FPS thật đo được 55-64 (user xác nhận "6x") nhưng KHÔNG lên 120 dù:
- `Win32_VideoController` xác nhận VDD chạy thật 3000x1920@**120Hz** (không phải giới hạn refresh rate)
- CONFIG gửi đúng `@120Hz 96768kbps`

## Root cause #4 — `Task.Delay(5)` = trần 60fps (CONFIRMED)

**Cơ chế**: NVENC có pipeline latency 1-2 frame → `TryGetEncodedSample` trả rỗng ~mỗi call thứ 2 → `ServeClientAsync` rơi vào nhánh `frame is null` → `Task.Delay(5)`. Nhưng độ phân giải timer mặc định của Windows là **15.6ms** — "5ms" thực chất ngủ ~15.6ms.

**Số học khớp chính xác**: chu kỳ vòng lặp = acquire(~8ms@120Hz) + null-sleep(15.6ms) chia đều ≈ 15.6–18ms/frame giao được = **55-64fps** — đúng dải đo trong log bất kể 60Hz hay 120Hz.

**Fix**: null từ native source KHÔNG cần sleep — `AcquireFrame` bên trong đã block theo vsync (timeout 500ms), tự điều tốc vòng lặp. Chỉ sleep sau 100 null liên tiếp (chỉ xảy ra với stub test không có blocking nội tại — tránh unit test ăn 100% CPU).

**Kỳ vọng sau fix**: ~90-120fps (giới hạn còn lại: tốc độ NVENC encode 3000x1920 HEVC ~5-7ms/frame + băng thông adb forward + tốc độ decode/render MediaCodec phía tablet).

## Root cause #5 — Deadlock recreate pipeline khi client force-stop (CONFIRMED, tái hiện live 06:43)

**Chuỗi sự kiện**: app Android restart (áp settings) → socket video qua adb forward chết nửa chừng → `NetworkStream.Write` sync KHÔNG có timeout block vô hạn (send buffer đầy) → `VideoStreamServer.Stop()` WaitAll timeout 2s rồi bỏ đi → `RecreateFrameSourceForNewResolution` gọi `Dispose()` native encoder TRONG KHI thread serve vẫn đang ở trong `GetNextFrame()` → native globals bị tear down giữa call → host đứng vĩnh viễn (process Responding, log câm).

**Hệ quả nhìn từ user**: mỗi lần đổi settings trên tablet là host chết hình → tưởng nhầm "H265 hỏng".

**Fix**: (a) `stream.WriteTimeout = 5000`; (b) `_nativeCallLock` serialize `GetNextFrame`/`Shutdown` + re-check `_initialized` sau khi thắng lock.

## Ghi chú H.265 (không phải bug PC)

Đường HEVC PC↔tablet đã HOẠT ĐỘNG: ép `encode_pref=1` trực tiếp vào SharedPreferences qua `run-as` → PC nhận ngay `CAPS codecs=1` → chọn codec=1. Bug thật nằm ở **dialog Android**: RadioGroup Encode xếp NGANG làm nút "H.265 (HEVC)" tràn mép dialog → tap không trúng → lưu 0. Fix: xếp DỌC + Toast xác nhận giá trị đọc lại từ disk.
