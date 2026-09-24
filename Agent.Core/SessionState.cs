namespace V3Netbill.Agent.Core;

/// <summary>
/// Status sesi yang berjalan di PC ini: sessionId + total/sisa durasi detik.
/// Fase 7 hanya membawa data; logika kunci overlay (LockScreen) baru Fase 8.
/// </summary>
public sealed class SessionState
{
    public enum LockState { Unlocked, Locked }

    public string? SessionId { get; set; }
    public int DurasiDetik { get; set; }
    public int SisaDetik { get; set; }
    public LockState State { get; set; } = LockState.Unlocked;

    public bool SedangBerjalan => !string.IsNullOrWhiteSpace(SessionId);
}
