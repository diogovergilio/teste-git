using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Monta um relatório consolidado de um período sob demanda (request/response via hub).
/// Implementação real (MariaDB) e fake (dev/teste) são intercambiáveis.
public interface IReportSource
{
    Task<ReportData> BuildReportAsync(DateTime inicio, DateTime fim, CancellationToken ct);
}
