using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Dados do Módulo 3 (painel individual do agente), sob demanda pelo canal bidirecional.
/// Implementação real (MariaDB) e fake (dev/teste) são intercambiáveis, como em IReportSource.
public interface IAgentSource
{
    /// Cadastro de agentes do cliente — alimenta o combo do supervisor.
    Task<IReadOnlyList<AgentSummary>> ListarAgentesAsync(CancellationToken ct);

    /// Painel de um agente. Null quando o id não existe mais no cadastro do PABX
    /// (vínculo órfão) — o hub traduz isso em AgentErrors.AgenteRemovido.
    Task<AgentDetail?> BuildAgentDetailAsync(int agentId, CancellationToken ct);
}
