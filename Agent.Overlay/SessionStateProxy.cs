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

    public SessionStateProxy(ILogger<SessionStateProxy> logger)
    {
        _logger = logger;
    }

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
            OnPropertyChanged(nameof(ProgressPercent));
        }
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
        }
    }

    /// <summary>Label identitas akun: "VOUCHER 123456" atau "MEMBER nama"/"MEMBERSHIP nama".</summary>
    public string AkunLabel
    {
        get
        {
            string tipe = AkunTipe == "MEMBER" ? "MEMBER" : "VOUCHER";
            string identitas = !string.IsNullOrWhiteSpace(AkunNama)
                ? AkunNama!
                : !string.IsNullOrWhiteSpace(AkunKode)
                    ? AkunKode!
                    : "-";
            return $"{tipe} / {identitas}";
        }
    }

    public string CountdownText
    {
        get
        {
            if (SisaDetik <= 0) return "--:--";
            var ts = TimeSpan.FromSeconds(SisaDetik);
            return ts.ToString(@"mm\:ss");
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private record StateUpdatePayload(bool Locked, string? SessionId, int DurasiDetik, int SisaDetik);
    private record SessionStartedPayload(string? KodeUnik, string? Nama, string? Tipe);
    private record TickPayload(int SisaDetik);
}