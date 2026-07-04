<#
.SYNOPSIS
    M0.2 bench-transport - do throughput thuc te qua kenh adb forward (TCP tunnel qua USB).

.DESCRIPTION
    Chieu do: PC (Windows, client, GUI TcpClient) --> tablet (server, toybox `nc -l -p <port>`).
    Day la dung chieu video stream that (PC encode roi gui NAL units xuong tablet decoder qua :29500).

    Quy trinh:
      1. `adb forward tcp:<port> tcp:<port>` mo tunnel USB.
      2. `adb shell` khoi dong android-echo-server.sh (`nc -l -p <port> > /dev/null`) tren tablet,
         tu thoat sau <DurationSec + buffer> giay (dung lenh `timeout` cua toybox).
      3. PC mo TcpClient toi 127.0.0.1:<port> (duoc adb forward tunnel qua USB toi tablet),
         gui lien tuc buffer ngau nhien trong >= DurationSec giay, do so byte da gui.
      4. Tinh MB/s trung binh = TotalBytesSent / ElapsedSeconds / 1MB.
      5. So sanh voi nguong P-High (8.6 MB/s) va P-Ultra (10.4 MB/s).

.PARAMETER Port
    TCP port dung cho forward + nc listener. Default 29600 (khac video/control that :29500/:29501
    de khong dung do voi app khac dang phat trien song song).

.PARAMETER DurationSec
    So giay gui du lieu lien tuc. Default 15s (>= 10s theo yeu cau).

.PARAMETER BufferSizeKB
    Kich thuoc moi buffer gui 1 lan qua socket. Default 256KB.

.EXAMPLE
    .\bench-adb.ps1
    .\bench-adb.ps1 -Port 29610 -DurationSec 20
#>

param(
    [int]$Port = 29600,
    [int]$DurationSec = 15,
    [int]$BufferSizeKB = 256
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$remoteScript = "/data/local/tmp/android-echo-server.sh"

function Write-Section($msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

# ---- 0. Kiem tra thiet bi ----
Write-Section "0. Kiem tra ADB device"
$devices = & adb devices
$devices | ForEach-Object { Write-Host $_ }
$devicesText = $devices -join "`n"
if ($devicesText -notmatch "\bdevice\b") {
    Write-Error "Khong tim thay thiet bi ADB nao dang o trang thai 'device'. Dung lai."
    exit 1
}

# ---- 1. Push server script (idempotent) ----
Write-Section "1. Dam bao android-echo-server.sh da co tren thiet bi"
$env:MSYS_NO_PATHCONV = "1"
& adb push "$scriptDir\android-echo-server.sh" $remoteScript | Out-Host
& adb shell chmod 755 $remoteScript | Out-Host

# ---- 2. Don dep port cu (neu co) ----
Write-Section "2. Don dep forward/listener cu tren port $Port"
& adb forward --remove "tcp:$Port" 2>$null | Out-Null
& adb shell "pkill -f 'nc -l -p $Port'" 2>$null | Out-Null
Start-Sleep -Milliseconds 300

# ---- 3. Mo adb forward tunnel ----
Write-Section "3. adb forward tcp:$Port tcp:$Port"
& adb forward "tcp:$Port" "tcp:$Port" | Out-Host

# ---- 4. Khoi dong server tren tablet (background, tu thoat sau DurationSec+10s) ----
Write-Section "4. Khoi dong nc listener tren tablet (tu thoat sau $($DurationSec + 10)s)"
$serverDuration = $DurationSec + 10
$adbShellArgs = @("shell", "sh", $remoteScript, "$Port", "$serverDuration")
$serverProc = Start-Process -FilePath "adb" -ArgumentList $adbShellArgs -NoNewWindow -PassThru
Start-Sleep -Seconds 2   # cho nc len listen state

# ---- 5. Client PC: gui du lieu qua TcpClient toi 127.0.0.1:<port> ----
Write-Section "5. Gui du lieu qua adb forward tunnel ($DurationSec s, buffer ${BufferSizeKB}KB)"

$bufferSize = $BufferSizeKB * 1024
$buffer = New-Object byte[] $bufferSize
(New-Object Random).NextBytes($buffer)

$totalBytesSent = 0L
$sw = [System.Diagnostics.Stopwatch]::new()

try {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.NoDelay = $true
    $client.SendBufferSize = $bufferSize
    $client.Connect("127.0.0.1", $Port)
    $stream = $client.GetStream()

    $sw.Start()
    while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
        $stream.Write($buffer, 0, $buffer.Length)
        $totalBytesSent += $buffer.Length
    }
    $sw.Stop()
    $stream.Flush()
    $stream.Close()
    $client.Close()
}
catch {
    Write-Error "Loi khi gui du lieu qua adb forward tunnel: $_"
    Write-Host "GOI Y: neu bi 'Connection refused', co the nc listener tren tablet chua kip len, hoac da thoat. Kiem tra bang 'adb shell netstat -tln'." -ForegroundColor Yellow
    & adb forward --remove "tcp:$Port" 2>$null | Out-Null
    exit 1
}

$elapsedSec = $sw.Elapsed.TotalSeconds
$mbPerSec = [math]::Round(($totalBytesSent / 1MB) / $elapsedSec, 3)
$mbitPerSec = [math]::Round($mbPerSec * 8, 2)

# ---- 6. Don dep ----
Write-Section "6. Don dep"
& adb forward --remove "tcp:$Port" 2>$null | Out-Null
if ($serverProc -and -not $serverProc.HasExited) {
    Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
}
& adb shell "pkill -f 'nc -l -p $Port'" 2>$null | Out-Null

# ---- 7. Ket qua ----
Write-Section "7. KET QUA - ADB forward (T1)"
$totalMB = [math]::Round($totalBytesSent / 1MB, 1)
Write-Host ("Tong du lieu gui        : {0} MB" -f $totalMB)
Write-Host ("Thoi gian do             : {0:N2} s" -f $elapsedSec)
Write-Host ("Throughput trung binh    : {0} MB/s ({1} Mbit/s)" -f $mbPerSec, $mbitPerSec) -ForegroundColor Green
Write-Host ""
Write-Host "So sanh voi nguong yeu cau video profile:"
$reqPHigh = 8.6
$reqPUltra = 10.4
if ($mbPerSec -ge $reqPUltra) {
    Write-Host ("  >= P-Ultra ({0} MB/s): DAT" -f $reqPUltra) -ForegroundColor Green
} elseif ($mbPerSec -ge $reqPHigh) {
    Write-Host ("  >= P-High ({0} MB/s): DAT | < P-Ultra ({1} MB/s): CHUA DAT" -f $reqPHigh, $reqPUltra) -ForegroundColor Yellow
} else {
    Write-Host ("  < P-High ({0} MB/s): KHONG DAT" -f $reqPHigh) -ForegroundColor Red
}

# Xuat ket qua duoi dang object de script khac (RESULTS.md generator) co the tai su dung
[PSCustomObject]@{
    Transport      = "ADB-forward"
    Port           = $Port
    TotalMB        = $totalMB
    ElapsedSec     = [math]::Round($elapsedSec, 2)
    MBytesPerSec   = $mbPerSec
    MbitsPerSec    = $mbitPerSec
    MeetsPHigh     = ($mbPerSec -ge $reqPHigh)
    MeetsPUltra    = ($mbPerSec -ge $reqPUltra)
    Timestamp      = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
} | Format-List
