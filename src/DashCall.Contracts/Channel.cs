namespace DashCall.Contracts;

/// Envelope do canal coletor&lt;-&gt;hub. <see cref="Type"/> discrimina o payload.
/// Compat retroativa: coletores antigos ainda enviam <see cref="LiveSnapshot"/> "bare"
/// (sem envelope). O hub detecta a ausência da propriedade <c>type</c> e trata como snapshot.
public record CollectorEnvelope(
    string Type,                            // "snapshot" | "reportRequest" | "reportResponse"
    LiveSnapshot? Snapshot = null,          // coletor->hub
    ReportRequest? ReportRequest = null,    // hub->coletor
    ReportResult? ReportResponse = null);   // coletor->hub

/// Pedido de relatório sob demanda (hub->coletor). CorrelationId casa a resposta.
public record ReportRequest(string CorrelationId, DateTimeOffset Inicio, DateTimeOffset Fim);

/// Resposta a um pedido de relatório (coletor->hub). Data OU Error preenchido.
public record ReportResult(string CorrelationId, ReportData? Data, string? Error);
