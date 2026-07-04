# RESULTS - M0.2 bench-transport

**Ngay do**: 2026-07-03
**Thiet bi**: HONOR ROD2-W09 (MagicPad), serial `AL9SBB4622000114`, ket noi qua cap USB co san
**PC**: Windows, PowerShell 5.1 (khong chay Administrator trong phien nay)
**Gate**: R11 (USB speed thuc te) - PLAN-v2 §8, CONTEXT-BRIEF §5/§8

---

## 1. ADB forward (T1) - DA DO THUC TE

| Lan do | Tong du lieu | Thoi gian | Throughput |
|--------|-------------|-----------|-----------|
| Run 1 | 471 MB | 15.01 s | **31.378 MB/s** (251.02 Mbit/s) |
| Run 2 | 640 MB | 20.02 s | **31.974 MB/s** (255.79 Mbit/s) |

**Phuong phap**: `adb forward tcp:29600 tcp:29600` (tunnel USB that qua adb, khong phai loopback
gia lap) -> PC mo `System.Net.Sockets.TcpClient` toi `127.0.0.1:29600`, ghi lien tuc buffer
256KB ngau nhien trong 15-20s -> tablet chay `nc -l -p 29600 > /dev/null` (toybox nc that,
`/system/bin/nc`, xac nhan co san tren thiet bi qua `adb shell which nc`) nhan va xa du lieu.
Day la do throughput socket TCP THAT qua tunnel USB, khong phai proxy/uoc tinh.

**So sanh nguong yeu cau**:
- P-High (8.6 MB/s): **DAT** - vuot ~3.6x
- P-Ultra (10.4 MB/s): **DAT** - vuot ~3.0x

**Do tin cay**: 2 lan do doc lap cho ket qua nhat quan (31.4-32.0 MB/s, chenh lech <2%).
31 MB/s la con so hop ly cho tunnel adb qua USB 2.0 High-Speed (danh nghia 480 Mbit/s =
60 MB/s ly thuyet, thuc te dat ~50-60% sau overhead giao thuc adb + TCP/USB framing la binh
thuong).

---

## 2. USB NCM tethering (T2) - CHUA DO DUOC THROUGHPUT (chan boi quyen Admin)

### 2.1 Nhung gi DA lam duoc (that, khong gia dinh)

| Buoc | Ket qua |
|------|---------|
| `adb shell svc usb setFunctions ncm` | **CHAY DUOC**, khong can root, khong Permission denied |
| ADB tu ket noi lai sau khi doi USB function | Co (`transport_id` doi, serial giu nguyen) |
| Windows nhan adapter moi | Co - "Ethernet 5 / UsbNcm Host Device", driver native, Status Up, link speed bao 426 Mbps (danh nghia USB NCM class, KHONG phai throughput do duoc) |
| Tablet co interface `ncm0` | Co - IP tinh `192.168.66.211/24` (xem `adb shell ip -4 addr show ncm0`) |
| Windows IPv4 tren adapter moi | Chi co APIPA `169.254.111.126/16` - KHAC subnet voi tablet, khong the giao tiep IPv4 |
| ARP/NDP neighbor resolve | Co - Windows resolve dung MAC tablet (`e2:6d:16:fc:22:4b`) qua `Get-NetNeighbor`, chung to lien ket L2 (day USB) hoat dong |

### 2.2 Diem chan thuc te (log loi that)

1. **Gan static IP thu cong bi tu choi**:
   ```
   New-NetIPAddress -InterfaceAlias "Ethernet 5" -IPAddress 192.168.66.1 -PrefixLength 24
   New-NetIPAddress : Access is denied.
   ```
   Nguyen nhan: PowerShell session hien tai KHONG chay voi quyen Administrator
   (xac nhan bang `[Security.Principal.WindowsPrincipal]::...IsInRole(Administrator)` = `False`).

2. **Ping + TCP connect qua IPv6 link-local (khong can DHCP/static IP) deu TIMEOUT**:
   - `ping -6 fe80::e06d:16ff:fefc:224b%79` -> "Request timed out" x4 (100% loss).
   - `TcpClient.Connect([fe80::e06d:16ff:fefc:224b%79]:29700)` -> "A connection attempt failed
     because the connected party did not properly respond after a period of time" (timeout).
   - Trong khi do neighbor cache van resolve dung MAC (L2 song), nen nghi ngo o tang cao hon:
     `Get-NetConnectionProfile` cho adapter nay bao `NetworkCategory: Public`,
     `IPv4Connectivity/IPv6Connectivity: NoTraffic`. Windows mac dinh xep NIC moi/khong ro vao
     "Public" va Windows Firewall chan phan lon inbound tren profile nay; doi sang "Private"
     can `Set-NetConnectionProfile` - CUNG can quyen Administrator.
   - Ly do sau xa: `svc usb setFunctions ncm` la duong tat thap (bypass Tethering framework
     cua Android), nen khong co dnsmasq/DHCP server tu dong nhu khi bat "USB tethering" qua
     Settings UI - dieu nay lam Windows khong nhan duoc IP DHCP hop le, dan den chuoi van de
     tren.

3. **Ket luan cho phien nay**: KHONG do duoc so MB/s thuc te qua NCM vi thieu quyen
   Administrator tren may Windows dang dung. Day la **rang buoc quyen cua moi truong**, khong
   phai gioi han ky thuat cua ban than USB NCM - ve nguyen tac NCM van co tiem nang throughput
   cao hon ADB (gan voi bang thong USB vat ly hon, it overhead giao thuc adb) va nen duoc do
   lai khi co quyen Administrator.

**Script da chuan bi san** (`bench-ncm.ps1`): tu kiem tra quyen admin, neu co se tu dong bat NCM,
doi network category sang Private, gan static IP cung subnet voi tablet, va do throughput qua
TCP that (cung phuong phap voi bench-adb.ps1). Neu chay khong co quyen admin, script se dung
lai som va in ra chinh xac cac loi da liet ke o tren (khong chay lung tung/silent fail).

---

## 3. Khuyen nghi (Gate R11 / Task M0.2 DoD)

| Tieu chi | Ket qua |
|----------|---------|
| ADB forward co du bang thong cho P-High (8.6 MB/s)? | **CO** (31+ MB/s do that, vuot 3.6x) |
| ADB forward co du bang thong cho P-Ultra (10.4 MB/s)? | **CO** (31+ MB/s do that, vuot 3.0x) |
| NCM co bang thong cao hon ADB khong? | **CHUA XAC DINH** - can chay lai `bench-ncm.ps1` voi quyen Administrator |

**Khuyen nghi kien truc**: **T1 (ADB forward) DU DUNG** cho ca P-High va P-Ultra dua tren so
lieu do that hien tai (31.4-32.0 MB/s, biên an toan ~3x so voi nguong cao nhat 10.4 MB/s).
Setting "Transport: Auto (NCM->ADB)" trong Settings Catalog (CONTEXT-BRIEF §4) van nen giu NCM
la lua chon uu tien khi co the, vi:
  - NCM it phu thuoc adb server/driver hon, on dinh hon cho phien dai (giam rui ro adb server
    restart/reconnect anh huong stream).
  - NCM co tiem nang bang thong cao hon (gan bang thong USB vat ly that, USB 2.0 High-Speed ly
    thuyet 60 MB/s vs ADB tunnel hien do 31 MB/s ~52% hieu suat ly thuyet).
  - Nhung **ADB forward la fallback AN TOAN va DA CHUNG MINH DU DUNG** - khong phai lam gate
    chan M1. Co the trien khai M1 voi ADB truoc, bo sung NCM benchmark + support sau khi co moi
    truong voi quyen Administrator (hoac chay tren may build/CI co quyen day du).

**Hanh dong tiep theo (ngoai pham vi M0.2 hien tai)**:
- Chay `bench-ncm.ps1` tren PowerShell "Run as Administrator" de co so lieu NCM that.
- Neu NCM throughput > ADB throughput ro ret (vd >1.5x), can nhac uu tien NCM lam T1 mac dinh,
  ADB lam fallback (dao nguoc thu tu Auto trong Settings Catalog).
- Neu NCM khong on dinh (vd Android tat NCM khi khoa man hinh, hoac mat ket noi khi App
  background - lien quan Risk R10), giu ADB forward la transport mac dinh du NCM co bang thong
  cao hon tren ly thuyet.

---

## 4. Files tao trong M0.2

| File | Muc dich |
|------|---------|
| `tools/bench-transport/android-echo-server.sh` | Server toybox `nc` chay tren tablet qua `adb shell`, nhan va xa du lieu vao `/dev/null`, tu thoat sau N giay (dung `timeout`) |
| `tools/bench-transport/bench-adb.ps1` | Script PowerShell do throughput qua ADB forward tunnel - DA CHAY THAT, 2 lan, ket qua 31.4-32.0 MB/s |
| `tools/bench-transport/bench-ncm.ps1` | Script do throughput qua USB NCM - da thu chay, dung som va bao loi ro rang do thieu quyen Administrator; san sang chay lai khi co quyen |
| `tools/bench-transport/RESULTS.md` | File nay - tong hop so lieu do that + khuyen nghi |

**Don dep sau khi do**: da xoa listener/process tam thoi tren tablet (`pkill -f nc`), remove
`adb forward`, va tra USB function ve `adb` binh thuong (`adb shell svc usb setFunctions adb`).
Khong con file rac nao tren `/data/local/tmp/` ngoai `android-echo-server.sh` (giu lai co chu
dich, la tool ho tro cho lan do sau).

---

## 5. Cau hoi chua giai quyet (can nguoi/quyen tiep theo)

1. So lieu throughput NCM thuc te la bao nhieu? Can chay `bench-ncm.ps1` voi quyen
   Administrator tren Windows.
2. NCM co on dinh qua thoi gian dai (soak test) khong, hay bi Android tat khi man hinh khoa/app
   background (lien quan Risk R10, M4.5)? Chua kiem tra trong phien nay.
3. Neu du an chinh thuc dung NCM, co can tu dong hoa buoc "gan static IP + doi network
   category" luc first-run (can quyen Admin, co the can UAC elevation prompt trong app cai dat
   PC-host) khong? Day la UX/kien truc can quyet dinh o M1+.
