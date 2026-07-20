using DashCall.Contracts;
using MySqlConnector;

namespace DashCall.Collector.Eccp;

/// Controle do agente (Módulo 7 — ESCRITA) via daemon ECCP. Lê do MariaDB o canal completo
/// (`Type/number`) e o `eccp_password` do agente; monta o agent_hash; executa a ação.
///
/// A diferença SIP/ vs Agent/ vive só aqui: SIP/ (QueueAdd) responde na hora; Agent/ (AgentLogin)
/// responde "logging" e o resultado vem pelo evento `agentloggedin` — que aguardamos por um tempo.
public sealed class EccpAgentSource
{
    private readonly string _connectionString;
    private readonly string _host;
    private readonly int _port;
    private readonly string _eccpUser;
    private readonly string _eccpPass;

    /// Quanto esperar o evento agentloggedin no login Agent/ (a pessoa digita a senha no telefone).
    private static readonly TimeSpan EsperaLogin = TimeSpan.FromSeconds(30);

    public EccpAgentSource(string connectionString, string eccpHost, string eccpUser, string eccpPass,
        int eccpPort = EccpClient.DefaultPort)
    {
        _connectionString = connectionString;
        _host = eccpHost;
        _port = eccpPort;
        _eccpUser = eccpUser;
        _eccpPass = eccpPass;
    }

    public async Task<AgentActionResult> ExecutarAsync(AgentActionRequest req, CancellationToken ct)
    {
        var (canal, ext, eccpPass) = await LerAgenteAsync(req.AgentId, ct);
        if (canal is null)
            return new AgentActionResult(req.CorrelationId, AgentControlState.Failed,
                "Agente não encontrado no cadastro do PABX.");

        try
        {
            await using var c = await EccpClient.ConnectAsync(_host, _port, _eccpUser, _eccpPass, ct);
            var hash = EccpProtocol.AgentHash(c.AppCookie, canal, eccpPass);
            var numero = SoDigitos(canal);

            return req.Acao switch
            {
                AgentAction.Login => await LoginAsync(c, req, canal, numero, ext, hash, ct),
                AgentAction.Logout => await ComandoAsync(c, req,
                    EccpProtocol.LogoutAgent(c.NextId(), canal, hash), AgentControlState.LoggedOut, ct),
                AgentAction.Pause => await ComandoAsync(c, req,
                    EccpProtocol.PauseAgent(c.NextId(), canal, hash, req.BreakId ?? 0),
                    AgentControlState.Paused, ct),
                AgentAction.Unpause => await ComandoAsync(c, req,
                    EccpProtocol.UnpauseAgent(c.NextId(), canal, hash), AgentControlState.LoggedIn, ct),
                _ => new AgentActionResult(req.CorrelationId, AgentControlState.Failed, "Ação desconhecida."),
            };
        }
        catch (EccpException ex)
        {
            return new AgentActionResult(req.CorrelationId, AgentControlState.Failed,
                MensagemErro(ex.Code, ex.Message));
        }
    }

    private async Task<AgentActionResult> LoginAsync(
        EccpClient c, AgentActionRequest req, string canal, string numero, string ext, string hash,
        CancellationToken ct)
    {
        var resp = await c.RequestAsync(EccpProtocol.LoginAgent(c.NextId(), numero, hash, ext), ct);
        if (EccpProtocol.EhFalha(resp))
        {
            var (code, msg) = EccpProtocol.Falha(resp);
            return new AgentActionResult(req.CorrelationId, AgentControlState.Failed, MensagemErro(code, msg));
        }

        var status = EccpProtocol.Campo(resp, "status");
        // SIP/ (QueueAdd) já volta logado. Agent/ volta "logging" e o resultado vem por evento.
        if (status == "logged-in")
            return new AgentActionResult(req.CorrelationId, AgentControlState.LoggedIn);

        // Agent/: aguarda o telefone/senha. agentloggedin = ok; agentfailedlogin = falhou;
        // timeout = ainda "logging" (a tela pede para atender o telefone).
        using var esperaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        esperaCts.CancelAfter(EsperaLogin);
        var evento = await c.AguardarEventoAsync(["agentloggedin", "agentfailedlogin"], esperaCts.Token);

        return EccpProtocol.NomeEvento(evento ?? "") switch
        {
            "agentloggedin" => new AgentActionResult(req.CorrelationId, AgentControlState.LoggedIn),
            "agentfailedlogin" => new AgentActionResult(req.CorrelationId, AgentControlState.Failed,
                "Login não confirmado — a senha no telefone pode ter sido recusada."),
            _ => new AgentActionResult(req.CorrelationId, AgentControlState.Logging), // timeout
        };
    }

    private static async Task<AgentActionResult> ComandoAsync(
        EccpClient c, AgentActionRequest req, string requestXml, AgentControlState okState,
        CancellationToken ct)
    {
        var resp = await c.RequestAsync(requestXml, ct);
        if (EccpProtocol.EhFalha(resp))
        {
            var (code, msg) = EccpProtocol.Falha(resp);
            return new AgentActionResult(req.CorrelationId, AgentControlState.Failed, MensagemErro(code, msg));
        }
        return new AgentActionResult(req.CorrelationId, okState);
    }

    /// Canal completo ("Agent/9000"), extensão física e eccp_password do agente.
    private async Task<(string? Canal, string Ext, string EccpPass)> LerAgenteAsync(
        int agentId, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        // A extensão física do agente vem do FreePBX (asterisk.devices) pelo ramal; para SIP/ é o
        // próprio número. Aqui usamos o `number` como extensão — o daemon resolve o `dial` real.
        const string sql = @"
SELECT CONCAT(type,'/',number) canal, number, IFNULL(eccp_password,'') eccp
FROM call_center.agent WHERE id=@id AND estatus='A';";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", agentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, "", "");
        return (r.GetString("canal"), r.GetString("number"), r.GetString("eccp"));
    }

    private static string SoDigitos(string canal)
    {
        var barra = canal.IndexOf('/');
        return barra >= 0 ? canal[(barra + 1)..] : canal;
    }

    /// Traduz os códigos do daemon em mensagens que a atendente entende.
    private static string MensagemErro(int code, string msg) => code switch
    {
        401 => "Não autorizado a operar este agente.",
        404 => "Agente ou ramal não encontrado no PABX.",
        409 => "Este agente já está logado em outro ramal.",
        417 => "Ação não permitida no estado atual do agente.",
        500 => "O PABX não conseguiu concluir a operação. Tente novamente.",
        _ => msg,
    };
}
