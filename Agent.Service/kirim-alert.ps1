# Kirim peringatan ke Telegram bila agent dihentikan paksa atau di-uninstall.
#
# Dipanggil dari watchdog.cmd, yang jalan sebagai scheduled task tiap menit
# dan TIDAK bergantung pada agent — justru itu satu-satunya alasan file ini
# ada. Kalau service-nya yang mengirim peringatan, peringatan itu tidak akan
# pernah sampai untuk kejadian yang justru diobserver.
#
# Token bot hanya dibaca dari registry PC dan langsung dipakai ke api.telegram.org.
# Nilainya tidak pernah ditulis ke berkas mana pun dan tidak pernah dikirim
# ke server v3netbill.
param([string]$Jenis = "")

$ErrorActionPreference = "SilentlyContinue"

$root = Join-Path $env:ProgramData "v3NetbillAgent"
$stamp = Join-Path $root "alert-terkirim.txt"
# Anti-spam: kalau service mati terus-menerus (mis. folder install dihapus
# tapi service masih terdaftar)- tapi tanpa henti, bombardir Telegram tidak berguna.
$jedaJam = 6

if (-not $Jenis) { exit 0 }

New-Item -ItemType Directory -Force -Path $root | Out-Null

# Jangan kirim lagi dalam $jedaJam
if (Test-Path $stamp) {
    $terakhir = (Get-Item $stamp).LastWriteTime
    if (((Get-Date) - $terakhir).TotalHours -lt $jedaJam) { exit 0 }
}

$key = Get-ItemProperty -Path "HKLM:\SOFTWARE\v3Netbill\Agent" -ErrorAction SilentlyContinue
$bot = $key.OtpBotToken
$chat = $key.OtpChatId
if (-not $bot -or -not $chat) { exit 0 }

$namaPc = $env:COMPUTERNAME
$waktu  = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

$judul = switch ($Jenis) {
    "stopped"  { "AGENT DIHENTIKAN PAKSA" }
    "deleted"  { "AGENT DI-UNINSTALL" }
    "gagal"    { "AGENT GAGAL DIJALANKAN" }
    default    { "PERINGATAN AGENT" }
}

$isi = @(
    "v3Netbill Agent",
    "",
    $judul,
    "PC      : $namaPc",
    "Waktu   : $waktu",
    "Status  : watchdog mendeteksi perubahan di luar aplikasi.",
    "",
    "Service sudah dihidupkan ulang otomatis bila masih terdaftar.",
    "Kalau ini tidak kamu lakukan, cek orang yang ada di depan PC ini.",
)

$url = "https://api.telegram.org/bot$bot/sendMessage"
try {
    $resp = Invoke-RestMethod -Method Post -Uri $url `
        -Body @{ chat_id = $chat; text = ($isi -join "`n") } -TimeoutSec 20
    if ($resp.ok) {
        Set-Content -Path $stamp -Value $waktu -Encoding utf8
    }
} catch {
    # Gagal kirim tidak boleh menggagalkan watchdog: scheduler akan mencoba
    # lagi pada menit berikutnya, dan stamp tidak disentuh kalau gagal.
}
exit 0