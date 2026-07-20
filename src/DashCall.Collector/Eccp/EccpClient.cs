using System.Net.Sockets;
using System.Text;

namespace DashCall.Collector.Eccp;

/// Cliente ECCP: uma conexão TCP efêmera ao daemon (porta 20005). Conecta, autentica a conexão,
/// executa comandos e fecha. Sem estado entre chamadas — o agente fica logado no Asterisk
/// independente deste socket (confirmado no daemon: sem `<timeout>`, não há inatividade).
///
/// Uso:
///   await using var c = await EccpClient.ConnectAsync(host, user, pass, ct);
///   var resp = await c.RequestAsync(EccpProtocol.PauseAgent(...), ct);
public sealed class EccpClient : IAsyncDisposable
{
    public const int DefaultPort = 20005;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly EccpFraming _framing = new();
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _requestId;

    /// app_cookie devolvido no login da conexão — necessário para o agent_hash.
    public string AppCookie { get; private set; } = "";

    private EccpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// Conecta e autentica a conexão (nível aplicação, contra eccp_authorized_clients).
    /// A senha vai como md5 se já for 32-hex, senão em claro (o daemon aceita os dois).
    public static async Task<EccpClient> ConnectAsync(
        string host, int port, string username, string password, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        var c = new EccpClient(tcp);

        var resp = await c.RequestAsync(EccpProtocol.Login(c.NextId(), username, password), ct);
        if (EccpProtocol.EhFalha(resp))
        {
            var (code, msg) = EccpProtocol.Falha(resp);
            await c.DisposeAsync();
            throw new EccpException(code, $"login ECCP: {msg}");
        }
        c.AppCookie = EccpProtocol.Campo(resp, "app_cookie")
            ?? throw new EccpException(0, "login ECCP sem app_cookie");
        return c;
    }

    public int NextId() => ++_requestId;

    /// Envia um request e aguarda a próxima RESPONSE (eventos que chegarem antes vão para
    /// <see cref="EventosPendentes"/>). Timeout via <paramref name="ct"/>.
    public async Task<string> RequestAsync(string requestXml, CancellationToken ct)
    {
        await EnviarAsync(requestXml, ct);
        while (true)
        {
            var doc = await ProximoDocumentoAsync(ct);
            if (EccpProtocol.NomeEvento(doc) is not null)
            {
                EventosPendentes.Add(doc); // evento assíncrono: guarda e segue esperando a response
                continue;
            }
            return doc; // é <response>
        }
    }

    /// Aguarda um EVENTO específico (por nome) até o timeout. Usado no login Agent/, onde o
    /// resultado real vem por `agentloggedin`/`agentfailedlogin`. Devolve o doc ou null no timeout.
    public async Task<string?> AguardarEventoAsync(
        IReadOnlyCollection<string> nomes, CancellationToken ct)
    {
        // Primeiro, algum já veio junto com a response?
        foreach (var e in EventosPendentes)
            if (nomes.Contains(EccpProtocol.NomeEvento(e) ?? "")) return e;

        try
        {
            while (true)
            {
                var doc = await ProximoDocumentoAsync(ct);
                var nome = EccpProtocol.NomeEvento(doc);
                if (nome is not null && nomes.Contains(nome)) return doc;
                // outros eventos/responses no meio: ignora (não esperamos response aqui)
            }
        }
        catch (OperationCanceledException)
        {
            return null; // timeout — no login, vira "logging" (verifique o telefone)
        }
        catch (EccpException)
        {
            // Conexão caiu enquanto esperávamos o evento: tratamos como "não confirmado" (o login
            // pode ter se completado no Asterisk mesmo assim), não como erro que estoura a tela.
            return null;
        }
    }

    public List<string> EventosPendentes { get; } = [];

    private async Task EnviarAsync(string xml, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task<string> ProximoDocumentoAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_framing.TryReadDocument() is { } doc) return doc;
            int n = await _stream.ReadAsync(_buf, ct);
            if (n == 0) throw new EccpException(0, "conexão ECCP encerrada pelo daemon");
            _framing.Feed(Encoding.UTF8.GetString(_buf, 0, n));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* já caiu */ }
        _tcp.Dispose();
        await ValueTask.CompletedTask;
    }
}

/// Falha reportada pelo daemon (`<failure><code/><message/></failure>`) ou de transporte.
public sealed class EccpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
