namespace DashCall.Contracts;

/// Ação de controle do agente no PABX (Módulo 7 — ESCRITA via ECCP).
public enum AgentAction { Login, Logout, Pause, Unpause }

/// Estado do agente após a ação (o que a tela mostra).
public enum AgentControlState { LoggedOut, Logging, LoggedIn, Paused, Failed }

/// Pedido de controle (hub->coletor).
///
/// SEGURANÇA: AgentId e a senha ECCP são resolvidos pelo HUB a partir do JWT (Usuario.AgentRefId),
/// NUNCA vêm do navegador — a atendente só aciona o próprio agente, do próprio tenant.
/// BreakId só é usado em Pause (id da tabela `break`).
public record AgentActionRequest(
    string CorrelationId,
    int AgentId,
    AgentAction Acao,
    int? BreakId = null);

/// Resposta ao controle (coletor->hub).
/// Estado após a ação; Erro preenchido em falha (código/mensagem do daemon).
public record AgentActionResult(
    string CorrelationId,
    AgentControlState Estado,
    string? Erro = null);
