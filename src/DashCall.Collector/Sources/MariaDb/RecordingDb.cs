using System.Text;
using DashCall.Contracts;
using MySqlConnector;

namespace DashCall.Collector.Sources.MariaDb;

/// Acesso READ-ONLY às gravações (Módulo 6). Arquivo próprio, como os outros Db.
///
/// Fonte: `call_recording`, enriquecida por FK INDEXADA — `id_call_incoming` → `call_entry`
/// (entrada) e `id_call_outgoing` → `calls` (saída). Isso é melhor que o join por `uniqueid` do
/// plano inicial, que não tem índice em nenhum dos lados, e dá a direção de brinde.
///
/// LEITURA DE ARQUIVO: o coletor abre o `.wav49` do disco. Todo caminho passa por
/// <see cref="RecordingPath.ResolverSeguro"/> — o `recordingfile` vem do banco do cliente e não
/// é confiável.
public sealed class RecordingDb
{
    private readonly string _connectionString;
    private readonly string _baseDir;
    private readonly int _retencaoDias;
    private readonly long _maxBytes;

    /// <param name="baseDir">Pasta das gravações na VPS (ex.: /var/spool/asterisk/monitor).</param>
    /// <param name="retencaoDias">Janela em que o ARQUIVO existe no disco (default 30).</param>
    /// <param name="maxBytes">Teto do arquivo no canal (default 20 MB).</param>
    public RecordingDb(string connectionString, string baseDir,
        int retencaoDias = 30, long maxBytes = 20L * 1024 * 1024)
    {
        _connectionString = connectionString;
        _baseDir = baseDir;
        _retencaoDias = retencaoDias;
        _maxBytes = maxBytes;
    }

    private sealed record Row(int Id, DateTime Quando, string Callerid, string? Fila,
        string? Agente, int? Duracao, int? Espera, string Direcao);

    /// Lista paginada com filtros. Devolve as linhas da página + o total do filtro (para o supervisor
    /// saber quantas existem). Disponibilidade do arquivo derivada da data — não toca o disco.
    public async Task<(IReadOnlyList<RecordingRow> Rows, int Total)> ListarAsync(
        DateTime inicio, DateTime fim, string? fila, int? agentId, string? callerid,
        int pagina, int porPagina, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (where, parametros) = MontarFiltro(inicio, fim, fila, agentId, callerid);

        int total;
        await using (var cmd = new MySqlCommand(
            $"SELECT COUNT(*) FROM call_recording r " +
            "LEFT JOIN call_entry ce ON ce.id=r.id_call_incoming " +
            "LEFT JOIN calls ca ON ca.id=r.id_call_outgoing " + where, conn))
        {
            foreach (var p in parametros) cmd.Parameters.Add((MySqlParameter)p.Clone());
            total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        const string sql = @"
SELECT r.id, r.datetime_entry,
  COALESCE(ce.callerid, ca.phone, '') callerid,
  qc.descr fila,
  ag.name agente,
  COALESCE(ce.duration, ca.duration) duracao,
  COALESCE(ce.duration_wait, ca.duration_wait) espera,
  CASE WHEN r.id_call_incoming IS NOT NULL THEN 'entrada' ELSE 'saida' END direcao
FROM call_recording r
LEFT JOIN call_entry ce ON ce.id=r.id_call_incoming
LEFT JOIN calls ca ON ca.id=r.id_call_outgoing
LEFT JOIN queue_call_entry qce ON qce.id=ce.id_queue_call_entry
LEFT JOIN asterisk.queues_config qc ON qc.extension=qce.queue
LEFT JOIN agent ag ON ag.id=ce.id_agent
";
        var rows = new List<Row>();
        await using (var cmd = new MySqlCommand(
            sql + where + " ORDER BY r.datetime_entry DESC LIMIT @limit OFFSET @offset", conn))
        {
            foreach (var p in parametros) cmd.Parameters.Add((MySqlParameter)p.Clone());
            cmd.Parameters.AddWithValue("@limit", Math.Clamp(porPagina, 1, 200));
            cmd.Parameters.AddWithValue("@offset", Math.Max(0, (pagina - 1) * porPagina));

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new Row(
                    Convert.ToInt32(r["id"]),
                    r.GetDateTime("datetime_entry"),
                    r["callerid"]?.ToString() ?? "",
                    r["fila"] as string,
                    r["agente"] as string,
                    r["duracao"] == DBNull.Value ? null : Convert.ToInt32(r["duracao"]),
                    r["espera"] == DBNull.Value ? null : Convert.ToInt32(r["espera"]),
                    r.GetString("direcao")));
        }

        // Disponibilidade derivada da data: dentro da retenção → arquivo no disco. O download
        // continua sendo a verdade se a inferência errar por um dia.
        var corte = DateTime.Today.AddDays(-_retencaoDias);
        var resultado = rows.Select(x => new RecordingRow(
            x.Id, new DateTimeOffset(x.Quando, TimeSpan.Zero), x.Callerid, x.Fila, x.Agente,
            x.Duracao, x.Espera, x.Direcao, Disponivel: x.Quando >= corte)).ToList();

        return (resultado, total);
    }

    /// Lê o binário de UMA gravação, validando o caminho. Erros são códigos de RecordingErrors,
    /// não exceção — a tela os trata de forma específica.
    public async Task<RecordingDownloadResult> BaixarAsync(
        string correlationId, int id, CancellationToken ct)
    {
        string? recordingFile;
        await using (var conn = new MySqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(
                "SELECT recordingfile FROM call_recording WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            recordingFile = (await cmd.ExecuteScalarAsync(ct)) as string;
        }

        if (string.IsNullOrWhiteSpace(recordingFile))
            return new RecordingDownloadResult(correlationId, Erro: RecordingErrors.Expirada);

        var caminhoBanco = RecordingPath.ResolverSeguro(_baseDir, recordingFile);
        if (caminhoBanco is null)
            return new RecordingDownloadResult(correlationId, Erro: RecordingErrors.CaminhoInvalido);

        var caminho = EncontrarArquivoReal(caminhoBanco);
        if (caminho is null)
            // Registro existe, arquivo não — expirou entre listar e clicar.
            return new RecordingDownloadResult(correlationId, Erro: RecordingErrors.Expirada);

        var info = new FileInfo(caminho);
        if (info.Length > _maxBytes)
            return new RecordingDownloadResult(correlationId, Erro: RecordingErrors.ArquivoGrande);

        var bytes = await File.ReadAllBytesAsync(caminho, ct);
        return new RecordingDownloadResult(correlationId, Filename: Path.GetFileName(caminho), Conteudo: bytes);
    }

    /// Acha o arquivo real a partir do caminho vindo do banco, tolerando a peculiaridade do Issabel:
    /// a coluna guarda ".wav49" (minúsculo, com sufixo numérico), mas o arquivo no disco é ".WAV"
    /// (maiúsculo, sem sufixo). Procura pelo MESMO nome-base + qualquer extensão.
    ///
    /// Seguro: o diretório vem do caminho já validado por ResolverSeguro, e EnumerateFiles só
    /// devolve arquivos DENTRO dele — não há como escapar da pasta de gravações.
    private static string? EncontrarArquivoReal(string caminhoValidado)
    {
        // Caso literal — alguns clientes podem gravar exatamente o que está no banco.
        if (File.Exists(caminhoValidado)) return caminhoValidado;

        var dir = Path.GetDirectoryName(caminhoValidado);
        if (dir is null || !Directory.Exists(dir)) return null;

        // "q-1-...286.wav49" → base "q-1-...286"; procura "q-1-...286.*".
        var semExt = Path.GetFileNameWithoutExtension(caminhoValidado);
        return Directory.EnumerateFiles(dir, semExt + ".*").FirstOrDefault();
    }

    /// Monta o WHERE e os parâmetros compartilhados entre a contagem e a página.
    /// Parâmetros clonados por comando porque o MySqlConnector não deixa reusar a mesma instância.
    private static (string Where, List<MySqlParameter> Parametros) MontarFiltro(
        DateTime inicio, DateTime fim, string? fila, int? agentId, string? callerid)
    {
        var sb = new StringBuilder("WHERE r.datetime_entry>=@inicio AND r.datetime_entry<@fim");
        var ps = new List<MySqlParameter>
        {
            new("@inicio", inicio),
            new("@fim", fim),
        };

        if (!string.IsNullOrWhiteSpace(fila))
        {
            sb.Append(" AND ce.id_queue_call_entry IN " +
                "(SELECT id FROM queue_call_entry WHERE queue=@fila)");
            ps.Add(new MySqlParameter("@fila", fila));
        }
        if (agentId is > 0)
        {
            sb.Append(" AND ce.id_agent=@agent");
            ps.Add(new MySqlParameter("@agent", agentId.Value));
        }
        if (!string.IsNullOrWhiteSpace(callerid))
        {
            sb.Append(" AND COALESCE(ce.callerid, ca.phone) LIKE @cid");
            ps.Add(new MySqlParameter("@cid", $"%{callerid.Trim()}%"));
        }

        return (sb.ToString(), ps);
    }
}
