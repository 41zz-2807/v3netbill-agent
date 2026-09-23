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
    private bool _locked;
    private string? _sessionId;
    private int _durasiDetik;
    private int _sisaDetik;

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

    public void ApplyStateUpdate(string json)
    {
        try
        {
            var data = JsonConvert.DeserializeObject<StateUpdatePayload>(json);
            if (data == null) return;

            Locked = data.Locked;
            SessionId = data.SessionId;
            DurasiDetik = data.DurasiDetik;
            SisaDetik = data.SisaDetik;

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
            SisaDetik = data.SisaDetik;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tick");
        }
    }

    public void ApplySessionStopped(string json)
    {
        Locked = false;
        SessionId = null;
        DurasiDetik = 0;
        SisaDetik = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private record StateUpdatePayload(bool Locked, string? SessionId, int DurasiDetik, int SisaDetik);
    private record TickPayload(int SisaDetik);
}