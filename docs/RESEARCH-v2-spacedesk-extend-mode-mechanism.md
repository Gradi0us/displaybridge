# RESEARCH-v2-spacedesk-extend-mode-mechanism

**Created**: 2026-07-03
**Research Target**: Cơ chế Spacedesk tạo màn hình ảo Extend thật (không phải mirror) — xác minh lại kiến trúc M2 sau khi user phát hiện bản hiện tại (mirror + JPEG/H.264) KHÔNG đạt yêu cầu ban đầu "màn hình phụ độc lập"
**Mode**: Planning · **Level**: L3-Analyze · **Language**: vi
**Status**: Completed

---

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Kiến trúc gốc (PLAN-v1/v2, IddCx) là ĐÚNG — chưa từng sai** | Spacedesk trên Windows 10+ dùng chính xác **WDDM IddCx Indirect Display Driver** — cùng công nghệ đã chọn từ đầu dự án | Chocolatey spacedesk-server changelog + MS Learn IddCx overview | CAO |
| 2 | **Bug gốc rễ: session 4-6 đã lệch khỏi kiến trúc gốc mà không ai để ý** | Vì M2 (driver) bị block bởi thiếu toolchain, các session sau dùng GDI/DXGI **capture màn hình chính** (mirror) làm lối tắt demo — nhưng lối tắt này KHÔNG BAO GIỜ tạo ra 1 monitor mới trong Windows, chỉ gửi ảnh đi. Đây là lý do Windows không nhận tablet là màn hình riêng, và ảnh vỡ (capture đúng 2560×1600 của laptop, gửi sang panel 3000×1920 tỉ lệ khác của tablet → méo/vỡ khi scale) | So khớp code hiện tại (`GdiScreenCapture`/`DesktopDuplicationCapture` chụp primary output) với kiến trúc PLAN §5.1 | CAO |
| 3 | **IddCx chạy ở Session 0, driver tự cấp DirectX surface — không cần "capture" gì cả** | Khi có driver thật, Windows tự coi đó là 1 adapter/monitor, compose desktop lên swapchain của driver — không có bước "chụp màn hình chính" nào trong luồng đúng | MS Learn IddCx Objects | CAO |
| 4 | **EDID/resolution/refresh phải khai đúng theo THIẾT BỊ THẬT, không cố định** | `IddCxMonitorArrival` nhận EDID nhị phân + mode list do driver tự định nghĩa; OS thương lượng refresh rate qua `DISPLAYCONFIG_RATIONAL` khớp đúng mode driver khai — đây chính là cơ chế để tablet hiện ĐÚNG độ phân giải riêng của nó (3000×1920@120), không bị ăn theo laptop | MS Learn `IddCxMonitorArrival`, `IDDCX_DISPLAYCONFIGPATH` | CAO |
| 5 | **WDK là công cụ THỨ 3 cần cài, khác MSVC/Windows SDK đã có** | WDK (Windows Driver Kit) cài qua VS Installer → tab "Individual Components" → "Windows Driver Kit" (component ID `Microsoft.VisualStudio.Component.WDK`) — chưa cài trên máy (xác nhận qua registry + vswhere) | Live probe 2026-07-03 + MS Learn "Download the WDK" | CAO |

---

## INVERSION — giả định sai đã dẫn tới lối rẽ mirror

**Giả định sai: "Cứ demo được cái gì đó chạy trước, tối ưu kiến trúc sau."**
→ Lật ngược: việc chọn GDI/DXGI-capture-màn-hình-chính làm "bước đệm" (session 4-6) đã đi ĐÚNG mục tiêu ngắn hạn (có demo chạy được) nhưng **rẽ nhánh khỏi con đường dẫn tới Extend mode** — mirror và Extend không phải 2 mức độ hoàn thiện của CÙNG 1 kiến trúc, mà là **2 kiến trúc khác nhau hoàn toàn** (capture-and-forward vs. virtual-adapter). Không có lượng "tối ưu thêm" nào biến mirror thành Extend.
→ Chân lý: pipeline capture/encode (DXGI Desktop Duplication + Media Foundation H.264 + NVENC) session 6 xây dựng **KHÔNG UỔNG PHÍ** — phần "lấy hình ảnh → nén → gửi qua socket" giữ nguyên 100%; chỉ có NGUỒN của hình ảnh phải đổi: từ "chụp primary output" sang "đọc swapchain mà driver IddCx nhận từ DWM khi Windows coi tablet là 1 adapter riêng".

---

## Kiến trúc ĐÚNG cần chuyển sang (so sánh)

```
SAI (session 4-6, đang chạy)              ĐÚNG (cần chuyển sang, M2)
──────────────────────────────            ──────────────────────────────
Windows Desktop (1 màn: laptop)           Windows Desktop (2 màn: laptop + "tablet ảo")
        │                                          │                    │
   DXGI DuplicateOutput                       DWM render bt1      DWM render bt2 (driver IddCx)
   (primary, 2560×1600)                            │                    │
        │                                     (laptop panel)      IddCx swapchain
   H.264 encode                                                        │
        │                                                         H.264 encode
   gửi qua socket                                                      │
        │                                                         gửi qua socket
   Tablet hiển thị (mirror,                                            │
   SAI tỉ lệ → vỡ hình)                                          Tablet hiển thị
                                                                  (ĐÚNG native 3000×1920,
                                                                   Windows coi là Display 2 thật)
```

**Điều kiện để coi là ĐẠT YÊU CẦU** (tiêu chí Opus sẽ kiểm gắt):
1. Windows Settings → Display hiện **2 monitor riêng biệt** (không phải 1 màn nhân bản).
2. Kéo cửa sổ từ laptop sang "Display 2" → cửa sổ biến mất khỏi laptop, hiện trên tablet — đúng ngữ nghĩa Extend.
3. Độ phân giải "Display 2" trong Windows Settings hiện đúng **3000×1920** (hoặc landscape 3000×1920 tùy orientation), KHÔNG phải 2560×1600 hay bất kỳ số nào của laptop.
4. Ảnh hiển thị trên tablet KHÔNG méo/vỡ (vì giờ render đúng native resolution ngay từ đầu, không qua bước scale sai tỉ lệ).

---

## RICE — vị trí M2 trong roadmap sau phát hiện này

| Việc | R | I | C | E (giờ) | Verdict |
|------|---|---|---|---------|---------|
| **M2 IddCx driver thật (fork Virtual-Display-Driver, đã chọn từ PLAN-v1)** | 10 | 10 | 0.7 | 20-30 | ✅ BẮT BUỘC — không có lối tắt nào khác đạt được yêu cầu "màn hình phụ độc lập" |
| Tiếp tục tối ưu mirror (fix blur bằng cách scale đúng tỉ lệ) | 10 | 2 | 0.9 | 3 | ❌ REJECTED — dù sửa được blur, Windows vẫn không coi đó là monitor riêng, không đạt yêu cầu gốc |

**Kết luận Fermi effort**: 20-30 giờ công cho 1 dev quen C++/WDF — đây là ước lượng GỐC từ PLAN-v1 §8 (P1 IddCx, 2-3 tuần), không đổi. Việc mất công ở session 4-6 KHÔNG lãng phí (pipeline capture→encode→socket→decode dùng lại được 100%), chỉ là chưa đủ để gọi "M2 xong".

---

## Kết luận + Synthesis

```
┌────── TỪ SPACEDESK RESEARCH ĐẾN FIX M2 ─────────────────────────────┐
│                                                                      │
│ [Spacedesk = IddCx thật] ──xác nhận──▶ [Kiến trúc gốc PLAN-v1 ĐÚNG] │
│         │                                        │                  │
│         ▼                                        ▼                  │
│ [EDID/mode = per-monitor thật] ──▶ [Fix root cause: capture SAI     │
│                                     nguồn (primary screen) → phải   │
│                                     đổi thành driver swapchain]     │
│         │                                        │                  │
│         ▼                                        ▼                  │
│ [WDK = công cụ thứ 3 cần cài] ──▶ [Giữ nguyên: H.264/NVENC encoder, │
│  (khác MSVC+SDK đã có)             VideoStreamServer, protocol,     │
│                                     touch injector — chỉ thay NGUỒN │
│                                     capture]                        │
│                                                                      │
│ Tiêu chí PASS (Opus gate): 2 Display riêng · kéo cửa sổ qua được ·  │
│ đúng 3000×1920 · không vỡ hình                                      │
└──────────────────────────────────────────────────────────────────────┘
```

## Sources & References

| # | Source | URL |
|---|--------|-----|
| 1 | Chocolatey — spacedesk-server package | https://community.chocolatey.org/packages/spacedesk-server/1.0.42 |
| 2 | MS Learn — Indirect Display Driver Model Overview | https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview |
| 3 | MS Learn — IddCx Objects | https://learn.microsoft.com/en-us/windows-hardware/drivers/display/iddcx-objects |
| 4 | MS Learn — IddCxMonitorArrival | https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/nf-iddcx-iddcxmonitorarrival |
| 5 | MS Learn — Download the WDK | https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk |
| 6 | MS Learn — Visual Studio workload/component IDs (WDK) | https://learn.microsoft.com/en-us/visualstudio/install/workload-and-component-ids |

## Document Lineage

| Version | Focus | Status |
|---------|-------|--------|
| RESEARCH-v1-windows-touch-gesture-mechanism.md | Cơ chế touch/gesture (M3) | Reference, không đổi |
| **RESEARCH-v2-spacedesk-extend-mode-mechanism.md** | Xác nhận + fix root cause M2 (Extend mode) | ✅ Current |
