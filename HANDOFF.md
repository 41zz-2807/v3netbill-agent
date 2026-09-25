# HANDOFF — v3Netbill Agent (lanjut besok)

> Konteks pekerjaan: opencode, working dir `~/docker` di VPS billing (`/home/warnet/docker`).
> Repo agent: `/home/warnet/docker/v3netbill/v3NetbillAgent` (git, push ke main auto-CI).
> Project backend+fronend: `/home/warnet/docker/v3netbill` (backend NestJS, frontend React/Vite).

## 1. Status saat ini (Tgl 24-25 Sep 2026)

- **PC warnet (DESKTOP-CECVVJB, user ttrial)**: SUDAH ter-install build lama x86 yang bercup pada overlay-nya TIDAK pernah jalan (bug XAML `StartupUri`), lalu user meng-upgrade ke build **#18 (43a313a)**.
- **Overlay SEKARANG jalan & login tampil** (crash XAML sudah diperbaiki di #18). Wallpaper + dashboard menunggu fix.
- **Login voucher/member belum bisa**: `login_request` tidak pernah sampai ke backend → kecurigaan: named-pipe overlay↔service tidak tersambung (ACL pipe mungkin gagal di #18, dan overlay diam-diam menelan kegagalan pipe → "tidak ada respon").
  - Fix hardening sudah dibuat di build **#19 (f2391cf, run 36036945833, CI hijau)**: ACL pipe di-`try/catch` (server tetap menunggu koneksi walau ACL gagal) + overlay menunjukkan pesan error jika pipe belum tersambung.
- **Belum ter-install**: build #19 (f2391cf) — punya **Emergency Stop PIN default `123456`** (tombol STOP AGENT saat pip/backend mati).
- **Ter-install di PC**: build #18 (43a313a) = overlay jalan + login tampil + MUTLAK NOL escape pin darurat. Home khawatir terjebak, makanya #19 dibuat.

## 2. Nilai konfigurasi (kunci utama)

| Item | Nilai |
|---|---|
| Server URL | https://v3netbill.<domain> |
| PcId (UUID) | `3bebb9b6-dbb6-4443-8c9d-415c94f060f7` |
| AgentToken | `c8566146-e90d-4cfa-8fa3-71ff5e89fd7f` |
| Emergency PIN (overlay) | `123456` |
| Registry (harus begini) | HKLM\SOFTWARE\v3Netbill\Agent AND WOW6432Node — ServerUrl, PcId, AgentToken |
| GetConfig precedence | registry → appsettings.json → default |
| Admin dashboard | admin / admin123 |

## 3. TEMPAT SIMPAN SEMUA CHAT DAN RUJUKAN

- Chat/tugas ini BELUM permanen di storage; separuh teknis ada di `HANDOFF.md` ini + repo git.
- Untuk melanjutkan: buka opencode di `/home/warnet/docker`, minta resume dari `HANDOFF.md`.

## 4. Build / commit terakhir (yang HARUS dipakai PC berikutnya)

- Commit: **f2391cf** — "feat(overlay): emergency stop PIN default 123456 + flag maintenance + hardening ACL pipe + feedback login"
- CI run: **36036945833** (hijau/success) — artifact `v3NetbillAgentSetup` (sekitar 61 MB).
- Link: https://github.com/41zz-2807/v3netbill-agent/actions/runs/36036945833
- Build lokal (validasi): `docker run --rm -v <repo>:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build v3NetbillAgent.sln -p:EnableWindowsTargeting=true`

## 5. Backend + Frontend perubahan yang SUDAH diterapkan (server, live via auto-reload)

- `@Public()` pada `GET /api/settings/wallpaper` → overlay bisa ambil wallpaper tanpa JWT (terverifikasi HTTP 200).
- Dashboard WS: `DashboardPage.tsx` pindah ke `window.location.origin` (bukan `:3000`) + proxy `/socket.io` (ws:true) di `vite.config.ts` — fix "Menghubungkan..." permanent.
- Logging `login_request ... → SUKSES/message` di `session.gateway.ts` (untuk debug login ke depan).

## 6. Masalah yang BELUM selesai

1. [Kritis] Kenapa pipe overlay↔service tidak tersambung pada #18? (login_request tak pernah sampai backend; tidak ada log error). Hipotesis utama: `SetNamedSecurityInfoW` gagal di #18 → server pipe tak pernah `WaitForConnection`. #19 me-hardening tapi **belum teruji runtime di PC**.
2. Wallpaper overlay: kode sudah ada di #19 (download `/api/settings/wallpaper`, set sebagai background Border). Belum teruji karena #19 belum di-install.
3. Dashboard "Menghubungkan...": fix frontend/vite sudah deployed (server) — perlu dicoba lagi dari browser (hard refresh).
4. VPS nginx? TIDAK ada nginx di host — situs lewat **Cloudflare → VPS :5173 langsung** (Vite dev server). `allowedHosts` sudah berisi domain.
5. CI auto-upload (`V3NETBILL_BACKEND_URL`) masih salah (tidak dipakai).
6. Backend `test/app.e2e-spec.ts` error `supertest/types` (pre-existing, bukan gangguan).

## 7. Instalasi untuk PC/Windows BARU (besok)

Langkah aman & pasti:
1. Buka Add/Remove Programs → uninstall "v3Netbill" versi lama (jika ada).
2. Download MSI dari run **#36036945833** (bukan run lain — nama artefak sama antarmuka antar run).
3. Jalankan MSI → dialog isi (jika menu prefill kosong):
   - Server Url: `https://v3netbill.<domain>`
   - PC ID: `3bebb9b6-dbb6-4443-8c9d-415c94f060f7`
   - Agent Token: `c8566146-e90d-4cfa-8fa3-71ff5e89fd7f`
4. Verifikasi `C:\Program Files\v3NetbillAgent` (64-bit) ada (bukan (x86)).
5. Reboot. Overlay fullscreen login akan muncul (lewat shortcut logon + watchdog ke sesi interaktif).
6. Coba login voucher `321110` + passwordnya (lihat halaman voucher/member akun; jika gagal cek pesan error baru di layar).

## 8. Diagnostics (bila PC error)

- `diagnosa.bat` di repo agent → jalankan di PC → tanpa arti SAH → tempel output (berisi service path, tasklist, registry, event log 1000-1026). Artifact `diagnosa` dari workflow `diagnosa.yml` (dispatch), lalu user download as raw text.
- Cek backend: `docker compose logs --since 30m v3netbill-backend | grep login_request`
- Cek DB: `docker exec postgres-15 psql -U billing_user -d v3netbill -c 'select "namaPc", status, "lastHeartbeatAt", now()-"lastHeartbeatAt" from "Pc"'`

## 9. Pemakaian Emergency STOP (build #19+)

- Tekan **Ctrl+Alt+Shift+F12** → masukkan PIN `123456` → muncul tombol **STOP AGENT (DARURAT)**.
- Tekan tombol → overlay mati, flag `C:\Users\Public\Documents\v3netbill-agent-stop.flag` ditulis, watchdog tidak bangkitkan lagi, service di-stop.
- Untuk NORMAL kembali: hapus file flag lalu nyalakan service:
  ```
  del "C:\Users\Public\Documents\v3netbill-agent-stop.flag"
  sc config v3netbillAgent start= auto
  sc start v3netbillAgent
  ```
- Keluar terpaksa tanpa PIN99 di build lama (#18): Ctrl+Shift+Esc → Alt+F,N → `cmd /c sc stop v3netbillAgent & sc config v3netbillAgent start= disabled` (centang admin) → kill `Agent.Overlay.exe` via Task Manager.

## 10. Catatan teknis yang berguna

- Overlay WPF single-instance via `Global\V3NetbillAgentOverlay` mutex di `App.OnStartup`.
- Watchdog: `Agent.Service/Worker.cs` `WatchdogCallback` — restart overlay bila Locked & tidak berjalan; skip jika flag maintenance ada.
- Pipe server: `NamedPipeServerStream` Message mode; ACL Everyone via `SecureNamedPipe.cs` (`SetNamedSecurityInfoW` + SDDL `D:(A;;GA;;;WD)`).
- Launcher sesi interaktif: `InteractiveProcess.cs` (CreateProcessAsUser + WTSQueryUserToken) — overlay dari service tampil di desktop user.
- Reg keys installer: ditulis saat MSI jalankan → `HKLM\SOFTWARE\v3Netbill\Agent`.
- File penting repo: `Installer/Product.wxs`, `Agent.Service/Worker.cs`, `Agent.Overlay/MainWindow.xaml(.cs)`, `Agent.Overlay/App.xaml(.cs)`, `Agent.Overlay/PipeClient.cs`, `.github/workflows/build-agent.yml`, `diagnosa.bat`.