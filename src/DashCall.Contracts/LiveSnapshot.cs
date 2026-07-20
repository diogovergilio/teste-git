namespace DashCall.Contracts;

/// Estado completo de um call center num instante. É o que o coletor envia ao hub.
public record LiveSnapshot(
    string TenantId,
    DateTimeOffset Timestamp,
    IReadOnlyList<QueueLive> Queues,
    IReadOnlyList<AgentLive> Agents,
    OperationToday? Operation = null,
    IReadOnlyList<AgentRank>? Ranking = null);

/// Consolidado da operação hoje (global). Calculado pelo coletor com as MESMAS queries globais
/// do Grafana (paridade). TmoSeconds é opcional (depende de existir pós-atendimento no schema).
public record OperationToday(
    int Offered,
    int Answered,
    int Abandoned,
    double SlaPercent,
    int TmaSeconds,
    int TmeSeconds,
    int? TmoSeconds);

/// Linha do ranking de agentes (hoje) — do painel "Ranking" do Grafana.
public record AgentRank(string AgentId, string Name, int Answered, int TmaSeconds);

public record QueueLive(
    string Id,
    string Name,
    int CallsWaiting,
    int LongestWaitSeconds,
    int CallsInProgress,
    QueueAgents Agents,
    QueueToday Today);

/// Contagem de agentes por estado numa fila (foto atual).
public record QueueAgents(int LoggedIn, int Available, int Paused, int OnCall);

/// Contadores acumulados do dia para a fila (calculados pelo coletor a partir do queue_log).
public record QueueToday(
    int Offered,
    int Answered,
    int Abandoned,
    double SlaPercent,
    int TmaSeconds,   // tempo médio de atendimento (conversa)
    int TmeSeconds);  // tempo médio de espera

public enum AgentState { Offline, Available, OnCall, Paused }

/// <param name="CurrentCallSeconds">Duração da ligação em curso (0 quando não está falando).</param>
/// <param name="BreakName">Motivo da pausa (ex.: "Almoço"); null fora de pausa.</param>
/// <param name="BreakSeconds">Há quanto tempo está em pausa. Opcionais no fim do record para
/// manter compatibilidade: um hub novo continua lendo snapshot de coletor antigo.</param>
public record AgentLive(
    string Id,
    string Name,
    AgentState State,
    int CurrentCallSeconds,
    IReadOnlyList<string> QueueIds,
    string? BreakName = null,
    int BreakSeconds = 0);
