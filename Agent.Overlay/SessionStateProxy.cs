using System;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Local session state proxy — updated via named pipe messages from Service.
/// Implements INotifyPropertyChanged for WPF binding.
/// </summary>
public class SessionStateProxy : INotifyPropertyChanged
{
private readonly ILogger<SessionStateProxy> _logger;
    private bool _locked = true;
    private string? _sessionId;
    private int _durasiDetik;
    private int _sisaDetik;
    private string? _akunKode;
    private string? _akunNama;
    private string? _akunTipe;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private string _serverStatus = ServerStatusUnknown;

    public SessionStateProxy(ILogger<SessionStateProxy> logger)
    {
        _logger = logger;
    }

    public const string ServerStatusUnknown = "connecting";
    public const string ServerStatusConnected = "connected";
    public const string ServerStatusDisconnected = "disconnected";

    /// <summary>Status koneksi server: connecting | connected | disconnected.</summary>
    public string ServerStatus
    {
        get => _serverStatus;
        set
        {
            if (_serverStatus == value) return;
            _serverStatus = value;
            OnPropertyChanged(nameof(ServerStatus));
            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(ServerTerputus));
            OnPropertyChanged(nameof(LabelWaktuText));
        }
    }

    /// <summary>Teks status koneksi untuk indikator di card login.</summary>
    public string ServerStatusText => _serverStatus switch
    {
        ServerStatusConnected => "Terhubung ke server",
        ServerStatusDisconnected => "Terputus dari server",
        _ => "Menghubungkan ke server...",
    };

    /// <summary>
    /// True kalau server sedang tidak terjangkau.
    /// <para>
    /// Dipakai kartu mini untuk memberi tahu hitung mundur tidak lagi
    /// bergerak. Pada insiden 2 Okt 2026 angka di layar membeku diam-diam
    /// selama 8 jam tanpa satu pun tanda bahwa tidak ada yang menghitung —
    /// dalam aplikasi billing itu lebih buruk daripada tidak ditampilkan.
    /// </para>
    /// </summary>
    public bool ServerTerputus => _serverStatus == ServerStatusDisconnected;

    /// <summary>Label di bawah hitung mundur.</summary>
    public string LabelWaktuText => ServerTerputus ? "SERVER TERPUTUS" : "SISA WAKTU";

    public bool Locked
    {
        get => _locked;
        private set
        {
            if (_locked == value) return;
            _locked = value;
            OnPropertyChanged(nameof(Locked));
            OnPropertyChanged(nameof(IsLocked));
        }
    }

    public string? SessionId
    {
        get => _sessionId;
        private set
        {
            if (_sessionId == value) return;
            _sessionId = value;
            OnPropertyChanged(nameof(SessionId));
        }
    }

    public int DurasiDetik
    {
        get => _durasiDetik;
        private set
        {
            if (_durasiDetik == value) return;
            _durasiDetik = value;
            OnPropertyChanged(nameof(DurasiDetik));
            OnPropertyChanged(nameof(ProgressPercent));
            RaiseWaktuKategoriNotifikasi();
        }
    }

    public int SisaDetik
    {
        get => _sisaDetik;
        private set
        {
            if (_sisaDetik == value) return;
            _sisaDetik = value;
            OnPropertyChanged(nameof(SisaDetik));
            OnPropertyChanged(nameof(CountdownText));
            OnPropertyChanged(nameof(SisaWaktuMenipis));
            OnPropertyChanged(nameof(ProgressPercent));
            RaiseWaktuKategoriNotifikasi();
        }
    }

    /// <summary>Ambang waktu untuk warna timeline mini panel.</summary>
    private const double BatasHijau = 60;
    private const double BatasKuning = 30;

    public bool IsWaktuAman => ProgressPercent >= BatasHijau;
    public bool IsWaktuSedang => ProgressPercent >= BatasKuning && ProgressPercent < BatasHijau;
    public bool IsWaktuKritis => ProgressPercent < BatasKuning;

    private void RaiseWaktuKategoriNotifikasi()
    {
        OnPropertyChanged(nameof(IsWaktuAman));
        OnPropertyChanged(nameof(IsWaktuSedang));
        OnPropertyChanged(nameof(IsWaktuKritis));
    }

    public bool IsLocked => Locked;

    public string? AkunKode
    {
        get => _akunKode;
        private set
        {
            if (_akunKode == value) return;
            _akunKode = value;
            OnPropertyChanged(nameof(AkunKode));
            OnPropertyChanged(nameof(AkunLabel));
            OnPropertyChanged(nameof(AkunIdentitas));
            OnPropertyChanged(nameof(AkunTipeLabel));
        }
    }

    public string? AkunNama
    {
        get => _akunNama;
        private set
        {
            if (_akunNama == value) return;
            _akunNama = value;
            OnPropertyChanged(nameof(AkunNama));
            OnPropertyChanged(nameof(AkunLabel));
            OnPropertyChanged(nameof(AkunIdentitas));
            OnPropertyChanged(nameof(AkunTipeLabel));
        }
    }

    public string? AkunTipe
    {
        get => _akunTipe;
        private set
        {
            if (_akunTipe == value) return;
            _akunTipe = value;
            OnPropertyChanged(nameof(AkunTipe));
            OnPropertyChanged(nameof(AkunLabel));
            OnPropertyChanged(nameof(AkunIdentitas));
            OnPropertyChanged(nameof(AkunTipeLabel));
        }
    }

    /// <summary>Label identitas akun: "VOUCHER 123456" atau "MEMBER nama"/"MEMBERSHIP nama".</summary>
    public string AkunLabel
    {
        get
        {
            string tipe = AkunTipe == "MEMBER" ? "MEMBER" : "VOUCHER";
            return $"{tipe} / {AkunIdentitas}";
        }
    }

    /// <summary>
    /// Identitas akun saja (nama member atau kode voucher), tanpa didahului tipe.
    /// Dipakai sebagai judul kartu mini yang meniru kartu PC di dashboard web.
    /// </summary>
    public string AkunIdentitas
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AkunNama)) return AkunNama!;
            if (!string.IsNullOrWhiteSpace(AkunKode)) return AkunKode!;
            return "-";
        }
    }

    /// <summary>Tipe akun siap tampil: "Voucher" atau "Member".</summary>
    public string AkunTipeLabel => AkunTipe == "MEMBER" ? "Member" : "Voucher";

    /// <summary>
    /// Sisa waktu <= 5 menit — sama ambangnya dengan kartu PC di dashboard web,
    /// supaya operator dan pelanggan melihat penanda yang sama.
    /// </summary>
    public bool SisaWaktuMenipis => SisaDetik <= 300;

    /// <summary>
    /// Hitung mundur siap tampil.
    /// <para>
    /// <b>Format verbatim hanya boleh satu backslash.</b> Di dalam
    /// <c>@"..."</c> tidak ada escaping, jadi <c>@"mm\\:ss"</c> berarti dua
    /// backslash dan <c>TimeSpan.ToString</c> melempar
    /// <c>FormatException</c>. Binding WPF yang melempar tidak menampilkan
    /// apa pun — jadi hitung mundur hilang total untuk setiap sesi di bawah
    /// satu jam. Gejalanya dilaporkan 2 Okt 2026: label "SISA WAKTU" ada
    /// tapi angkanya kosong.
    /// </para>
    /// <para>
    /// Karena itu dibungkus try/catch dan selalu punya nilai cadangan.
    /// Angka ini adalah informasi paling penting di layar itu; ia tidak
    /// boleh kosong dalam keadaan apa pun.
    /// </para>
    /// </summary>
    public string CountdownText
    {
        get
        {
            if (SisaDetik <= 0) return "--:--";
            try
            {
                var ts = TimeSpan.FromSeconds(SisaDetik);
                // Sesi >= 1 jam memakai format dashboard web "Xj MM:SS".
                //
                // ⚠️ JANGAN pakai `hh` di sini. `hh` adalah komponen jam
                // DALAM SEHARI, jadi 3600 detik jadi "1j 01:00" — jamnya
                // dobel dengan angka jam total yang sudah ditulis di depan.
                // Yang dipakai hanya `mm` (menit-dalam-saat) dan `ss`, karena
                // jam total sudah dicetak sendiri.
                return ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}j {ts:mm\\:ss}"
                    : ts.ToString(@"mm\:ss");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Format hitung mundur gagal untuk {Detik} detik", SisaDetik);
                // Cadangan terakhir: detik mentah. Salah bentuk lebih baik
                // daripada tidak tampil sama sekali.
                return $"{SisaDetik}s";
            }
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (DurasiDetik <= 0) return 0;
            return Math.Max(0, Math.Min(100, (double)SisaDetik / DurasiDetik * 100));
        }
    }

    /// <summary>Kurangi sisa detik lokal 1 langkah (fallback countdown bila tick pipe terlewat).</summary>
    public void DecaySisaDetik()
    {
        if (Locked) return;
        if (SisaDetik <= 0) return;
        SisaDetik = SisaDetik - 1;
    }

    /// <summary>Sudah cukup lama tanpa tick dari server — layak memakai fallback decay lokal.</summary>
    public bool IsTickStale()
    {
        return (DateTime.UtcNow - _lastTickUtc).TotalSeconds > 3;
    }

    public void ApplyStateUpdate(string json)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<StateUpdatePayload>(json);
            if (data == null) return;

            // URUTAN PENTING: set SisaDetik/DurasiDetik DULU, baru Locked.
            // Setter Locked memicu UpdateVisibility/UpdateWindowState di MainWindow
            // secara SINKRON (kita sudah dalam Dispatcher.Invoke). Kalau SisaDetik
            // belum di-set, mini-window sesi tidak pernah tampil (race).
            SessionId = data.SessionId;
            DurasiDetik = data.DurasiDetik;
            SisaDetik = data.SisaDetik;
            Locked = data.Locked;

            _logger.LogDebug("State updated: Locked={Locked}, SessionId={SessionId}, Sisa={Sisa}", Locked, SessionId, SisaDetik);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse state update");
        }
    }

    public void ApplySessionTick(string json)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<TickPayload>(json);
            if (data == null) return;
            _lastTickUtc = DateTime.UtcNow;
            SisaDetik = data.SisaDetik;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tick");
        }
    }

    public void ApplySessionStopped(string json)
    {
        Locked = true;
        SessionId = null;
        DurasiDetik = 0;
        SisaDetik = 0;
        AkunKode = null;
        AkunNama = null;
        AkunTipe = null;
    }

    public void ApplySessionStarted(string json)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<SessionStartedPayload>(json);
            if (data == null) return;
            AkunKode = data.KodeUnik;
            AkunNama = data.Nama;
            AkunTipe = data.Tipe;
            _logger.LogInformation("Session started sebagai: {Label}", AkunLabel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse session started");
        }
    }

    /// <summary>Terapkan status koneksi server (indikator hijau/merah di card login).</summary>
    public void ApplyServerLink(string json)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<ServerLinkPayload>(json);
            if (data == null) return;
            ServerStatus = data.Terhubung ? ServerStatusConnected : ServerStatusDisconnected;
            _logger.LogInformation(
                "Status server: {Status}{Alasan}",
                ServerStatusText,
                string.IsNullOrEmpty(data.Alasan) ? "" : $" ({data.Alasan})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse server link status");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private record StateUpdatePayload(bool Locked, string? SessionId, int DurasiDetik, int SisaDetik);
    private record SessionStartedPayload(string? KodeUnik, string? Nama, string? Tipe);
    private record TickPayload(int SisaDetik);
    private record ServerLinkPayload(bool Terhubung, string? Alasan);
}