using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashCall.Collector.Sources;
using DashCall.Contracts;

namespace DashCall.Collector.Net;

/// Mantém uma conexão WebSocket de SAÍDA com o hub, agora FULL-DUPLEX:
///  - ENVIO: consome o stream de snapshots e os envia (envelope Type=snapshot).
///  - RECEPÇÃO: escuta pedidos de relatório (Type=reportRequest), monta via IReportSource
///    e responde (Type=reportResponse) com o MESMO CorrelationId.
/// Reconecta sozinho (ReconnectPolicy). Nunca abre porta de entrada na VPS.
public sealed class HubConnection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Uri _hubUri;      // ex.: wss://hub.exemplo.com/collector/stream
    private readonly string _token;    // segredo por tenant

    public HubConnection(Uri hubUri, string token)
    {
        _hubUri = hubUri;
        _token = token;
    }

    /// Consome a fonte e mantém o canal bidirecional; em falha, reconecta e retoma.
    public async Task RunAsync(
        IAsyncEnumerable<LiveSnapshot> stream, IReportSource reports, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {_token}");

            // Serializa TODOS os sends: o WebSocket não permite dois SendAsync concorrentes.
            using var sendLock = new SemaphoreSlim(1, 1);
            // Encerra ambas as tarefas quando qualquer uma delas terminar (conexão caiu).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                await ws.ConnectAsync(_hubUri, ct);
                attempt = 0;

                var send = PumpSnapshotsAsync(ws, stream, sendLock, linked.Token);
                var recv = PumpRequestsAsync(ws, reports, sendLock, linked.Token);

                // Ao terminar a primeira tarefa, cancela a outra e aguarda ambas.
                await Task.WhenAny(send, recv);
                linked.Cancel();
                try { await Task.WhenAll(send, recv); } catch { /* propaga abaixo se for erro real */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[collector] conexão caiu: {ex.Message}");
            }

            if (ct.IsCancellationRequested) break;
            var delay = ReconnectPolicy.DelayFor(attempt++);
            try { await Task.Delay(delay, ct); } catch { break; }
        }
    }

    /// ENVIO: cada snapshot vira um envelope Type=snapshot.
    private static async Task PumpSnapshotsAsync(
        ClientWebSocket ws, IAsyncEnumerable<LiveSnapshot> stream,
        SemaphoreSlim sendLock, CancellationToken ct)
    {
        await foreach (var snap in stream.WithCancellation(ct))
        {
            var env = new CollectorEnvelope("snapshot", Snapshot: snap);
            await SendAsync(ws, env, sendLock, ct);
        }
    }

    /// RECEPÇÃO: pedidos de relatório -> monta -> responde (mesmo CorrelationId).
    private static async Task PumpRequestsAsync(
        ClientWebSocket ws, IReportSource reports,
        SemaphoreSlim sendLock, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveFullAsync(ws, buffer, ct);
            if (msg is null) break; // Close recebido

            CollectorEnvelope? env;
            try { env = JsonSerializer.Deserialize<CollectorEnvelope>(msg, Json); }
            catch { continue; } // mensagem malformada é ignorada

            if (env is null) continue;

            // Relatório sob demanda (Módulo 2).
            if (env.Type == "reportRequest" && env.ReportRequest is not null)
            {
                var req = env.ReportRequest;
                ReportResult result;
                try
                {
                    var data = await reports.BuildReportAsync(
                        req.Inicio.UtcDateTime, req.Fim.UtcDateTime, ct);
                    result = new ReportResult(req.CorrelationId, data, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    result = new ReportResult(req.CorrelationId, null, ex.Message);
                }

                await SendAsync(ws, new CollectorEnvelope("reportResponse", ReportResponse: result),
                    sendLock, ct);
                continue;
            }

            // Painel do agente (Módulo 3). Só responde se a fonte souber — assim um coletor
            // apontado para a fonte fake não finge ter o módulo.
            if (env.Type == "agentRequest" && env.AgentRequest is not null && reports is IAgentSource agentes)
            {
                var req = env.AgentRequest;
                AgentResult result;
                try
                {
                    if (req.AgentId is null)
                    {
                        var lista = await agentes.ListarAgentesAsync(ct);
                        result = new AgentResult(req.CorrelationId, Agents: lista);
                    }
                    else
                    {
                        var detalhe = await agentes.BuildAgentDetailAsync(req.AgentId.Value, ct);
                        result = detalhe is null
                            // Vínculo órfão: o agente saiu do cadastro do PABX. Código próprio
                            // para a tela poder dizer isso, em vez de "erro ao carregar".
                            ? new AgentResult(req.CorrelationId, Error: AgentErrors.AgenteRemovido)
                            : new AgentResult(req.CorrelationId, Detail: detalhe);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    result = new AgentResult(req.CorrelationId, Error: ex.Message);
                }

                await SendAsync(ws, new CollectorEnvelope("agentResponse", AgentResponse: result),
                    sendLock, ct);
            }
        }
    }

    private static async Task SendAsync(
        ClientWebSocket ws, CollectorEnvelope env, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, Json);
        await sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { sendLock.Release(); }
    }

    /// Lê uma mensagem completa (pode vir fragmentada). Retorna null no Close.
    private static async Task<string?> ReceiveFullAsync(
        ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
