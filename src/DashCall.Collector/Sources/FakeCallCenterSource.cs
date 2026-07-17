using System.Linq;
using System.Runtime.CompilerServices;
using DashCall.Contracts;

namespace DashCall.Collector.Sources;

public sealed class FakeCallCenterSource : ICallCenterSource, IReportSource
{
    private readonly string _tenantId;
    private readonly int _intervalMs;
    private int _tick;

    public FakeCallCenterSource(string tenantId, int intervalMs = 1000)
    {
        _tenantId = tenantId;
        _intervalMs = intervalMs;
    }

    public async IAsyncEnumerable<LiveSnapshot> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return Build(_tick++);
            try { await Task.Delay(_intervalMs, ct); }
            catch (TaskCanceledException) { yield break; }
        }
    }

    private LiveSnapshot Build(int t)
    {
        int waiting = t % 5;
        int onCall = 2 + t % 3;
        int loggedIn = 6;
        var agentsCount = new QueueAgents(loggedIn, loggedIn - onCall - 1, 1, onCall);
        var today = new QueueToday(
            Offered: 100 + t,
            Answered: 90 + t,
            Abandoned: 10,
            SlaPercent: 90 + t % 10,
            TmaSeconds: 175 + t % 20,
            TmeSeconds: 20 + t % 10);
        var queue = new QueueLive("100", "Vendas", waiting, waiting * 12, onCall, agentsCount, today);

        var agents = new List<AgentLive>
        {
            new("A1", "Ana",   AgentState.OnCall,    t % 60, new[] { "100" }),
            new("A2", "Bruno", AgentState.Available, 0,      new[] { "100" }),
            new("A3", "Célia", AgentState.Paused,    0,      new[] { "100" }),
        };

        var operation = new OperationToday(
            Offered: today.Offered,
            Answered: today.Answered,
            Abandoned: today.Abandoned,
            SlaPercent: 90 + t % 8,
            TmaSeconds: 190,
            TmeSeconds: 22,
            TmoSeconds: 190 + 36);

        var ranking = new List<AgentRank>
        {
            new("A-diego",   "Diego Alves",   38 + t % 3, 165),
            new("A-ana",     "Ana Ribeiro",   34,         172),
            new("A-bruno",   "Bruno Costa",   29,         180),
            new("A-felipe",  "Felipe Nunes",  25,         188),
            new("A-eduarda", "Eduarda Lima",  21,         195),
        }.OrderByDescending(r => r.Answered).ToList();

        return new LiveSnapshot(_tenantId, DateTimeOffset.UnixEpoch.AddSeconds(t),
            new[] { queue }, agents, operation, ranking);
    }

    /// Relatório fake DETERMINÍSTICO para o período: números plausíveis derivados dos
    /// dias da janela. Serve para dev/teste do canal request/response (sem banco).
    public Task<ReportData> BuildReportAsync(DateTime inicio, DateTime fim, CancellationToken ct)
    {
        var dias = Math.Max(1, (int)Math.Ceiling((fim - inicio).TotalDays));
        int total = 120 * dias;
        int atendidas = (int)Math.Round(total * 0.82);
        int perdas = total - atendidas;
        double pctAtend = Math.Round(100.0 * atendidas / total, 1);
        double pctPerdas = Math.Round(100.0 * perdas / total, 1);

        var summary = new ReportSummary(
            Total: total, Atendidas: atendidas, Perdas: perdas,
            PercentPerdas: pctPerdas, PercentAtendidas: pctAtend,
            SlaPercent: 91.5, TmaSeconds: 182, TmeSeconds: 24);

        // Distribuição determinística por fila (pesos fixos que somam o total).
        var filas = new[] { ("100", "1-FILA AGENDAR", 0.62), ("200", "2-FILA SUPORTE", 0.26), ("300", "3-FILA VENDAS", 0.12) };
        var queues = new List<ReportQueueRow>();
        foreach (var (id, name, peso) in filas)
        {
            int q = (int)Math.Round(total * peso);
            queues.Add(new ReportQueueRow(id, name, q, Math.Round(100.0 * q / total, 1)));
        }

        // Distribuição determinística por atendente (sobre as atendidas).
        var nomes = new[] { ("A-diego", "Diego Alves", 0.28), ("A-ana", "Ana Ribeiro", 0.24),
                            ("A-bruno", "Bruno Costa", 0.20), ("A-felipe", "Felipe Nunes", 0.16),
                            ("A-eduarda", "Eduarda Lima", 0.12) };
        var agentRows = new List<ReportAgentRow>();
        foreach (var (id, name, peso) in nomes)
        {
            int a = (int)Math.Round(atendidas * peso);
            agentRows.Add(new ReportAgentRow(id, name, a, Math.Round(100.0 * a / atendidas, 1)));
        }

        var data = new ReportData(
            new DateTimeOffset(inicio, TimeSpan.Zero),
            new DateTimeOffset(fim, TimeSpan.Zero),
            summary,
            agentRows.OrderByDescending(a => a.Atendidas).ToList(),
            queues.OrderByDescending(q => q.Quantidade).ToList());

        return Task.FromResult(data);
    }
}
