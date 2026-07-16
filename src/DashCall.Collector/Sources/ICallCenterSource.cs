using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Fornece um fluxo contínuo de snapshots do call center.
/// Implementação fake (dev) e real (MariaDB+AMI) são intercambiáveis.
public interface ICallCenterSource
{
    IAsyncEnumerable<LiveSnapshot> StreamAsync(CancellationToken ct);
}
