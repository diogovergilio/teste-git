using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Gravações sob demanda (Módulo 6). Intercambiável (real MariaDB / fake), como as outras fontes.
public interface IRecordingSource
{
    Task<(IReadOnlyList<RecordingRow> Rows, int Total)> ListarGravacoesAsync(
        RecordingListRequest req, CancellationToken ct);

    Task<RecordingDownloadResult> BaixarGravacaoAsync(
        RecordingDownloadRequest req, CancellationToken ct);
}
