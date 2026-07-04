#!/system/bin/sh
# android-echo-server.sh
# Chạy trên PC, điều khiển thiết bị Android qua `adb shell` để mở nc listener
# nhận dữ liệu throughput test và ghi thẳng vào /dev/null (không giữ trên disk).
#
# Thiết bị có toybox nc thật (đã kiểm tra: `adb shell which nc` -> /system/bin/nc,
# hỗ trợ `-l -p <port>` listen mode). Vì vậy dùng TCP socket thật, KHÔNG cần proxy
# qua adb push.
#
# Cách dùng (thường được bench-adb.ps1 tự gọi qua `adb shell`, không cần chạy tay):
#   adb shell "sh /data/local/tmp/android-echo-server.sh <port> <duration_sec>"
#
# Server nhận dữ liệu trong <duration_sec> giây rồi tự thoát (dùng timeout của toybox
# nếu có, fallback bằng `nc -l -p <port> > /dev/null` chạy 1 lần rồi thoát khi client
# đóng kết nối).

PORT="${1:-29600}"
DURATION="${2:-15}"

# toybox timeout có sẵn trên hầu hết ROM Android hiện đại (kể cả HONOR/MagicOS).
# Nếu không có timeout, nc sẽ tự thoát khi client đóng socket (đủ dùng cho bench).
if command -v timeout >/dev/null 2>&1; then
  timeout "${DURATION}" nc -l -p "${PORT}" > /dev/null
else
  nc -l -p "${PORT}" > /dev/null
fi
