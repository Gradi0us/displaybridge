# RCA-v2-android-connect-adb-reverse-lifecycle

**Created**: 2026-07-04
**Research Target**: Android app khó/không kết nối tới PC Host (ECONNREFUSED chập chờn) dù `adb devices` nhận thiết bị
**Mode**: Planning | **Level**: L3-Analyze | **Type**: RCA | **Language**: vi
**Status**: Completed — fix đã xác định, giao subagent implement
**Researcher**: Claude (deep-research skill, session 18)

---

## Executive Summary

```
┌─────────────────────────────────────────────────────────────────┐
│  VERDICT: KHÔNG PHẢI lỗi app Android, KHÔNG PHẢI lỗi Host      │
│  listen/port. ROOT CAUSE = adb reverse tunnel KHÔNG BAO GIỜ    │
│  được PC Host tự thiết lập — nó chỉ tồn tại khi ai đó chạy tay │
│  `adb reverse`, và BỊ XOÁ mỗi khi adb server restart / rút cáp │
│  USB / reboot máy hoặc tablet / 2 bản adb khác version kill     │
│  server của nhau (máy này có 2 bản: winget + scrcpy).          │
│  Health: App code OK · Infra glue MISSING → fix ở PC Host      │
└─────────────────────────────────────────────────────────────────┘
```

## Key Findings

| # | Finding | Chi tiết | Bằng chứng | Độ tin cậy |
|---|---------|----------|------------|------------|
| 1 | **Host không bao giờ chạy adb reverse** | Toàn bộ codebase PC không có chỗ nào gọi `adb reverse`/`adb forward`. Tunnel hôm nay do thao tác tay (session 17, 08:16) | Grep toàn bộ `pc-host/src`: chỉ `AdbDeviceChecker` gọi `adb devices`; `StreamingCoordinator.cs:227` chỉ dùng cho driver auto-disable | CAO |
| 2 | **Tunnel đã biến mất khi kiểm tra live** | `adb reverse --list` trả về RỖNG lúc 2026-07-04 (sau ~vài giờ), trong khi `adb devices` vẫn thấy `AL9SBB4622000114 device` | Live capture session 18 | CAO |
| 3 | **2 bản adb.exe xung đột** | `where adb` → winget platform-tools **và** scrcpy v3.3.4 bundle. Client version mismatch → "killing adb server" → mất toàn bộ reverse rules | Live capture session 18 | CAO |
| 4 | **Android retry OK, không phải bug** | Cả 2 socket client retry mỗi 1s vô hạn (`retryDelayMs=1000`, `willRetry=true`) — hành vi đúng, nhưng bất lực vì lỗi nằm phía PC | `VideoStreamClient.kt:59`, `ControlSocketClient.kt:56` | CAO |
| 5 | **Host listen IPAddress.Any → không phải nguyên nhân** | `VideoStreamServer.cs:97` dùng `TcpListener(IPAddress.Any, port)` — adb server connect vào localhost đều tới được | `VideoStreamServer.cs:97` | CAO |

## Causal Chain

```
TRIGGER                      AMPLIFIER                         SYMPTOM
─────────                    ─────────                         ────────
adb server restart      →    Không có code nào re-apply   →   App Android
(scrcpy/winget adb           adb reverse (chưa từng có         ECONNREFUSED
 version conflict,           feature này — session 17           vô hạn dù adb
 rút/cắm USB, reboot)        setup HOÀN TOÀN thủ công)          devices OK
```

**5 Whys**:
1. Tại sao app không connect? → ECONNREFUSED tới 127.0.0.1:29500/29501 trên tablet.
2. Tại sao ECONNREFUSED? → Không có gì listen trên tablet-side loopback: adb reverse tunnel không tồn tại.
3. Tại sao tunnel không tồn tại? → adb server đã restart (hoặc USB replug) → mọi reverse rule bị flush.
4. Tại sao không được re-apply? → PC Host **chưa bao giờ có code** thiết lập adb reverse; bước này là thao tác tay chưa được product hoá.
5. Tại sao chưa được product hoá? → Session 17 fix nhanh bằng tay để debug crash native; chưa có task wire vào StreamingCoordinator (anti-pattern AP1 feature-partially-shipped).

## Fix Required (giao subagent)

**RC1-FIX (P0)**: Thêm `AdbReverseManager` vào `DisplayBridge.Core.Video`:
- API: `EnsureReverse(IReadOnlyList<(int devicePort,int hostPort)>) → (bool ok, string detail)` — chạy `adb reverse tcp:X tcp:Y` cho từng cặp; idempotent (adb reverse tự overwrite rule cùng port).
- Kiểm tra hiện trạng bằng `adb reverse --list` trước, chỉ apply khi thiếu → không spam adb mỗi tick.
- Tái sử dụng logic tìm adb.exe + timeout + never-throw của `AdbDeviceChecker` (tách shared helper hoặc copy pattern).
- Wire vào `StreamingCoordinator.OnAdbPollTick` (timer 7s có sẵn): mỗi tick, nếu `Connected` → gọi `EnsureReverse` cho VideoPort/ControlPort đang bound. Tick đầu tiên hiện delay 20s — thêm một lần apply ngay trong `Start()` (fire-and-forget, không block UI) để user không phải chờ 20s sau khi mở Host.
- Log rõ: apply thành công / thất bại / đã có sẵn.

**RC3-HARDENING (P2, tuỳ chọn)**: Cảnh báo trong log khi phát hiện >1 adb.exe trên PATH (version conflict risk).

## Validation Plan (tôi tự test sau khi agent xong)
1. `adb reverse --remove-all` → mở Host mới → trong ≤7s tunnel tự xuất hiện (`adb reverse --list` có 2 rule) → app connect được không cần thao tác tay.
2. Giả lập mất tunnel giữa phiên: `adb reverse --remove-all` khi đang stream → trong ≤7s tunnel tự hồi → app tự reconnect (retry 1s có sẵn).
3. Unit tests pass (82+), build sạch.

## Document Lineage
| Version | Document | Focus | Status |
|---------|----------|-------|--------|
| v1 | RCA-v1-resolution-stuck-800x600.md | Resolution handshake | Done |
| v2 | (this) | ADB reverse lifecycle | Active |

## Token Summary
~35k in (code reads + live probes) · ~3k out (doc). Context OK.
