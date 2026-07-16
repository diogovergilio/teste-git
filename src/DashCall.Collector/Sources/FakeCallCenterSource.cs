using System.Linq;
using System.Runtime.CompilerServices;
using DashCall.Contracts;

namespace DashCall.Collector.Sources;

public sealed class FakeCallCenterSource : ICallCenterSource
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
}
