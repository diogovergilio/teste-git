namespace DashCall.Contracts;

/// Uma linha do cadastro de agentes do cliente, para o supervisor escolher ao criar o login.
/// <paramref name="Id"/> é a chave primária de `call_center.agent` — é por ela que o vínculo é
/// feito, NUNCA pelo <paramref name="Number"/>: o ramal é reciclado entre pessoas (o 12 já foi da
/// Andreia e hoje é da Giselly), e vincular por número faria uma ver o dia da outra sem erro nenhum.
public record AgentSummary(int Id, string Number, string Name, string Type, bool Ativo);

/// Um dia da série do agente (evolução da semana).
public record AgentDay(DateOnly Dia, int Atendidas);

/// Números do agente hoje.
public record AgentToday(
    int Atendidas,
    int TmaSeconds,
    int LogadoSeconds,
    int PausaSeconds,
    int? PosicaoRanking);

/// Painel individual do agente (somente leitura).
/// <paramref name="Satisfacao"/> é null quando o cliente não tem pesquisa — mesma regra da folha 2.
public record AgentDetail(
    int Id,
    string Number,
    string Name,
    AgentState State,
    int CurrentCallSeconds,
    string? BreakName,
    int BreakSeconds,
    AgentToday Hoje,
    IReadOnlyList<AgentDay> Semana,
    AgentSatisfaction? Satisfacao);

/// Nota do agente na pesquisa de satisfação, no período da série.
public record AgentSatisfaction(double Media, int Avaliacoes);
