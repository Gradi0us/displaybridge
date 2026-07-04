<#
.SYNOPSIS
    M0.2 bench-transport - thu bat USB tethering NCM va do throughput qua network adapter that.

.DESCRIPTION
    Ket qua da xac nhan trong phien do thuc te (xem RESULTS.md):
      - `adb shell svc usb setFunctions ncm` CHAY DUOC (khong can root), thiet bi chuyen
        USB function sang NCM, adb debugging session bi ngat tam thoi roi tu ket noi lai
        (transport_id doi nhung serial giu nguyen).
      - Windows TU NHAN driver "UsbNcm Host Device" (native, khong can driver rieng) va
        len 1 network adapter moi (vi du "Ethernet 5", link speed bao cao 426 Mbps - day la
        toc do danh nghia cua USB NCM class, khong phai throughput thuc do duoc).
      - Tablet co interface `ncm0` voi static IP (vi du 192.168.66.211/24) nhung KHONG chay
        DHCP server (vi day la duong tat qua `svc usb`, khong di qua Tethering framework binh
        thuong cua Android nen khong co dnsmasq/iptables tu dong cau hinh).
      - Windows chi nhan duoc APIPA (169.254.x.x/16) do khong co DHCP server phia tablet ->
        KHAC subnet voi tablet -> khong the giao tiep IPv4 truc tiep.
      - Gan static IP thu cong cho adapter Windows (`New-NetIPAddress`) THAT BAI voi loi
        "Access is denied" vi PowerShell session KHONG chay voi quyen Administrator.
      - Thu ket noi qua dia chi IPv6 link-local (khong can DHCP/admin de tu cau hinh) - ARP/NDP
        neighbor cache tren Windows CO resolve dung MAC cua tablet (chung to L2 link song),
        nhung ping VA ket noi TCP truc tiep DEU timeout. Nguyen nhan nhieu kha nang: Windows
        xep adapter moi vao network category "Public/Unidentified" (mac dinh, doi quyen admin
        de doi sang Private) khien Windows Firewall chan traffic; hoac thieu route/cau hinh
        o tang OS can quyen admin de sua.

    KET LUAN: NCM throughput test KHONG hoan tat duoc trong phien nay do RANG BUOC QUYEN
    (PowerShell khong chay Administrator). Day la thong tin quan trong cho quyet dinh kien
    truc (xem RESULTS.md muc "NCM - han che quyen"), KHONG phai loi cua script.

    Script nay giu lai cac buoc da thu de: (a) tai hien nhanh khi co quyen Administrator,
    (b) lam tai lieu cho lan sau.

.NOTES
    CHAY VOI QUYEN ADMINISTRATOR de vuot qua han che da gap:
      1. Mo PowerShell "Run as Administrator".
      2. Chay lai script nay. Script se:
         - Bat NCM qua adb.
         - Phat hien adapter moi qua Get-NetAdapter (loc theo InterfaceDescription "UsbNcm").
         - Doi network category adapter do sang "Private" (Set-NetConnectionProfile - can admin).
         - Gan static IP cung subnet voi tablet (vi du 192.168.66.1/24 neu tablet la
           192.168.66.211/24 - script se tu doc IP tablet qua `adb shell ip -4 addr show ncm0`).
         - Mo TcpClient toi tablet qua IPv4 that (khong phai loopback/forward) va do MB/s
           bang cung phuong phap voi bench-adb.ps1.
#>

param(
    [int]$Port = 29700,
    [int]$DurationSec = 15,
    [int]$BufferSizeKB = 256
)

$ErrorActionPreference = "Continue"

function Write-Section($msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Section "0. Kiem tra quyen Administrator"
if (-not (Test-IsAdmin)) {
    Write-Host "KHONG chay voi quyen Administrator." -ForegroundColor Red
    Write-Host ""
    Write-Host "Trong phien do M0.2 (2026-07-03), day chinh la diem chan thuc te:" -ForegroundColor Yellow
    Write-Host "  - New-NetIPAddress bao 'Access is denied' khi gan static IP cho adapter NCM."
    Write-Host "  - Khong the doi network category (Public -> Private) de Windows Firewall cho phep traffic."
    Write-Host "  - Ket qua: ping + TCP connect qua IPv6 link-local toi tablet deu TIMEOUT (mac du ARP/NDP"
    Write-Host "    da resolve dung MAC cua tablet, tuc L2 link song, chi bi chan o tang cao hon)."
    Write-Host ""
    Write-Host "Chi tiet day du: xem RESULTS.md muc 'NCM - han che quyen'." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Hay chay lai script nay tu PowerShell 'Run as Administrator' de tiep tuc do that." -ForegroundColor Cyan
    exit 2
}

# ---- Tu day tro di CHI CHAY duoc khi da co quyen Administrator ----
# Bao ve bang try/finally: du chay xong hay loi giua chung, LUON tra tablet ve
# USB function 'adb', xoa static IP da gan, va kill nc listener (tranh de tablet
# ket o che do NCM lam hong workflow adb / de lai IP rac tren adapter Windows).
$assignedIfIndex = $null
$assignedHostIp  = $null
try {

Write-Section "1. Bat USB NCM function tren tablet"
$env:MSYS_NO_PATHCONV = "1"
& adb shell svc usb setFunctions ncm | Out-Host
Start-Sleep -Seconds 3
& adb devices -l | Out-Host

Write-Section "2. Doc IP cua tablet tren interface ncm0"
$tabletIpLine = & adb shell "ip -4 addr show ncm0" 2>&1
Write-Host $tabletIpLine
$tabletIp = $null
if ($tabletIpLine -match "inet (\d+\.\d+\.\d+\.\d+)/(\d+)") {
    $tabletIp = $Matches[1]
    $prefixLen = [int]$Matches[2]
    Write-Host "Tablet IP: $tabletIp/$prefixLen"
} else {
    Write-Error "Khong doc duoc IP cua tablet tren ncm0. Dung lai, xem log ip addr o tren."
    exit 1
}

Write-Section "3. Tim network adapter NCM tren Windows"
$adapter = Get-NetAdapter | Where-Object { $_.InterfaceDescription -match "UsbNcm" }
if (-not $adapter) {
    Write-Error "Khong tim thay adapter 'UsbNcm Host Device' tren Windows. Kiem tra Device Manager."
    exit 1
}
Write-Host "Tim thay adapter: $($adapter.Name) (ifIndex $($adapter.InterfaceIndex))"

Write-Section "4. Doi network category sang Private (can admin) + gan static IP cung subnet"
Set-NetConnectionProfile -InterfaceIndex $adapter.InterfaceIndex -NetworkCategory Private -ErrorAction SilentlyContinue

# Chon IP host khac tablet trong cung subnet, vi du .1 neu tablet dung .211
$ipParts = $tabletIp.Split(".")
$hostIpLastOctet = if ($ipParts[3] -eq "1") { "2" } else { "1" }
$hostIp = "$($ipParts[0]).$($ipParts[1]).$($ipParts[2]).$hostIpLastOctet"

Remove-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -IPAddress $hostIp -PrefixLength $prefixLen -ErrorAction Stop | Out-Null
$assignedIfIndex = $adapter.InterfaceIndex
$assignedHostIp  = $hostIp
Write-Host "Gan IP Windows: $hostIp/$prefixLen"

Write-Section "5. Ping thu de kiem tra ket noi L3"
Test-Connection -ComputerName $tabletIp -Count 4

Write-Section "6. Khoi dong nc listener tren tablet + do throughput qua IP that"
$remoteScript = "/data/local/tmp/android-echo-server.sh"
$serverDuration = $DurationSec + 10
Start-Process -FilePath "adb" -ArgumentList @("shell", "sh", $remoteScript, "$Port", "$serverDuration") -NoNewWindow
Start-Sleep -Seconds 2

$bufferSize = $BufferSizeKB * 1024
$buffer = New-Object byte[] $bufferSize
(New-Object Random).NextBytes($buffer)
$totalBytesSent = 0L
$sw = [System.Diagnostics.Stopwatch]::new()

try {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.NoDelay = $true
    $client.Connect($tabletIp, $Port)
    $stream = $client.GetStream()
    $sw.Start()
    while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
        $stream.Write($buffer, 0, $buffer.Length)
        $totalBytesSent += $buffer.Length
    }
    $sw.Stop()
    $stream.Close()
    $client.Close()
}
catch {
    Write-Error "Loi khi gui du lieu qua NCM: $_"
    exit 1
}

$elapsedSec = $sw.Elapsed.TotalSeconds
$mbPerSec = [math]::Round(($totalBytesSent / 1MB) / $elapsedSec, 3)

Write-Section "7. KET QUA - USB NCM (T2)"
Write-Host ("Throughput trung binh qua NCM: {0} MB/s" -f $mbPerSec) -ForegroundColor Green

[PSCustomObject]@{
    Transport    = "USB-NCM"
    HostIp       = $hostIp
    TabletIp     = $tabletIp
    TotalMB      = [math]::Round($totalBytesSent / 1MB, 1)
    ElapsedSec   = [math]::Round($elapsedSec, 2)
    MBytesPerSec = $mbPerSec
    Timestamp    = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
} | Format-List

}
finally {
    Write-Section "8. Don dep (LUON chay du thanh cong hay loi)"
    # Kill nc listener tren tablet (neu con)
    & adb shell "pkill -f 'nc -l -p $Port'" 2>$null | Out-Null
    # Xoa static IP da gan cho adapter Windows (neu da gan)
    if ($assignedIfIndex -and $assignedHostIp) {
        Remove-NetIPAddress -InterfaceIndex $assignedIfIndex -IPAddress $assignedHostIp -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "Da xoa static IP $assignedHostIp khoi adapter (ifIndex $assignedIfIndex)."
    }
    # Tra USB function ve 'adb' de khong ket tablet o che do NCM
    & adb shell svc usb setFunctions adb 2>$null | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Da tra USB function ve 'adb'." -ForegroundColor Green
}
