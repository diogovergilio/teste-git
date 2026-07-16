using DashCall.Contracts;
using MySqlConnector;

namespace DashCall.Collector.Sources.MariaDb;

/// Acesso READ-ONLY ao MariaDB do call center. Cada método executa uma das queries
/// validadas contra o Grafana (paridade exata). Nunca escreve no banco.
///
/// Regras de paridade replicadas aqui:
///  - "Oferecidas" = linhas com datetime_end IS NOT NULL; "Atendidas" = status='terminada';
///    "Abandono" = status='abandonada'.
///  - TMA = SUM(duration)/COUNT(id)  (divide pelo TOTAL — duration é NULL em abandonada e o SUM ignora NULL).
///  - TME = SUM(duration_wait)/COUNT(id). Ambos arredondados para segundos inteiros.
///  - NS% = 100 * SUM(duration_wait <= servicelevel) / COUNT(*) sobre as linhas ended.
///  - Sempre filtrar id_queue_call_entry <> 7 (mantém paridade com o Grafana).
///  - TMO (Tempo Médio Operacional) = TMA + TME (definição do cliente).
///
/// Parametrização da data: quando <c>since</c> é null, usa-se o literal CURDATE() na SQL
/// (produção = "hoje"); caso contrário passa-se @since como parâmetro (janela histórica/teste).
public sealed class CallCenterDb
{
    private readonly string _connectionString;

    public CallCenterDb(string connectionString) => _connectionString = connectionString;

    // ---- DTOs internos -------------------------------------------------------

    private sealed record QueueRow(int QceId, string Ext, string Name);
    private sealed record TodayRow(int Offered, int Answered, int Abandoned, int Tma, int Tme, double Sla);
    private sealed record WaitingRow(int Waiting, int Longest);

    // ---- Montagem do snapshot completo --------------------------------------

    /// Executa TODAS as queries e monta um <see cref="LiveSnapshot"/> coerente.
    /// <paramref name="since"/> null → "hoje" (CURDATE()); caso contrário janela a partir da data.
    public async Task<LiveSnapshot> BuildSnapshotAsync(string tenantId, DateTime? since, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var queues = await GetQueuesAsync(conn, ct);
        var today = await GetTodayByQueueAsync(conn, since, ct);
        var waiting = await GetWaitingByQueueAsync(conn, ct);
        var inprogress = await GetInProgressByQueueAsync(conn, ct);
        var operation = await GetOperationAsync(conn, since, ct);
        var agents = await GetAgentsLiveAsync(conn, ct);
        var ranking = await GetRankingAsync(conn, since, ct);

        var queueLive = new List<QueueLive>(queues.Count);
        foreach (var q in queues)
        {
            var t = today.GetValueOrDefault(q.QceId);
            var w = waiting.GetValueOrDefault(q.QceId);
            int inProg = inprogress.GetValueOrDefault(q.QceId);

            var qToday = t is null
                ? new QueueToday(0, 0, 0, 0, 0, 0)
                : new QueueToday(t.Offered, t.Answered, t.Abandoned, t.Sla, t.Tma, t.Tme);

            // LIMITAÇÃO: o schema não mapeia agente→fila, então a contagem por-fila de agentes
            // (LoggedIn/Available/Paused) NÃO é obtível. Apenas OnCall é real, derivado das chamadas
            // em progresso da fila (current_call_entry). Os demais ficam em 0 propositalmente.
            var qAgents = new QueueAgents(LoggedIn: 0, Available: 0, Paused: 0, OnCall: inProg);

            queueLive.Add(new QueueLive(
                Id: q.Ext,
                Name: q.Name,
                CallsWaiting: w?.Waiting ?? 0,
                LongestWaitSeconds: w?.Longest ?? 0,
                CallsInProgress: inProg,
                Agents: qAgents,
                Today: qToday));
        }

        // Timestamp determinístico (UnixEpoch): o hub usa a hora de recepção na produção se precisar.
        return new LiveSnapshot(
            TenantId: tenantId,
            Timestamp: DateTimeOffset.UnixEpoch,
            Queues: queueLive,
            Agents: agents,
            Operation: operation,
            Ranking: ranking);
    }

    // ---- Queries individuais -------------------------------------------------

    /// Q_queues — todas as filas configuradas.
    private static async Task<List<QueueRow>> GetQueuesAsync(MySqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT b.id AS qce_id, b.queue AS ext, c.descr AS name
FROM queue_call_entry b JOIN asterisk.queues_config c ON b.queue=c.extension
WHERE b.id<>7;";
        var rows = new List<QueueRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new QueueRow(r.GetInt32("qce_id"), r.GetString("ext"), r.GetString("name")));
        return rows;
    }

    /// Q_today_by_queue (@since) — contadores do dia por fila.
    private static async Task<Dictionary<int, TodayRow>> GetTodayByQueueAsync(
        MySqlConnection conn, DateTime? since, CancellationToken ct)
    {
        string sql = $@"
SELECT b.id AS qce_id, COUNT(*) offered, SUM(a.status='terminada') answered,
  SUM(a.status='abandonada') abandoned,
  ROUND(IFNULL(SUM(a.duration)/COUNT(a.id),0)) tma, ROUND(IFNULL(SUM(a.duration_wait)/COUNT(a.id),0)) tme,
  ROUND(100*SUM(a.duration_wait<=d.data)/COUNT(*),2) sla
FROM call_entry a JOIN queue_call_entry b ON a.id_queue_call_entry=b.id
JOIN asterisk.queues_details d ON b.queue=d.id AND d.keyword='servicelevel'
WHERE a.datetime_end IS NOT NULL AND a.id_queue_call_entry<>7 AND a.datetime_end>={SincePredicate(since)}
GROUP BY b.id;";
        var map = new Dictionary<int, TodayRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        AddSince(cmd, since);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetInt32("qce_id")] = new TodayRow(
                Offered: ToInt(r["offered"]),
                Answered: ToInt(r["answered"]),
                Abandoned: ToInt(r["abandoned"]),
                Tma: ToInt(r["tma"]),
                Tme: ToInt(r["tme"]),
                Sla: ToDouble(r["sla"]));
        return map;
    }

    /// Q_operation (@since) — consolidado global (mesma lógica do por-fila, sem GROUP BY). TmoSeconds = null.
    private static async Task<OperationToday> GetOperationAsync(
        MySqlConnection conn, DateTime? since, CancellationToken ct)
    {
        string sql = $@"
SELECT COUNT(*) offered, SUM(a.status='terminada') answered,
  SUM(a.status='abandonada') abandoned,
  ROUND(IFNULL(SUM(a.duration)/COUNT(a.id),0)) tma, ROUND(IFNULL(SUM(a.duration_wait)/COUNT(a.id),0)) tme,
  ROUND(100*SUM(a.duration_wait<=d.data)/COUNT(*),2) sla
FROM call_entry a JOIN queue_call_entry b ON a.id_queue_call_entry=b.id
JOIN asterisk.queues_details d ON b.queue=d.id AND d.keyword='servicelevel'
WHERE a.datetime_end IS NOT NULL AND a.id_queue_call_entry<>7 AND a.datetime_end>={SincePredicate(since)};";
        await using var cmd = new MySqlCommand(sql, conn);
        AddSince(cmd, since);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return new OperationToday(0, 0, 0, 0, 0, 0, null);
        return new OperationToday(
            Offered: ToInt(r["offered"]),
            Answered: ToInt(r["answered"]),
            Abandoned: ToInt(r["abandoned"]),
            SlaPercent: ToDouble(r["sla"]),
            TmaSeconds: ToInt(r["tma"]),
            TmeSeconds: ToInt(r["tme"]),
            TmoSeconds: ToInt(r["tma"]) + ToInt(r["tme"])); // TMO = TMA + TME (Tempo Médio Operacional)
    }

    /// Q_waiting_by_queue — chamadas em espera agora (ao vivo).
    private static async Task<Dictionary<int, WaitingRow>> GetWaitingByQueueAsync(
        MySqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT b.id qce_id, COUNT(*) waiting, IFNULL(MAX(TIMESTAMPDIFF(SECOND,a.datetime_entry_queue,NOW())),0) longest
FROM call_entry a JOIN queue_call_entry b ON a.id_queue_call_entry=b.id
WHERE a.status IN ('en_cola','en-cola') AND a.id_agent IS NULL AND a.id_queue_call_entry<>7
GROUP BY b.id;";
        var map = new Dictionary<int, WaitingRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetInt32("qce_id")] = new WaitingRow(ToInt(r["waiting"]), ToInt(r["longest"]));
        return map;
    }

    /// Q_inprogress_by_queue — chamadas em atendimento agora, por fila.
    private static async Task<Dictionary<int, int>> GetInProgressByQueueAsync(
        MySqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT b.id qce_id, COUNT(*) inprog FROM current_call_entry a
JOIN queue_call_entry b ON a.id_queue_call_entry=b.id WHERE a.id_queue_call_entry<>7 GROUP BY b.id;";
        var map = new Dictionary<int, int>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetInt32("qce_id")] = ToInt(r["inprog"]);
        return map;
    }

    /// Q_agents_live — agentes logados com estado atual.
    /// Estado: on_call→OnCall; senão on_break→Paused; senão Available. QueueIds vazio (schema não mapeia agente→fila).
    private static async Task<List<AgentLive>> GetAgentsLiveAsync(MySqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT ag.id, ag.name, MAX(br.id IS NOT NULL) on_break, MAX(brk.name) break_name,
  MAX(cce.id_agent IS NOT NULL) on_call, IFNULL(MAX(TIMESTAMPDIFF(SECOND,cce.datetime_init,NOW())),0) call_seconds
FROM audit login JOIN agent ag ON login.id_agent=ag.id
LEFT JOIN audit br ON br.id_agent=ag.id AND br.datetime_end IS NULL AND br.id_break IS NOT NULL
LEFT JOIN break brk ON br.id_break=brk.id
LEFT JOIN current_call_entry cce ON cce.id_agent=ag.id
WHERE login.datetime_end IS NULL AND login.id_break IS NULL
GROUP BY ag.id, ag.name;";
        var agents = new List<AgentLive>();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            bool onCall = ToInt(r["on_call"]) != 0;
            bool onBreak = ToInt(r["on_break"]) != 0;
            var state = onCall ? AgentState.OnCall : onBreak ? AgentState.Paused : AgentState.Available;
            agents.Add(new AgentLive(
                Id: r.GetInt32("id").ToString(),
                Name: r.GetString("name"),
                State: state,
                CurrentCallSeconds: ToInt(r["call_seconds"]),
                QueueIds: Array.Empty<string>()));
        }
        return agents;
    }

    /// Q_ranking (@since, top 20) — agentes por atendidas (desc).
    /// Sem filtro de tipo/estatus: qualquer agente que atendeu hoje aparece (inclui recepções type='SIP').
    private static async Task<List<AgentRank>> GetRankingAsync(
        MySqlConnection conn, DateTime? since, CancellationToken ct)
    {
        string sql = $@"
SELECT ag.id, ag.name, COUNT(a.id) answered, ROUND(IFNULL(SUM(a.duration)/COUNT(a.id),0)) tma
FROM agent ag LEFT JOIN call_entry a ON ag.id=a.id_agent AND a.datetime_end IS NOT NULL
  AND a.id_queue_call_entry<>7 AND a.datetime_end>={SincePredicate(since)}
GROUP BY ag.id, ag.name HAVING answered>0 ORDER BY answered DESC LIMIT 20;";
        var ranking = new List<AgentRank>();
        await using var cmd = new MySqlCommand(sql, conn);
        AddSince(cmd, since);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            ranking.Add(new AgentRank(
                AgentId: r.GetInt32("id").ToString(),
                Name: r.GetString("name"),
                Answered: ToInt(r["answered"]),
                TmaSeconds: ToInt(r["tma"])));
        return ranking;
    }

    // ---- Helpers -------------------------------------------------------------

    /// Predicado da data: CURDATE() literal quando since é null; senão @since parametrizado.
    private static string SincePredicate(DateTime? since) => since.HasValue ? "@since" : "CURDATE()";

    private static void AddSince(MySqlCommand cmd, DateTime? since)
    {
        if (since.HasValue)
            cmd.Parameters.Add(new MySqlParameter("@since", since.Value));
    }

    // SUM(...) de expressões booleanas volta como DECIMAL/long; COUNT como long. Normaliza p/ int.
    private static int ToInt(object value) =>
        value is null || value == DBNull.Value ? 0 : (int)Math.Round(Convert.ToDouble(value));

    private static double ToDouble(object value) =>
        value is null || value == DBNull.Value ? 0d : Convert.ToDouble(value);
}
