using System.Text.Json.Serialization;

namespace V3Netbill.Agent.Core;

/// <summary>Balasan <c>client:login_result</c> dari server (namespace <c>/session</c>).</summary>
/// <remarks>
/// Nama properti C# menyesuaikan PEMBACA di <c>Worker.OnClientLoginResult</c>
/// (<c>Sukses</c>/<c>Alasan</c>/<c>SessionId</c>); nama JSON ("wire") mengikuti
/// emit backend di <c>session.gateway.ts</c> (<c>success</c>/<c>message</c>/<c>sessionId</c>).
/// Dipetakan eksplisit via <see cref="JsonPropertyNameAttribute"/> agar kebal
/// terhadap apapun JsonNamingPolicy milik serializer SocketIOClient.
/// </remarks>
public sealed class ClientLoginResultPayload
{
    /// <summary>Backend: <c>success</c> — true bila kode+password valid.</summary>
    [JsonPropertyName("success")]
    public bool Sukses { get; set; }

    /// <summary>Backend: <c>message</c> — alasan bila gagal (mis. "PC not registered").</summary>
    [JsonPropertyName("message")]
    public string? Alasan { get; set; }

    /// <summary>Backend: <c>sessionId</c> — hanya terisi saat berhasil.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
}

/// <summary>Event <c>session:start</c> — sesi voucher/member dimulai.</summary>
/// <remarks>
/// C# membaca <c>SessionId</c> dan <c>DurasiDetik</c> (lihat
/// <c>Worker.OnSessionStarted</c>); backend mengirim wire
/// <c>sessionId</c>/<c>durasiDetikTersedia</c> (lihat <c>emitSessionStart</c> di
/// <c>session.gateway.ts</c>). Total detik sesi = <c>durasiDetikTersedia</c>.
/// </remarks>
public sealed class SessionStartPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("durasiDetikTersedia")]
    public int DurasiDetik { get; set; }

    /// <summary>Identitas akun dari backend (kodeUnik/nama/tipe) — untuk ditampilkan di window mini.</summary>
    [JsonPropertyName("account")]
    public AccountInfoPayload? Account { get; set; }
}

/// <summary>Identitas akun yang login (voucher/member).</summary>
public sealed class AccountInfoPayload
{
    [JsonPropertyName("kodeUnik")]
    public string? KodeUnik { get; set; }

    [JsonPropertyName("nama")]
    public string? Nama { get; set; }

    [JsonPropertyName("tipe")]
    public string? Tipe { get; set; }
}

/// <summary>Event <c>session:tick</c> — update countdown tiap interval.</summary>
/// <remarks>Backend wire <c>sisaDetik</c> (lihat <c>emitSessionTick</c>).</remarks>
public sealed class SessionTickPayload
{
    [JsonPropertyName("sisaDetik")]
    public int SisaDetik { get; set; }
}

/// <summary>Event <c>session:stop</c> — sesi berhenti (manual/habis/disconnect_timeout).</summary>
/// <remarks>Backend wire <c>alasan</c> (lihat <c>emitSessionStop</c>).</remarks>
public sealed class SessionStopPayload
{
    [JsonPropertyName("alasan")]
    public string Alasan { get; set; } = string.Empty;
}

/// <summary>Event <c>admin:lock</c> — dashboard memerintahkan kunci layar PC ini sekarang juga.</summary>
public sealed class AdminLockPayload
{
    [JsonPropertyName("pcId")]
    public string PcId { get; set; } = string.Empty;
}

/// <summary>Event <c>admin:shutdown</c> — dashboard memerintahkan matikan PC (setelah sesi dihentikan).</summary>
public sealed class AdminShutdownPayload
{
    [JsonPropertyName("pcId")]
    public string PcId { get; set; } = string.Empty;
}

/// <summary>Event <c>agent:otp_config</c> — server mendorong konfigurasi OTP Telegram
/// setelah admin menyimpannya di halaman Pengaturan. Nilai kosong = fitur OTP dimatikan.</summary>
public sealed class OtpConfigPayload
{
    [JsonPropertyName("botToken")]
    public string? BotToken { get; set; }

    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }
}

/// <summary>
/// Event <c>agent:nextcloud_config</c> - server mendorong konfigurasi Nextcloud
/// (tujuan upload log agent + overlay) setelah admin menyimpannya di halaman
/// Pengaturan.
/// </summary>
/// <remarks>
/// Disimpan ke registry PC, bukan ke appsettings.json atau source: repo ini
/// publik, jadi password di sana akan terbaca seluruh dunia. Pola yang sama
/// seperti <see cref="OtpConfigPayload"/>.
/// </remarks>
public sealed class NextcloudConfigPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("pass")]
    public string? Pass { get; set; }

    [JsonPropertyName("folder")]
    public string? Folder { get; set; }

    /// <summary>
    /// Nama PC yang dibaca manusia, mis. "PC001".
    /// </summary>
    /// <remarks>
    /// Wajib ikut dikirim karena <c>pcId</c> berupa UUID dan sama sekali tidak
    /// bisa dipetakan orang ke PC tertentu. Tanpa nama ini, berkas log di
    /// Nextcloud bernama UUID dan kasir tidak tahu itu milik PC yang mana.
    /// </remarks>
    [JsonPropertyName("nama")]
    public string? Nama { get; set; }
}

/// <summary>
/// Hash PIN bypass/maintenance yang didorong server lewat <c>agent:bypass_config</c>.
/// </summary>
/// <remarks>
/// Yang dikirim hanya hash-nya, bukan PIN aslinya, supaya client bisa
/// memverifikasi sendiri tanpa server. Kosong berarti PIN belum diset di
/// Pengaturan dan client memakai PIN emergency bawaan.
/// </remarks>
public sealed class BypassConfigPayload
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

/// <summary>
/// Konfigurasi daya PC yang didorong server lewat <c>agent:power_config</c>.
/// </summary>
/// <remarks>
/// <c>preventSleep = true</c> berarti PC tidak boleh tidur, layar tidak boleh
/// mati sendiri, dan screensaver tidak boleh muncul — ketiganya membuat
/// pelanggan berhenti billing tanpa tahu kenapa. <c>false</c> berarti
/// dikembalikan ke pengaturan Windows normal.
/// </remarks>
public sealed class PowerConfigPayload
{
    [JsonPropertyName("preventSleep")]
    public bool PreventSleep { get; set; }
}

/// <summary>
/// Event <c>agent:auto_update_config</c> — server mendorong status sakelar
/// pembaruan otomatis setelah admin mengubahnya di halaman Pengaturan.
/// </summary>
/// <remarks>
/// Ini cuma informasi "sakelarnya nyala/tidak". Keputusan install tetap ada di
/// PC: yang tahu versinya sendiri, yang tahu sedang ada sesi berjalan, dan yang
/// tahu teknisi sedang maintenance. Server tidak pernah memerintahkan install.
/// </remarks>
public sealed class AutoUpdateConfigPayload
{
    [JsonPropertyName("aktif")]
    public bool Aktif { get; set; }
}

/// <summary>
/// Laporan progres pembaruan otomatis dari agent ke server.
/// </summary>
/// <remarks>
/// ⚠️ Server memakai angka ini untuk mengisi badge di Halaman PC. Jadi
/// <see cref="Persen"/> WAJIB dikunci 0..100 di agent — server tidak menyaring,
/// dan angka 130% di layar operator terlihat seperti file rusak.
/// </remarks>
public sealed class AutoUpdateProgressPayload
{
    [JsonPropertyName("pcId")]
    public string PcId { get; set; } = "";

    [JsonPropertyName("agentToken")]
    public string AgentToken { get; set; } = "";

    [JsonPropertyName("pesan")]
    public string Pesan { get; set; } = "";

    [JsonPropertyName("persen")]
    public int Persen { get; set; }
}

/// <summary>Balasan ack <c>client:create_password</c> dari server.</summary>
/// <remarks>
/// Mengikuti <see cref="ClientLoginResultPayload"/>: nama C# untuk pembaca di
/// Worker, nama wire mengikuti emit backend (<c>success</c>/<c>message</c>).
/// </remarks>
public sealed class CreatePasswordResultPayload
{
    [JsonPropertyName("success")]
    public bool Sukses { get; set; }

    [JsonPropertyName("message")]
    public string? Alasan { get; set; }
}
