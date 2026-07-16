using System.Runtime.CompilerServices;
using DashCall.Collector.Sources.MariaDb;
using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Fonte REAL de dados: poller READ-ONLY do MariaDB do call center.
/// A cada <c>intervalMs</c> monta um <see cref="LiveSnapshot"/> (via <see cref="CallCenterDb"/>)
/// com paridade validada às queries do Grafana, e o emite no stream.
///
/// Resiliência: se uma rodada falhar (erro de query/conexão), loga em Console.Error e CONTINUA —
/// reemite o último snapshot bom se houver, ou pula a rodada. Nunca derruba o stream.
public sealed class MariaDbCallCenterSource : ICallCenterSource
{
    private readonly CallCenterDb _db;
    private readonly string _tenantId;
    private readonly int _intervalMs;

    public MariaDbCallCenterSource(string connectionString, string tenantId, int intervalMs = 2000)
    {
        _db = new CallCenterDb(connectionString);
        _tenantId = tenantId;
        _intervalMs = intervalMs;
    }

    public async IAsyncEnumerable<LiveSnapshot> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        LiveSnapshot? last = null;
        while (!ct.IsCancellationRequested)
        {
            LiveSnapshot? snapshot = null;
            try
            {
                // Produção = "hoje": since=null faz a SQL usar CURDATE().
                snapshot = await _db.BuildSnapshotAsync(_tenantId, since: null, ct);
                last = snapshot;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[mariadb] falha ao montar snapshot: {ex.Message}");
                snapshot = last; // reemite o último bom (ou null → pula a rodada)
            }

            if (snapshot is not null)
                yield return snapshot;

            try { await Task.Delay(_intervalMs, ct); }
            catch (TaskCanceledException) { yield break; }
        }
    }
}
