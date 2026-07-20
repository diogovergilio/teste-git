using DashCall.Contracts;
using MySqlConnector;

namespace DashCall.Collector.Sources.MariaDb;

/// Acesso READ-ONLY às queries da folha 2 ("Análise") do relatório.
/// Separado do <see cref="CallCenterDb"/> de propósito: lá ficam as queries de paridade com o
/// Grafana, que não devem ser mexidas por conta de um bloco analítico novo.
///
/// Janela: filtra por <c>datetime_end</c>, a MESMA coluna do resumo da folha 1 — assim a soma do
/// mapa de calor bate com o total ofertado. O agrupamento por hora/dia usa
/// <c>datetime_entry_queue</c>, que é quando a ligação de fato chegou.
///
/// Portabilidade: sem window functions (LEAD/OVER). O Issabel4 roda em CentOS7, onde MariaDB 5.5
/// é comum, e elas exigem 10.2+.
public sealed class AnalysisDb
{
    private readonly string _connectionString;

    /// Janela de rechamada do FCR (definição acordada com o cliente).
    public static readonly TimeSpan JanelaFcr = TimeSpan.FromHours(24);

    /// Avisa uma única vez que a satisfação está indisponível — este relatório é pedido sob
    /// demanda e repetir o aviso a cada consulta só poluiria o log.
    private bool _avisouSemPesquisa;

    public AnalysisDb(string connectionString) => _connectionString = connectionString;

    /// Monta a folha 2 para a janela [inicio, fim) (fim exclusivo).
    public async Task<ReportAnalysis> BuildAnalysisAsync(DateTime inicio, DateTime fim, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var heat = await GetHeatAsync(conn, inicio, fim, ct);
        var bands = await GetAbandonBandsAsync(conn, inicio, fim, ct);
        var fcr = Fcr.Calcular(await GetCallRowsAsync(conn, inicio, fim, ct), JanelaFcr);
        var satisfaction = await GetSatisfactionAsync(conn, inicio, fim, ct);

        return new ReportAnalysis(heat, fcr, bands, satisfaction);
    }

    /// Mapa de calor: uma célula por (dia da semana, hora) com total e abandonadas.
    /// Só volta célula com movimento — o frontend preenche a grade 7x24 com zeros.
    private static async Task<List<HeatCell>> GetHeatAsync(
        MySqlConnection conn, DateTime inicio, DateTime fim, CancellationToken ct)
    {
        const string sql = @"
SELECT DAYOFWEEK(a.datetime_entry_queue) dow, HOUR(a.datetime_entry_queue) h,
  COUNT(*) total, SUM(a.status='abandonada') abandonadas
FROM call_entry a
WHERE a.datetime_end IS NOT NULL AND a.id_queue_call_entry<>7
  AND a.datetime_end>=@inicio AND a.datetime_end<@fim
  AND a.datetime_entry_queue IS NOT NULL
GROUP BY dow, h;";
        var cells = new List<HeatCell>();
        await using var cmd = new MySqlCommand(sql, conn);
        AddWindow(cmd, inicio, fim);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cells.Add(new HeatCell(ToInt(r["dow"]), ToInt(r["h"]), ToInt(r["total"]), ToInt(r["abandonadas"])));

        return cells;
    }

    /// Abandono por faixa de espera. O corte de 60s é o `servicelevel` do cliente: separa
    /// "abandonou dentro do SLA" de "abandonou porque esperou demais". A faixa 0-10s isola a
    /// desistência imediata (normalmente engano, não insatisfação).
    private static async Task<List<AbandonBand>> GetAbandonBandsAsync(
        MySqlConnection conn, DateTime inicio, DateTime fim, CancellationToken ct)
    {
        const string sql = @"
SELECT CASE
    WHEN a.duration_wait<=10 THEN '0-10s'
    WHEN a.duration_wait<=30 THEN '11-30s'
    WHEN a.duration_wait<=60 THEN '31-60s'
    WHEN a.duration_wait<=120 THEN '61-120s'
    ELSE '>120s' END faixa,
  COUNT(*) quantidade
FROM call_entry a
WHERE a.datetime_end IS NOT NULL AND a.id_queue_call_entry<>7 AND a.status='abandonada'
  AND a.datetime_end>=@inicio AND a.datetime_end<@fim
GROUP BY faixa;";
        var raw = new Dictionary<string, int>();
        await using var cmd = new MySqlCommand(sql, conn);
        AddWindow(cmd, inicio, fim);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            raw[r.GetString("faixa")] = ToInt(r["quantidade"]);

        // Ordem fixa e faixas sem ocorrência presentes com zero: o gráfico não pode
        // trocar de forma conforme o período escolhido.
        string[] ordem = ["0-10s", "11-30s", "31-60s", "61-120s", ">120s"];
        int total = raw.Values.Sum();

        return ordem
            .Select(f =>
            {
                int q = raw.GetValueOrDefault(f);
                return new AbandonBand(f, q, total > 0 ? Math.Round(100.0 * q / total, 2) : 0);
            })
            .ToList();
    }

    /// Leitura enxuta para o FCR: uma varredura, já ordenada por número e horário.
    /// Traz também as abandonadas — elas não entram no denominador, mas provam que o cliente voltou.
    private static async Task<List<CallRow>> GetCallRowsAsync(
        MySqlConnection conn, DateTime inicio, DateTime fim, CancellationToken ct)
    {
        const string sql = @"
SELECT a.callerid, a.datetime_entry_queue, a.status
FROM call_entry a
WHERE a.datetime_end IS NOT NULL AND a.id_queue_call_entry<>7
  AND a.datetime_end>=@inicio AND a.datetime_end<@fim
  AND a.datetime_entry_queue IS NOT NULL
ORDER BY a.callerid, a.datetime_entry_queue;";
        var rows = new List<CallRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        AddWindow(cmd, inicio, fim);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new CallRow(
                r.GetString("callerid"),
                r.GetDateTime("datetime_entry_queue"),
                r.GetString("status") == "terminada"));

        return rows;
    }

    /// Pesquisa de satisfação — vive no banco `pesquisa`, FORA do call_center (mesma conexão,
    /// nome qualificado). Devolve null quando não há avaliações no período OU quando o banco/GRANT
    /// não existe: cliente sem pesquisa simplesmente não vê o bloco, e o relatório inteiro não cai.
    private async Task<SatisfactionBlock?> GetSatisfactionAsync(
        MySqlConnection conn, DateTime inicio, DateTime fim, CancellationToken ct)
    {
        try
        {
            const string totais = @"
SELECT COUNT(*) avaliacoes,
  AVG(CASE WHEN tipo<>'DESISTIU' THEN p1 END) media_p1,
  AVG(CASE WHEN tipo<>'DESISTIU' THEN p2 END) media_p2
FROM pesquisa.pesquisa
WHERE data>=@inicio AND data<@fim;";
            int avaliacoes;
            double p1, p2;
            await using (var cmd = new MySqlCommand(totais, conn))
            {
                AddWindow(cmd, inicio, fim);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;
                avaliacoes = ToInt(r["avaliacoes"]);
                p1 = ToDouble(r["media_p1"]);
                p2 = ToDouble(r["media_p2"]);
            }

            if (avaliacoes == 0) return null;

            // Mesma estrutura do painel do Grafana: quando `avaliado` está vazio, o ramal
            // avaliado é o do `avaliador`.
            const string porAtendente = @"
SELECT ramal, IFNULL(ag.name,'Não logado') nome, AVG(p2) media, COUNT(*) avaliacoes
FROM (
  SELECT p2, avaliado ramal FROM pesquisa.pesquisa
   WHERE avaliado<>'' AND avaliador<>'' AND data>=@inicio AND data<@fim
  UNION ALL
  SELECT p2, avaliador ramal FROM pesquisa.pesquisa
   WHERE avaliado='' AND avaliador<>'' AND data>=@inicio AND data<@fim
) t
LEFT JOIN call_center.agent ag ON ag.number=t.ramal
GROUP BY ramal, nome ORDER BY media DESC;";
            var linhas = new List<SatisfactionAgentRow>();
            await using (var cmd = new MySqlCommand(porAtendente, conn))
            {
                AddWindow(cmd, inicio, fim);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    linhas.Add(new SatisfactionAgentRow(
                        r.GetString("ramal"), r.GetString("nome"),
                        Math.Round(ToDouble(r["media"]), 2), ToInt(r["avaliacoes"])));
            }

            return new SatisfactionBlock(avaliacoes, Math.Round(p1, 2), Math.Round(p2, 2), linhas);
        }
        catch (MySqlException ex)
        {
            // Banco `pesquisa` ausente ou sem GRANT para o usuário read-only.
            if (!_avisouSemPesquisa)
            {
                _avisouSemPesquisa = true;
                Console.WriteLine(
                    $"[collector] pesquisa de satisfacao indisponivel ({ex.Message}); relatorio segue sem o bloco.");
            }
            return null;
        }
    }

    private static void AddWindow(MySqlCommand cmd, DateTime inicio, DateTime fim)
    {
        cmd.Parameters.Add(new MySqlParameter("@inicio", inicio));
        cmd.Parameters.Add(new MySqlParameter("@fim", fim));
    }

    private static int ToInt(object v) => v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    private static double ToDouble(object v) => v is null || v == DBNull.Value ? 0 : Convert.ToDouble(v);
}
