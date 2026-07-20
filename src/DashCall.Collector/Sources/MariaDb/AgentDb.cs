using DashCall.Contracts;
using MySqlConnector;

namespace DashCall.Collector.Sources.MariaDb;

/// Acesso READ-ONLY aos dados do Módulo 3 (painel individual do agente).
/// Arquivo próprio, como o AnalysisDb: o CallCenterDb guarda as queries de paridade com o
/// Grafana e não deve ser mexido por conta de tela nova.
///
/// Portabilidade: sem window functions (um cliente roda MariaDB 5.5).
public sealed class AgentDb
{
    private readonly string _connectionString;

    /// Dias exibidos na evolução (inclui hoje).
    public const int DiasSerie = 7;

    public AgentDb(string connectionString) => _connectionString = connectionString;

    /// Cadastro de agentes do cliente — alimenta o combo do supervisor ao criar um login.
    public async Task<List<AgentSummary>> ListarAgentesAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Ordena ativos primeiro: é quem o supervisor procura. Traz inativos porque um agente
        // recém-desligado ainda pode precisar de consulta.
        const string sql = @"
SELECT id, number, name, type, estatus
FROM agent
ORDER BY (estatus='A') DESC, name;";
        var rows = new List<AgentSummary>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AgentSummary(
                ToInt(r["id"]),
                r["number"]?.ToString() ?? "",
                r["name"]?.ToString() ?? "",
                r["type"]?.ToString() ?? "",
                (r["estatus"]?.ToString() ?? "") == "A"));

        return rows;
    }

    /// Painel de um agente. Devolve null quando o id não existe no cadastro do cliente
    /// (agente removido do Issabel depois de vinculado, por exemplo).
    public async Task<AgentDetail?> BuildDetailAsync(int agentId, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cadastro = await GetCadastroAsync(conn, agentId, ct);
        if (cadastro is null) return null;

        var (estado, callSeconds, breakName, breakSeconds) = await GetEstadoAsync(conn, agentId, ct);
        var hoje = await GetHojeAsync(conn, agentId, ct);
        var semana = await GetSemanaAsync(conn, agentId, ct);
        var satisfacao = await GetSatisfacaoAsync(conn, cadastro, ct);

        return new AgentDetail(
            Id: cadastro.Id,
            Number: cadastro.Number,
            Name: cadastro.Name,
            State: estado,
            CurrentCallSeconds: callSeconds,
            BreakName: breakName,
            BreakSeconds: breakSeconds,
            Hoje: hoje,
            Semana: semana,
            Satisfacao: satisfacao);
    }

    private static async Task<AgentSummary?> GetCadastroAsync(
        MySqlConnection conn, int agentId, CancellationToken ct)
    {
        const string sql = "SELECT id, number, name, type, estatus FROM agent WHERE id=@id;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.Add(new MySqlParameter("@id", agentId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new AgentSummary(
            ToInt(r["id"]), r["number"]?.ToString() ?? "", r["name"]?.ToString() ?? "",
            r["type"]?.ToString() ?? "", (r["estatus"]?.ToString() ?? "") == "A");
    }

    /// Estado atual: mesma lógica de presença do wallboard, porém para um agente só.
    private static async Task<(AgentState, int, string?, int)> GetEstadoAsync(
        MySqlConnection conn, int agentId, CancellationToken ct)
    {
        const string sql = @"
SELECT MAX(br.id IS NOT NULL) on_break, MAX(brk.name) break_name,
  IFNULL(MAX(TIMESTAMPDIFF(SECOND,br.datetime_init,NOW())),0) break_seconds,
  MAX(cce.id_agent IS NOT NULL) on_call,
  IFNULL(MAX(TIMESTAMPDIFF(SECOND,cce.datetime_init,NOW())),0) call_seconds
FROM audit login
LEFT JOIN audit br ON br.id_agent=login.id_agent AND br.datetime_end IS NULL AND br.id_break IS NOT NULL
LEFT JOIN break brk ON br.id_break=brk.id
LEFT JOIN current_call_entry cce ON cce.id_agent=login.id_agent
WHERE login.id_agent=@id AND login.datetime_end IS NULL AND login.id_break IS NULL;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.Add(new MySqlParameter("@id", agentId));
        await using var r = await cmd.ExecuteReaderAsync(ct);

        // Sem login aberto = fora do sistema.
        if (!await r.ReadAsync(ct) || r["on_call"] == DBNull.Value)
            return (AgentState.Offline, 0, null, 0);

        bool onCall = ToInt(r["on_call"]) != 0;
        bool onBreak = ToInt(r["on_break"]) != 0;
        var estado = onCall ? AgentState.OnCall : onBreak ? AgentState.Paused : AgentState.Available;

        return (
            estado,
            onCall ? ToInt(r["call_seconds"]) : 0,
            onBreak && r["break_name"] != DBNull.Value ? r["break_name"]!.ToString() : null,
            onBreak ? ToInt(r["break_seconds"]) : 0);
    }

    /// Números de hoje. Tempo logado e em pausa saem do `audit`: períodos fechados somam a
    /// duração, e o período aberto conta até agora (senão o tempo "congela" enquanto a pessoa
    /// ainda está logada, que é justamente quando ela olha o painel).
    private static async Task<AgentToday> GetHojeAsync(
        MySqlConnection conn, int agentId, CancellationToken ct)
    {
        const string sqlChamadas = @"
SELECT COUNT(*) atendidas, ROUND(IFNULL(SUM(duration)/COUNT(id),0)) tma
FROM call_entry
WHERE id_agent=@id AND status='terminada' AND datetime_end>=CURDATE();";
        int atendidas = 0, tma = 0;
        await using (var cmd = new MySqlCommand(sqlChamadas, conn))
        {
            cmd.Parameters.Add(new MySqlParameter("@id", agentId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                atendidas = ToInt(r["atendidas"]);
                tma = ToInt(r["tma"]);
            }
        }

        const string sqlTempos = @"
SELECT
  IFNULL(SUM(CASE WHEN id_break IS NULL THEN
    TIMESTAMPDIFF(SECOND, GREATEST(datetime_init, CURDATE()), IFNULL(datetime_end, NOW())) END),0) logado,
  IFNULL(SUM(CASE WHEN id_break IS NOT NULL THEN
    TIMESTAMPDIFF(SECOND, GREATEST(datetime_init, CURDATE()), IFNULL(datetime_end, NOW())) END),0) pausa
FROM audit
WHERE id_agent=@id AND (datetime_end IS NULL OR datetime_end>=CURDATE());";
        int logado = 0, pausa = 0;
        await using (var cmd = new MySqlCommand(sqlTempos, conn))
        {
            cmd.Parameters.Add(new MySqlParameter("@id", agentId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                logado = ToInt(r["logado"]);
                pausa = ToInt(r["pausa"]);
            }
        }

        int? posicao = await GetPosicaoAsync(conn, agentId, ct);
        return new AgentToday(atendidas, tma, logado, pausa, posicao);
    }

    /// Posição no ranking de hoje (1 = quem mais atendeu). Null quando o agente ainda não atendeu:
    /// mostrar "12º de 12" para quem tem zero seria só constrangimento sem informação.
    private static async Task<int?> GetPosicaoAsync(
        MySqlConnection conn, int agentId, CancellationToken ct)
    {
        const string sql = @"
SELECT id_agent, COUNT(*) atendidas
FROM call_entry
WHERE status='terminada' AND datetime_end>=CURDATE() AND id_agent IS NOT NULL
GROUP BY id_agent ORDER BY atendidas DESC;";
        var ordem = new List<int>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ordem.Add(ToInt(r["id_agent"]));

        int i = ordem.IndexOf(agentId);
        return i >= 0 ? i + 1 : null;
    }

    /// Atendidas por dia nos últimos <see cref="DiasSerie"/> dias, com os dias sem
    /// movimento presentes em zero — o gráfico não pode mudar de forma conforme a semana.
    private static async Task<List<AgentDay>> GetSemanaAsync(
        MySqlConnection conn, int agentId, CancellationToken ct)
    {
        const string sql = @"
SELECT DATE(datetime_end) dia, COUNT(*) atendidas
FROM call_entry
WHERE id_agent=@id AND status='terminada'
  AND datetime_end >= CURDATE() - INTERVAL @dias DAY
GROUP BY dia;";
        var porDia = new Dictionary<DateOnly, int>();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.Add(new MySqlParameter("@id", agentId));
        cmd.Parameters.Add(new MySqlParameter("@dias", DiasSerie - 1));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            porDia[DateOnly.FromDateTime(r.GetDateTime("dia"))] = ToInt(r["atendidas"]);

        var hoje = DateOnly.FromDateTime(DateTime.Today);
        return Enumerable.Range(0, DiasSerie)
            .Select(i => hoje.AddDays(-(DiasSerie - 1 - i)))
            .Select(d => new AgentDay(d, porDia.GetValueOrDefault(d)))
            .ToList();
    }

    /// Nota do agente na pesquisa, no mesmo recorte da série. Null quando o cliente não tem
    /// pesquisa (banco ausente ou sem GRANT) — nunca derruba o painel.
    ///
    /// A pesquisa grava o CANAL ("Agent/24"), não o ramal: por isso os canais são resolvidos com o
    /// AgentResolver e comparados pelo id do cadastro, e não por string.
    private async Task<AgentSatisfaction?> GetSatisfacaoAsync(
        MySqlConnection conn, AgentSummary agente, CancellationToken ct)
    {
        try
        {
            const string sql = @"
SELECT avaliado canal, AVG(p2) media, COUNT(*) avaliacoes
FROM pesquisa.pesquisa
WHERE avaliado<>'' AND tipo<>'DESISTIU'
  AND data >= CURDATE() - INTERVAL @dias DAY
GROUP BY avaliado;";
            var crus = new List<(string Canal, double Media, int Avaliacoes)>();
            await using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.Add(new MySqlParameter("@dias", DiasSerie - 1));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    crus.Add((r.GetString("canal"), ToDouble(r["media"]), ToInt(r["avaliacoes"])));
            }
            if (crus.Count == 0) return null;

            var agentes = await GetTodosAgentesAsync(conn, ct);
            var doAgente = crus
                .Where(x => AgentResolver.ResolverId(x.Canal, agentes) == agente.Id)
                .ToList();
            if (doAgente.Count == 0) return null;

            int total = doAgente.Sum(x => x.Avaliacoes);
            double media = doAgente.Sum(x => x.Media * x.Avaliacoes) / total;
            return new AgentSatisfaction(Math.Round(media, 2), total);
        }
        catch (MySqlException)
        {
            // Banco `pesquisa` ausente ou sem GRANT: o painel sai sem a nota.
            return null;
        }
    }

    private static async Task<List<AgentRow>> GetTodosAgentesAsync(
        MySqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT id, number, name, type, estatus FROM agent;";
        var rows = new List<AgentRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AgentRow(
                ToInt(r["id"]), r["number"]?.ToString() ?? "", r["name"]?.ToString() ?? "",
                r["type"]?.ToString() ?? "", r["estatus"]?.ToString() ?? ""));

        return rows;
    }

    private static int ToInt(object v) => v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    private static double ToDouble(object v) => v is null || v == DBNull.Value ? 0 : Convert.ToDouble(v);
}
