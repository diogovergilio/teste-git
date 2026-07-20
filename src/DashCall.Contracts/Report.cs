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

// ---- Folha 2 "Análise" ------------------------------------------------------
// Blocos analíticos do relatório. Ver docs/superpowers/specs/2026-07-20-folha2-analise-design.md.

/// Célula do mapa de calor. <paramref name="DayOfWeek"/> segue o DAYOFWEEK() do MySQL
/// (1=domingo … 7=sábado) e <paramref name="Hour"/> vai de 0 a 23.
public record HeatCell(int DayOfWeek, int Hour, int Total, int Abandonadas);

/// Número que mais retornou dentro da janela de rechamada (pista operacional).
public record RepeatCaller(string Callerid, int Ligacoes);

/// Resolvido no 1º contato: das ligações ATENDIDAS, quantas não tiveram nova ligação
/// do mesmo número dentro da janela (24h).
public record FcrBlock(
    int Atendidas, int PrimeiroContato, double Percent, IReadOnlyList<RepeatCaller> TopRetornos);

/// Faixa de tempo de espera das ligações abandonadas (ex.: "31-60s").
public record AbandonBand(string Label, int Quantidade, double Percent);

/// Nota média por atendente na pesquisa de satisfação.
public record SatisfactionAgentRow(string Ramal, string Nome, double Media, int Avaliacoes);

/// Pesquisa de satisfação (banco `pesquisa`, separado do call_center).
public record SatisfactionBlock(
    int Avaliacoes, double MediaP1, double MediaP2, IReadOnlyList<SatisfactionAgentRow> PorAtendente);

/// Bloco analítico (folha 2 do PDF).
/// <paramref name="Satisfaction"/> é null quando o cliente não tem avaliações no período
/// (ou o coletor não tem GRANT no banco `pesquisa`) — o bloco simplesmente não é exibido.
public record ReportAnalysis(
    IReadOnlyList<HeatCell> Heat,
    FcrBlock Fcr,
    IReadOnlyList<AbandonBand> AbandonBands,
    SatisfactionBlock? Satisfaction);

/// Relatório completo de um período.
/// <paramref name="Analysis"/> é OPCIONAL de propósito: um hub novo continua funcionando
/// com um coletor antigo, que ainda não envia a folha 2 (mesma compatibilidade retroativa
/// aplicada ao LiveSnapshot).
public record ReportData(
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    ReportSummary Summary,
    IReadOnlyList<ReportAgentRow> Agents,
    IReadOnlyList<ReportQueueRow> Queues,
    ReportAnalysis? Analysis = null);
