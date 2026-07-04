# RESEARCH-v1-windows-touch-gesture-mechanism

**Created**: 2026-07-03
**Research Target**: Cơ chế Windows xử lý cử chỉ cảm ứng (tap/swipe/pinch/edge-swipe) qua digitizer — làm cơ sở thiết kế input injection cho DisplayBridge
**Mode**: Planning
**Level**: L2-Map
**Language**: vi
**Context**: PLAN-v2 (§4 Input settings), yêu cầu bổ sung "phải dùng được con trỏ chuột lẫn cảm ứng vuốt chạm"
**Status**: Completed

---

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Windows KHÔNG cần ta tự viết logic gesture** | OS có sẵn tầng nhận diện gesture (pinch/zoom/pan/rotate/2-finger-tap/press-and-tap) nằm NGAY TRÊN tầng touch contact thô — chỉ cần đưa đúng contact thô vào là Windows tự suy ra gesture | Microsoft Learn "Windows Touch Gestures Overview", "Getting Started with Multi-touch Messages" | CAO |
| 2 | **`InjectTouchInput`/`InjectSyntheticPointerInput(PT_TOUCH)` inject ở đúng tầng "contact thô"** | Không phải giả lập click chuột — đây là API đưa thẳng danh sách `POINTER_TOUCH_INFO` (tối đa 256 điểm) vào input stack, y hệt driver digitizer thật đưa vào | MS Learn `InjectTouchInput`, `InitializeTouchInjection` | CAO |
| 3 | **Hệ quả trực tiếp**: kiến trúc đã chọn từ PLAN-v1 (§5.5, dùng InjectSyntheticPointerInput) đã ĐÚNG tầng — pinch-zoom, 2-ngón xoay, vuốt cạnh mở Action Center sẽ tự động hoạt động nếu ta gửi đúng multi-touch thô, KHÔNG cần code riêng cho từng gesture | Suy luận trực tiếp từ Finding #1+#2 | CAO |
| 4 | **3 tầng message Windows cung cấp cho app**: `WM_TOUCH` (thô, per-finger) → `WM_GESTURE` (OS đã suy ra: zoom/pan/rotate/tap/press-tap) → `WM_POINTER` (hiện đại nhất, per-contact + app tự dùng `InteractionContext` API nếu muốn gesture riêng) | Ứng dụng bình thường (Windows Explorer, Edge, Photos...) lắng nghe `WM_GESTURE`/`WM_POINTER` — ta không kiểm soát việc này, chỉ kiểm soát input ta bơm vào | MS Learn Windows Touch Messages | CAO |
| 5 | **1 ngón = cần "cursor" hiển thị, không chỉ touch vô hình** | Nhiều app desktop truyền thống (không tối ưu cho touch) chỉ phản hồi tốt với con trỏ chuột di chuyển + click, không phải touch-tap. Đây là lý do yêu cầu "phải dùng được con trỏ chuột" của bạn hợp lý — không phải mọi thứ trên Windows đều là touch-first | Kinh nghiệm thực tế Windows tablet mode + review UX touch trên desktop app | TRUNG BÌNH |

---

## Overview

Windows xử lý input cảm ứng qua pipeline 3 tầng, từ thấp lên cao:

```
┌─────────────────────────────────────────────────────────────────┐
│  TẦNG 3: WM_GESTURE (OS tự suy ra)                              │
│  → zoom, pan, rotate, two-finger-tap, press-and-tap             │
│  → App KHÔNG cần biết có bao nhiêu ngón, chỉ nhận "đã zoom 1.5x"│
├─────────────────────────────────────────────────────────────────┤
│  TẦNG 2: WM_POINTER (hiện đại, per-contact + InteractionContext)│
│  → App tự làm gesture riêng nếu muốn (vd: Photos app pinch)     │
├─────────────────────────────────────────────────────────────────┤
│  TẦNG 1: WM_TOUCH / contact thô (POINTER_TOUCH_INFO)            │
│  → Vị trí, ID ngón, pressure, down/move/up — ĐÂY LÀ TẦNG        │
│    InjectTouchInput/InjectSyntheticPointerInput ĐANG BƠM VÀO    │
└─────────────────────────────────────────────────────────────────┘
        ▲
        │ Digitizer thật (hoặc app của ta) đưa contact thô vào đây
```

**Kết luận quan trọng nhất**: vì DisplayBridge inject ở **Tầng 1** (contact thô), toàn bộ Tầng 2 và Tầng 3 (gesture recognition) là **miễn phí** — Windows tự làm, không cần code riêng cho pinch-zoom/rotate/vuốt-cạnh. Việc của ta chỉ là: map đúng tọa độ + đúng ID ngón + đúng timing down/move/up từ MotionEvent Android sang POINTER_TOUCH_INFO.

## Component Map — 2 chế độ input cần thiết kế (MECE)

Từ yêu cầu "phải dùng được con trỏ chuột để cảm ứng vuốt chạm", có 2 nhu cầu KHÔNG chồng lấn:

| Chế độ | Khi nào cần | Cơ chế | API |
|--------|------------|--------|-----|
| **A. Touch thật (multi-point)** | App/OS UI hỗ trợ touch tốt (Edge, Photos, Windows Ink, pinch-zoom ảnh, vuốt cạnh mở Action Center) | Inject contact thô nhiều ngón, để Windows tự suy gesture (Tầng 2-3 ở trên) | `InjectSyntheticPointerInput(PT_TOUCH)`, tối đa 10 điểm |
| **B. Con trỏ chuột hiển thị (cursor mode)** | App desktop cũ không tối ưu touch (nhiều phần mềm win32 legacy chỉ nhận click chuột); hoặc user muốn thao tác chính xác kiểu trackpad (1 ngón di chuyển con trỏ, tap = click, giữ+kéo = drag) | Di chuyển `SetCursorPos` + `mouse_event`/`SendInput(MOUSEINPUT)` theo vị trí ngón; **không** dùng touch injection | `SendInput(INPUT_MOUSE)` |

**Thiết kế đề xuất — Hybrid mode (mặc định)**:
```
1 ngón chạm + giữ yên/di chuyển chậm  → CURSOR mode (di chuyển con trỏ chuột thật,
                                          tap ngắn = left-click, giữ lâu = drag)
2+ ngón cùng lúc                       → TOUCH mode (inject multi-point thô,
                                          Windows tự nhận pinch/zoom/rotate/2-finger-tap)
Vuốt từ mép màn hình ảo (trong 20px biên) → TOUCH mode bắt buộc (để Action Center/
                                          Task View nhận được edge-swipe — các gesture
                                          này chỉ kích hoạt từ contact thô ở đúng vùng biên)
```
Đây khớp đúng với cách nhiều remote-desktop-có-cảm-ứng làm (ví dụ Windows Remote Desktop mobile client, Chrome Remote Desktop): mặc định hiển thị con trỏ cho thao tác 1 ngón (dễ nhìn, chính xác), nhưng chuyển sang touch injection thật khi phát hiện đa điểm hoặc gesture đặc thù.

## Cập nhật thiết kế cho DisplayBridge

### Settings mới (thêm vào catalog PLAN-v2 §4.1, nhóm Input)

| Setting | Giá trị | Default | Loại apply |
|---------|--------|---------|-----------|
| Input mode | Cursor-only / Touch-only / **Hybrid (auto)** | Hybrid | ⚡ live |
| Cursor tap threshold | thời gian giữ trước khi coi là drag (ms) | 150ms | ⚡ live |
| Edge-swipe zone | độ rộng vùng biên kích hoạt touch bắt buộc (px, theo tỉ lệ màn ảo) | 24px | ⚡ live |

### Protocol: không cần message mới

Control socket đã có sẵn payload touch (pointerId, action, x, y, pressure, toolType) từ thiết kế M3 — đủ để PC-side tự quyết định Cursor/Touch mode dựa trên **số lượng pointer đang active cùng lúc** + **vị trí có nằm trong edge-zone không**. Không cần Android gửi thêm field "mode" — logic phân loại nằm hoàn toàn ở PC (đúng nguyên tắc PC-authoritative đã chốt ở PLAN-v2 §3).

### Cập nhật Native input injector (module A3, PLAN-v1 §4)

```
Control socket nhận TOUCH events
        │
        ▼
┌───────────────────────────────────┐
│ InputModeClassifier (PC, mới)     │
│  - đếm active pointers            │
│  - check vị trí trong edge-zone   │
└───────┬───────────────┬───────────┘
        │ 1 ngón,        │ ≥2 ngón HOẶC
        │ ngoài edge-zone│ trong edge-zone
        ▼                ▼
┌───────────────┐  ┌──────────────────────────┐
│ CursorInjector │  │ TouchInjector (đã có)    │
│ SendInput      │  │ InjectSyntheticPointer   │
│ (MOUSEINPUT)   │  │ Input(PT_TOUCH, multi)   │
└───────────────┘  └──────────────────────────┘
```

## Rủi ro / lưu ý

- **Chuyển đổi mode giữa chừng 1 thao tác** (vd: bắt đầu 1 ngón cursor rồi thêm ngón 2 giữa chừng để pinch): cần "nhả" cursor injection (mouse-up) trước khi chuyển sang touch injection để tránh trạng thái kẹt nút chuột ảo — xử lý ở `InputModeClassifier`.
- **Edge-swipe zone theo Hz/DPI của màn ảo**, không phải giá trị pixel cố định toàn cục — cần tính theo resolution đang chọn (đổi khi user đổi resolution ở settings).
- Đây là bổ sung thiết kế cho **M3 (Input 2 chiều)** trong roadmap — không ảnh hưởng M0 đã xong.

---

## UC5 Multi-Viewpoint (rút gọn)

| Perspective | Key Concern | Verdict |
|-------------|-------------|---------|
| Engineering | Không cần code gesture riêng, chỉ cần classifier đơn giản | ✅ effort thấp, đúng nguyên lý "để OS làm việc của OS" |
| Product/UX | User dùng app cũ (win32 legacy) và app mới (touch-first) đều mượt | ✅ Hybrid mode phủ cả 2 |
| SRE | Rủi ro kẹt trạng thái chuột khi chuyển mode giữa chừng | ⚠️ cần xử lý cẩn thận trong M3 |

**Tổng hợp**: Kiến trúc InjectSyntheticPointerInput đã chọn từ đầu dự án là đúng tầng kỹ thuật; bổ sung Hybrid Cursor/Touch classifier là điều chỉnh nhỏ, không phải thay đổi kiến trúc lớn.

---

## Sources & References

| # | Source | URL |
|---|--------|-----|
| 1 | Microsoft Learn — Windows Touch Gestures Overview | https://learn.microsoft.com/en-us/windows/win32/wintouch/windows-touch-gestures-overview |
| 2 | Microsoft Learn — Getting Started with Multi-touch Messages | https://learn.microsoft.com/en-us/windows/win32/wintouch/getting-started-with-multi-touch-messages |
| 3 | Microsoft Learn — InjectTouchInput | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injecttouchinput |
| 4 | Microsoft Learn — InitializeTouchInjection | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-initializetouchinjection |
| 5 | Microsoft Support — Touch gestures for Windows | https://support.microsoft.com/en-us/windows/touch-gestures-for-windows-a9d28305-4818-a5df-4e2b-e5590f850741 |
| 6 | Microsoft Learn — Precision Touchpad Input | https://learn.microsoft.com/en-us/windows/win32/input-precisiontouchpad/precision-touchpad-portal |

## Document Lineage

| Version | Document | Focus | Status |
|---------|----------|-------|--------|
| v1 | RESEARCH-v1-windows-touch-gesture-mechanism.md | Cơ chế gesture + thiết kế Hybrid Cursor/Touch | ✅ Current |

## Related Documents

- [PLAN-v2-tablet-second-display-adb.md](PLAN-v2-tablet-second-display-adb.md) — §4.1 Settings catalog (cập nhật theo research này)
- [TASK-v1-tablet-display-tracker.md](TASK-v1-tablet-display-tracker.md) — M3 sẽ implement InputModeClassifier
