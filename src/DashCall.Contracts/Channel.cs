namespace DashCall.Contracts;

/// Envelope do canal coletor&lt;-&gt;hub. <see cref="Type"/> discrimina o payload.
/// Compat retroativa: coletores antigos ainda enviam <see cref="LiveSnapshot"/> "bare"
/// (sem envelope). O hub detecta a ausência da propriedade <c>type</c> e trata como snapshot.
public record CollectorEnvelope(
    string Type,                            // "snapshot" | "reportRequest" | "reportResponse"
                                            // | "agentRequest" | "agentResponse"
                                            // | "recordingList*" | "recordingDownload*"
    LiveSnapshot? Snapshot = null,          // coletor->hub
    ReportRequest? ReportRequest = null,    // hub->coletor
    ReportResult? ReportResponse = null,    // coletor->hub
    AgentRequest? AgentRequest = null,      // hub->coletor
    AgentResult? AgentResponse = null,      // coletor->hub
    RecordingListRequest? RecordingListRequest = null,        // hub->coletor
    RecordingListResult? RecordingListResponse = null,        // coletor->hub
    RecordingDownloadRequest? RecordingDownloadRequest = null, // hub->coletor
    RecordingDownloadResult? RecordingDownloadResponse = null); // coletor->hub

/// Pedido de relatório sob demanda (hub->coletor). CorrelationId casa a resposta.
public record ReportRequest(string CorrelationId, DateTimeOffset Inicio, DateTimeOffset Fim);

/// Resposta a um pedido de relatório (coletor->hub). Data OU Error preenchido.
public record ReportResult(string CorrelationId, ReportData? Data, string? Error);

/// Pedido do Módulo 3 (hub->coletor).
/// <paramref name="AgentId"/> null → lista o cadastro (combo do supervisor);
/// preenchido → painel daquele agente.
///
/// SEGURANÇA: quem preenche o AgentId é o HUB, a partir do `Usuario.AgentRefId` resolvido pelo JWT.
/// Nunca o navegador — senão um agente trocaria o id e leria o dia de outra pessoa.
public record AgentRequest(string CorrelationId, int? AgentId);

/// Resposta ao pedido do Módulo 3 (coletor->hub). Um dos três preenchido.
public record AgentResult(
    string CorrelationId,
    IReadOnlyList<AgentSummary>? Agents = null,
    AgentDetail? Detail = null,
    string? Error = null);

/// Erros do Módulo 3 que a interface trata de forma específica.
public static class AgentErrors
{
    /// O vínculo existe no hub, mas o agente sumiu do cadastro do PABX (removido no Issabel).
    ///
    /// Tem código próprio porque a tela precisa dizer "este agente não está mais no cadastro do
    /// PABX — refaça o vínculo", com a ação concreta. Um erro genérico (ou um 404 cru) faria o
    /// supervisor achar que o problema é no DASH-CALL e abrir chamado do lado errado.
    public const string AgenteRemovido = "AGENTE_REMOVIDO";
}
