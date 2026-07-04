# RCA-v1-resolution-stuck-800x600

**Created**: 2026-07-03 · **Mode**: Planning · **Type**: RCA · **Level**: L2
**Context**: `DriverManager.cs`, `VirtualDisplayConfigurator.cs`, `DeviceCaps.cs`, `StreamingCoordinator.cs`, `App.xaml.cs`, `SettingsWindow.xaml.cs`, `C:\VirtualDisplayDriver\vdd_settings.xml` (đọc trực tiếp, không cần web research — bug nằm 100% trong code của mình)

---

## Executive Summary

```
┌─────────────────────────────────────────────────────────────┐
│ 2 ROOT CAUSE — CẢ 2 ĐỀU LÀ BUG WIRING, KHÔNG PHẢI BUG LOGIC  │
│                                                               │
│ RC1 (HIGH conf): Settings dialog hiển thị 2560×1600 vì       │
│     App.xaml.cs gọi `new SettingsWindow()` (parameterless)   │
│     → tự động dùng DeviceCaps.Placeholder, KHÔNG BAO GIỜ     │
│     đọc CurrentDeviceCaps thật từ StreamingCoordinator.      │
│                                                               │
│ RC2 (MEDIUM conf): Display 2 vẫn 800×600 vì driver restart   │
│     cần quyền Admin — cần xác nhận exe đang chạy có phải     │
│     bản ĐÃ sửa app.manifest hay chưa (2 bug độc lập chồng    │
│     lên nhau: SxS-crash-do-manifest-thiếu-dependency đã sửa  │
│     TRƯỚC, nhưng chưa chắc user đã chạy lại đúng bản mới).   │
└─────────────────────────────────────────────────────────────┘
```

## MECE Decomposition

```
Vấn đề "resolution sai/thấp"
├── A. Hiển thị trong app (Settings dialog) → RC1, đã xác nhận CODE-LEVEL
├── B. Giá trị thật trong Windows (Display Settings, 800×600) → RC2, cần verify runtime
└── C. Giá trị ghi trong vdd_settings.xml → CẦN KIỂM TRA (có thể B là do C sai, hoặc do C đúng nhưng chưa restart)
```

---

## Root Cause #1 — Settings dialog dùng Placeholder, không dùng CAPS thật

**Evidence (CONFIG-BASED, đọc trực tiếp)**:
- `SettingsWindow.xaml.cs:41` — `public SettingsWindow() : this(new SettingsStore(), DeviceCaps.Placeholder)`
- `DeviceCaps.cs:24` — `public static DeviceCaps Placeholder { get; } = new(2560, 1600);` — **trùng khớp chính xác với độ phân giải laptop** (không phải trùng hợp ngẫu nhiên, người viết code trước chọn số này làm "giá trị mặc định hợp lý" mà không để ý nó = độ phân giải máy dev)
- `App.xaml.cs:65` — `settingsMenuItem.Click += (_, _) => new SettingsWindow().ShowDialog();` — **gọi constructor không tham số**, không truyền `_streamingCoordinator.CurrentDeviceCaps` (property đã tồn tại sẵn, `StreamingCoordinator.cs:59`, chỉ là KHÔNG AI GỌI NÓ)

**5 Whys**:
1. Tại sao Settings hiện sai resolution? → Vì nó luôn dùng `DeviceCaps.Placeholder`.
2. Tại sao dùng Placeholder dù đã có CAPS thật? → Vì `App.xaml.cs` gọi `new SettingsWindow()` (parameterless).
3. Tại sao constructor parameterless tồn tại và được gọi thay vì bản đủ tham số? → Vì `SettingsWindow(SettingsStore, DeviceCaps)` (internal, đủ tham số) được thêm ở session M4, còn `App.xaml.cs` viết TRƯỚC khi `StreamingCoordinator.CurrentDeviceCaps` tồn tại (session 3+) — 2 phần code viết ở 2 thời điểm khác nhau, không ai nối lại.
4. Tại sao không có test bắt được việc này? → Vì đây là lỗi **wiring giữa UI event handler và business logic**, loại lỗi mà unit test (test riêng `SettingsWindow` hoặc riêng `StreamingCoordinator`) không bao giờ chạm tới — chỉ lộ ra khi chạy tay thật (đúng như DoD checklist AP1 đã cảnh báo: "unit test không đủ bằng chứng").
5. Tại sao AI code hiện tượng, không bị chặn giữa chừng? → App vẫn build sạch, chạy được, ShowDialog() thành công — không có exception nào để báo hiệu.

**Causal Chain**: Trigger (thêm `CurrentDeviceCaps` ở session sau nhưng không update lại callsite cũ) → Amplifier (parameterless constructor tồn tại làm callsite cũ "vẫn compile được", không lộ lỗi) → Symptom (Settings dialog hiện nhầm resolution laptop).

**Fix Required**: Sửa `App.xaml.cs:65` và `MainWindow.xaml.cs` (nút Settings) truyền `_streamingCoordinator.CurrentDeviceCaps` thật vào `SettingsWindow`. Cân nhắc XÓA hẳn constructor parameterless để loại lỗi tương tự tái diễn (fail-fast tại compile-time thay vì runtime).

---

## Root Cause #2 — Display 2 vẫn 800×600 (driver chưa reload)

**Evidence**:
- `VirtualDisplayConfigurator.cs:230-291` (`TryRestartDriver`) — comment ghi rõ: *"restarting a PnP device node requires Administrator privileges... verified empirically: `devcon restart` → 'Restart failed' while `IsInRole(Administrator)` == false"*.
- `app.manifest` (đã sửa ở lượt trước) yêu cầu `requireAdministrator` — NHƯNG bản `.exe` user chạy trong ảnh chụp **chưa chắc là bản đã build LẠI sau khi sửa manifest** (câu hỏi "b build app mới nhất chưa" của user gợi ý họ nghi ngờ đúng điều này).
- `800×600` khớp chính xác với giá trị mặc định gốc của VDD driver TRƯỚC khi bất kỳ ai sửa `vdd_settings.xml` — tức là **file có thể đã được ghi đúng 3000×1920 (ApplyResolution không cần Admin, chỉ ghi file text) nhưng driver CHƯA từng đọc lại** vì bước restart thất bại.

**Chưa đủ bằng chứng để CONFIRM 100%** (khác RC1) — 2 khả năng:
- (a) User chạy bản `.exe` cũ (trước fix manifest) → không có quyền Admin → restart luôn fail → ĐÃ GIẢI THÍCH ĐỦ.
- (b) User đã chạy bản mới (có manifest, có Admin) nhưng `devcon restart *Root\MttVDD*` vẫn fail vì lý do khác (driver instance ID không khớp pattern, hoặc cần chờ vài giây sau install trước khi restart được).

**Fix Required**: (1) Xác nhận với user đang chạy đúng bản `.exe` mới nhất trong `APP_BUILD` (không phải bản cũ trong `bin\Debug`). (2) Thêm log rõ ràng hơn ở `DriverManager.EnsureReady` — hiện tại log có nhưng nằm trong Debug.WriteLine, cần đảm bảo ghi vào `%TEMP%\displaybridge-host.log` để user tự xem được kết quả từng bước [1/3][2/3][3/3] mà không cần hỏi tôi.

---

## False Positive Classification

Không có false positive — cả 2 phát hiện đều là bug thật trong code hiện tại (đọc trực tiếp source, không phải suy đoán từ log).

## Impact vs Effort Fix Matrix

| Fix | Impact | Effort | Priority |
|-----|--------|--------|----------|
| RC1: truyền CurrentDeviceCaps vào SettingsWindow | Cao (UI hiển thị sai gây hiểu nhầm nghiêm trọng) | Rất thấp (1-2 dòng) | P0 |
| RC2: xác nhận chạy đúng bản .exe mới + tăng cường log | Cao (chặn hẳn tiêu chí PASS #3/#4) | Thấp | P0 |

## Corrective Actions (ngay)
1. Sửa `App.xaml.cs` + `MainWindow.xaml.cs` truyền `CurrentDeviceCaps` thật.
2. Xóa constructor `SettingsWindow()` parameterless (buộc mọi callsite phải truyền DeviceCaps — fail compile-time nếu quên).
3. Verify lại E2E với đúng bản `.exe` mới nhất, log đầy đủ [1/3][2/3][3/3] của `DriverManager.EnsureReady`.

## Preventive Actions (lâu dài)
- Khi thêm 1 property/field mới đại diện "trạng thái sống" (như `CurrentDeviceCaps`), grep toàn repo tìm mọi callsite cũ đang dùng giá trị tĩnh/placeholder tương đương, không chỉ thêm property rồi dừng.

---

## Lessons Learned
- **Điều tốt**: `DeviceCaps.Placeholder` và `SettingsWindow` đủ tham số đều đã được viết đúng — hạ tầng có sẵn, chỉ thiếu 1 dòng nối.
- **Điều chưa tốt**: 2 bug này lẽ ra bắt được sớm hơn nếu có 1 bước "chạy tay mở Settings dialog" trong checklist verify của session 4 (M4) — lúc đó `CurrentDeviceCaps` chưa tồn tại nên không phải lỗi của session đó, nhưng session 7/8 (khi CAPS thật đã có) lẽ ra nên tự mở Settings dialog kiểm tra thay vì chỉ test qua `Screen.AllScreens`.

## Document Lineage
| Version | Focus | Status |
|---------|-------|--------|
| RCA-v1-resolution-stuck-800x600.md | RC1 (settings wiring) + RC2 (driver restart elevation) | ✅ Current |
