namespace V3Netbill.Agent.Core;

/// <summary>
/// Nama event + key payload koneksi agent ↔ server v3Netbill (Socket.IO).
///
/// CATATAN SYNTAX: nilai event Socket.IO bersifat opaque string; SocketIOClient v4
/// membaca <b>namespace</b> dari path URI (mis. <c>http://host:3000/session</c> →
/// namespace <c>/session</c>). Event "emis" dipakai juga sebagai kunci event C#.
/// </summary>
public static class AgentEvents
{
    // ---------- namespace (cocokkan SessionGateway NestJS) ----------
    public const string SessionNamespace = "/session";

    // ---------- agent → server (emit) ----------
    public const string AgentRegister = "agent:register";
    public const string AgentHeartbeat = "agent:heartbeat";
    public const string ClientLoginRequest = "client:login_request";

    // ---------- server → agent (receive) ----------
    public const string ClientLoginResult = "client:login_result";
    public const string SessionStart = "session:start";
    public const string SessionTick = "session:tick";
    public const string SessionStop = "session:stop";

    // ---------- key payload (camelCase, sinkron gateway NestJS) ----------
    public const string KeyPcId = "pcId";
    public const string KeyAgentToken = "agentToken";
    public const string KeyKode = "kode";
    public const string KeyPassword = "password";
    public const string KeySukses = "sukses";
    public const string KeyAlasan = "alasan";
    public const string KeySessionId = "sessionId";
    public const string KeyDurasiDetik = "durasiDetik";
    public const string KeySisaDetik = "sisaDetik";
}
