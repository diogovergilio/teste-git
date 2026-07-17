namespace DashCall.Contracts;

/// Relatório consolidado de um período [Inicio, Fim). Calculado pelo coletor com as MESMAS
/// queries de paridade do Grafana, apenas com a janela de datas parametrizada.
public record ReportSummary(
    int Total, int Atendidas, int Perdas,
    double PercentPerdas, double PercentAtendidas,
    double SlaPercent, int TmaSeconds, int TmeSeconds);

/// Linha do relatório por atendente (todas as chamadas, sem filtro de tipo).
public record ReportAgentRow(string AgentId, string Name, int Atendidas, double Percent);

/// Linha do relatório por fila/convênio.
public record ReportQueueRow(string QueueId, string Name, int Quantidade, double Percent);

/// Relatório completo de um período.
public record ReportData(
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    ReportSummary Summary,
    IReadOnlyList<ReportAgentRow> Agents,
    IReadOnlyList<ReportQueueRow> Queues);
