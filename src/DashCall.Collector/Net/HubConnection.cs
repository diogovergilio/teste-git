using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashCall.Contracts;

namespace DashCall.Collector.Net;

/// Mantém uma conexão WebSocket de SAÍDA com o hub e envia snapshots.
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

    /// Consome a fonte e envia cada snapshot; em falha, reconecta e retoma.
    public async Task RunAsync(IAsyncEnumerable<LiveSnapshot> stream, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
            try
            {
                await ws.ConnectAsync(_hubUri, ct);
                attempt = 0;
                await foreach (var snap in stream.WithCancellation(ct))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(snap, Json);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[collector] conexão caiu: {ex.Message}");
                var delay = ReconnectPolicy.DelayFor(attempt++);
                try { await Task.Delay(delay, ct); } catch { break; }
            }
        }
    }
}
