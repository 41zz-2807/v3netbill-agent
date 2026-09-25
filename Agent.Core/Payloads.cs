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
