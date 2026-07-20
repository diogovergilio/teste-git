using DashCall.Contracts;

namespace DashCall.Collector.Sources;

/// Controle do agente no PABX (Módulo 7 — ESCRITA via ECCP). Só a fonte real MariaDB implementa;
/// a fake não (um coletor de teste não finge comandar o PABX).
public interface IAgentControlSource
{
    Task<AgentActionResult> ControlarAgenteAsync(AgentActionRequest req, CancellationToken ct);
}
