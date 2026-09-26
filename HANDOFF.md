# HANDOFF — v3Netbill Agent

> Dokumen serah-terima untuk sesi berikutnya. **Versi: 1.0.9.0** (commit `86a4b87`).
>
> Repo agent: `/home/warnet/docker/v3netbill/v3NetbillAgent` (git, push ke `main` → CI otomatis).
> Project backend + frontend: `/home/warnet/docker/v3netbill` (NestJS + React/Vite).

---

## 1. Status Saat Ini (26 Sep 2026)

- **Overlay (WPF)**: layar login fullscreen + mini-panel sesi berjalan. Login voucher/member
  **sudah bisa** lewat named pipe ke service.
- **Reconnect sudah stabil** setelah 4 perbaikan berturut-turut (lihat §4).
- **Heartbeat** = self-diagnosing, alasannya dicatat di `agent.log`.
- **Uninstall dilindungi PIN admin** (verifikasi lewat `/api/settings/verify-pin`).
- Installer MSI dibangun otomatis oleh GitHub Actions (WiX v4).

---

## 2. Konfigurasi (Kunci Penting)

Config dibaca `GetConfig()` (`Agent.Service/Worker.cs:608`) dengan urutan:
**registry → appsettings.json → nilai default**.

Registry: `HKLM\Software\v3Netbill\Agent` (dan `WOW6432Node` untuk installer 32-bit):
- `ServerUrl` — **WAJIB skema lengkap**, mis. `https://v3netbill.<domain>` atau
  `http://192.168.1.65:3000`. Tanpa skema → `new Uri(...)` di `Agent.Core/ServerConnection.cs:74`
  gagal.
- `PcId` — UUID PC, dibuat di dashboard.
- `AgentToken` — token agent, dibuat di dashboard.

> **Nilainya tidak ditulis di dokumen ini** (rahasia). Ambil dari dashboard PC, atau dari
> registry PC tersebut.

---

## 3. Pasang di PC Windows Baru

1. Add/Remove Programs → uninstall versi lama bila ada (jalankan
   `Installer/uninstall-old-agent.bat` bila service masih jalan).
2. Download MSI dari GitHub Actions run terbaru (repo agent).
3. Jalankan MSI → isi dialog **Konfigurasi Server**:
   - **Server URL** — domain publik untuk PC di luar jaringan, atau
     `http://192.168.1.65:3000` (PC satu jaringan LAN dengan server). **Harus ada skema.**
     Nilai domain publik tidak disimpan di dokumen ini — ambil dari dashboard PC.
   - **PC ID** — UUID dari dashboard.
   - **Agent Token** — token dari dashboard.
4. Verifikasi `C:\Program Files\v3NetbillAgent` (64-bit, bukan `(x86)`).
5. Reboot. Overlay fullscreen akan muncul (shortcut logon + watchdog sesi interaktif).
6. Coba login voucher/member. Kalau gagal, cek pesan error di overlay + `agent.log`.

> Field dialog punya prefill dari registry bila upgrade, jadi tidak perlu diisi ulang.

---

## 4. Riwayat Fix Stability (1.0.6 → 1.0.9)

| Versi | Commit | Masalah yang Diperbaiki |
|---|---|---|
| 1.0.6.0 | `de5b3c3` | Supervisor reconnect di `Worker.cs` → agent kembali online setelah backend restart |
| 1.0.7.0 | `5b90c77` | Single reconnect authority: matikan `ReconnectionAttempts` library (anti flapping / dual reconnect) |
| 1.0.8.0 | `213a03d` | Heartbeat mati permanen setelah reconnect (`StartHeartbeat ??=` tidak pernah bikin timer baru) |
| 1.0.9.0 | `1f53432` | Heartbeat self-diagnosing (log + hilangkan `async void`) |
| — | `86a4b87` | Catat alasan disconnect + tick heartbeat ke `agent.log` (bukan cuma Event Log) |

> **Pelajaran**: jangan pernah menyalakan reconnect di dua tempat (library + supervisor). Pilih satu
> otoritas. Ini penyebab utama flapping versi 1.0.6.

---

## 5. Builds Older (Tidak Dipakai)

- Build #18 (`43a313a`) — overlay jalan, tapi **NOL** escape pin darurat.
- Build #19 (`f2391cf`) — ditambah emergency stop PIN default + hardening ACL pipe. **Sudah
  digantikan** oleh 1.0.6–1.0.9.

---

## 6. Komponen Penting

| Komponen | Path | Fungsi |
|---|---|---|
| Windows Service | `Agent.Service/Worker.cs` | Koneksi Socket.IO, heartbeat, reconnect supervisor, watchdog |
| Protokol | `Agent.Core/ServerConnection.cs` | Klien Socket.IO, builder koneksi |
| Overlay (WPF) | `Agent.Overlay/MainWindow.xaml(.cs)` | Layar login fullscreen + mini-panel sesi |
| Overlay proxy | `Agent.Overlay/SessionStateProxy.cs` | Jembatan state ke UI |
| Pipe client | `Agent.Overlay/PipeClient.cs` | Klien named pipe (overlay ↔ service) |
| Installer | `Installer/Product.wxs` | WiX v4: dialog Konfigurasi Server, registry, proteksi uninstall |
| Uninstall lama | `Installer/uninstall-old-agent.bat` | Cabut versi lama yang masih running |

---

## 7. Mekanisme Penting

- **Single-instance overlay**: mutex `Global\V3NetbillAgentOverlay` di `App.OnStartup`.
- **Named pipe**: `NamedPipeServerStream` mode Message; ACL Everyone via `SecureNamedPipe.cs`
  (`SetNamedSecurityInfoW` + SDDL `D:(A;;GA;;;WD)`).
- **Launcher sesi interaktif**: `InteractiveProcess.cs` (CreateProcessAsUser + WTSQueryUserToken) —
  overlay dari service tampil di desktop user.
- **Watchdog**: `Worker.cs#WatchdogCallback` — restart overlay bila Locked & tidak berjalan; skip
  bila ada flag maintenance.
- **Watchdog scheduled task**: dijalankan tiap menit supaya service sulit dimatikan manual.

---

## 8. Proteksi Uninstall

- Tombol uninstall (Start Menu & Add/Remove Programs) menjalankan
  `msiexec /x {ProductCode}` → custom action `UninstallGuardAction` memanggil
  `Agent.Overlay.exe --uninstall-guard` **sebelum** `RemoveFiles`.
- Guard memverifikasi PIN admin lewat `/api/settings/verify-pin` (butuh `pcId` + `agentToken`).
- **Hanya uninstall murni** — saat upgrade, guard dilewati.
- Flag maintenance: `C:\Users\Public\Documents\v3netbill-agent-stop.flag` — bila ada, watchdog
  tidak bangkitkan overlay.

### K emergencies & pulih normal
- **Emergency STOP** (overlay): `Ctrl+Alt+Shift+F12` → masukkan PIN → tombol **STOP AGENT
  (DARURAT)**. Overlay mati, flag maintenance ditulis, service di-stop.
- **Pulih normal**:
  ```bat
  del "C:\Users\Public\Documents\v3netbill-agent-stop.flag"
  sc config v3netbillAgent start= auto
  sc start v3netbillAgent
  ```
- **Keluar paksa tanpa PIN** (mis. service macet): Task Manager → kill `Agent.Overlay.exe`, lalu
  `cmd /c sc stop v3netbillAgent & sc config v3netbillAgent start= disabled` (admin).

> Nilai PIN default ada di source `Agent.Overlay/MainWindow.xaml.cs` (`EmergencyPin`) — **jangan**
> disalin ke dokumen teks. Ganti di production lewat `PATCH /api/settings/pin-uninstall`.

---

## 9. Diagnostik

- **`diagnosa.bat`** di repo agent → jalankan di PC (Run as admin) → hasilnya jadi artefak
  workflow `diagnosa.yml` (dispatch manual) → unduh sebagai raw text. Isinya: service path,
  tasklist, registry, Event Log 1000-1026.
- **`agent.log`**: heartbeat, alasan disconnect, dan tick tercatat di sini (fitur 1.0.9).
- Dari server:
  ```bash
  docker compose logs --since 30m v3netbill-backend | grep login_request
  docker exec postgres-15 psql -U billing_user -d v3netbill \
    -c 'select "namaPc", status, "ipClient", "lastHeartbeatAt", now()-"lastHeartbeatAt" from "Pc"'
  ```
- Log register agent di backend memuat IP yang terdeteksi:
  `PC <uuid> registered with socket <id> (ip x.x.x.x via cf-connecting-ip)`.
  Lihat `docs/DETEKSI-IP.md` untuk arti tiap sumber IP.

---

## 10. Build & CI

- **Otomatis**: push ke `main` → GitHub Actions (`build-agent.yml`) build MSI via WiX v4 →
  artefak bisa diunduh dari run.
- **Manual (Windows)**: .NET 8 SDK + WiX Toolset v4 (`dotnet tool install --global wix`) →
  `dotnet publish`, generate WiX fragments, `dotnet build` → `.msi`.
- **Validasi build di Linux** (tanpa WiX MSI): kompilasi saja via
  ```bash
  docker run --rm -v <repo>:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet build v3NetbillAgent.sln -p:EnableWindowsTargeting=true
  ```

---

## 11. Yang Perlu Diketahui Sesi Berikutnya

1. **Reconnect sudah beres** — versi 1.0.6–1.0.9 menutup semua bug yang ditemukan. Kalau ada
   masalah koneksi, cek dulu apakah PC sudah di build terbaru.
2. **Login voucher/member sudah bisa** — kalau masih gagal, cek `agent.log` (bukan hanya Event Log)
   dan `diagnosa.bat`.
3. **IP PC** — kolom `ipClient` untuk PC di luar jaringan (lewat Cloudflare) sudah akurat
   (`180.178.96.34` untuk PC001). Untuk PC **satu jaringan LAN** dengan server, IP akan tampil
   `172.18.0.1` (batasan `docker-proxy`) — bukan bug, belum ada perbaikannya. Detail +
   opsi: `../docs/DETEKSI-IP.md`.
4. **Jangan tulis nilai rahasia ke dokumen** (ServerUrl token, AgentToken, PIN, SMTP/Telegram).
   Ambil dari `.env` / registry / dashboard.
5. **Jangan commit** ke repo backend/frontend tanpa diminta user — keduanya **belum di-commit**
   sejak `cf999ff` (lihat `../CONVERSATION_LOG.md` item 17).

---

## 12. Referensi

| Dokumen | Isi |
|---|---|
| `README.md` (repo ini) | Arsitektur agent, protokol Socket.IO, named pipe, deploy lengkap |
| `../AGENTS.md` | Aturan kerja agent AI untuk project utama |
| `../docs/DEPLOYMENT.md` | Topologi & deploy server |
| `../docs/DETEKSI-IP.md` | Mekanisme deteksi IP PC |
