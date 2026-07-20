using System.Security.Cryptography;
using System.Text;

namespace DashCall.Collector.Eccp;

/// Partes puras do protocolo ECCP (sem I/O): hash do agente, montagem de requests, leitura de
/// campos. Isoladas para se provar sem um daemon rodando.
public static class EccpProtocol
{
    /// agent_hash = md5(app_cookie + agent_number + eccp_password), concatenação SEM separador.
    /// agent_number vai COM prefixo ("Agent/9000"). Confirmado no daemon (`ECCPConn` _hashValidoAgenteECCP)
    /// e na spec (Protocolo ECCP.txt).
    public static string AgentHash(string appCookie, string agentNumber, string eccpPassword)
        => Md5Hex(appCookie + agentNumber + eccpPassword);

    /// md5 hex minúsculo — mesmo formato do PHP md5().
    public static string Md5Hex(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }

    /// Escapa só o '&' → '&amp;' nos valores, como o cliente PHP (SimpleXMLElement não escapa
    /// <, >, aspas — assume-se que os valores não os contêm).
    public static string Esc(string v) => v.Replace("&", "&amp;");

    // ---- Montagem dos requests (o `id` é do chamador, incremental por conexão) ----

    public static string Login(int id, string username, string password) =>
        $"<request id=\"{id}\"><login><username>{Esc(username)}</username>" +
        $"<password>{Esc(password)}</password></login></request>";

    /// loginagent SEM <password> (o daemon o ignora) e SEM <timeout> (stateless: agente fica logado
    /// sem depender do socket). number/extension são só dígitos.
    public static string LoginAgent(int id, string number, string hash, string extension) =>
        $"<request id=\"{id}\"><loginagent><agent_number>{Esc(number)}</agent_number>" +
        $"<agent_hash>{hash}</agent_hash><extension>{Esc(extension)}</extension></loginagent></request>";

    public static string LogoutAgent(int id, string agentNumber, string hash) =>
        $"<request id=\"{id}\"><logoutagent><agent_number>{Esc(agentNumber)}</agent_number>" +
        $"<agent_hash>{hash}</agent_hash></logoutagent></request>";

    public static string PauseAgent(int id, string agentNumber, string hash, int breakId) =>
        $"<request id=\"{id}\"><pauseagent><agent_number>{Esc(agentNumber)}</agent_number>" +
        $"<agent_hash>{hash}</agent_hash><pause_type>{breakId}</pause_type></pauseagent></request>";

    public static string UnpauseAgent(int id, string agentNumber, string hash) =>
        $"<request id=\"{id}\"><unpauseagent><agent_number>{Esc(agentNumber)}</agent_number>" +
        $"<agent_hash>{hash}</agent_hash></unpauseagent></request>";

    // ---- Leitura de respostas/eventos ----

    /// Texto de um elemento simples `<tag>valor</tag>` no documento (primeiro match), ou null.
    public static string? Campo(string xml, string tag)
    {
        var abre = $"<{tag}>";
        int i = xml.IndexOf(abre, StringComparison.Ordinal);
        if (i < 0) return null;
        i += abre.Length;
        int f = xml.IndexOf($"</{tag}>", i, StringComparison.Ordinal);
        return f < 0 ? null : xml[i..f];
    }

    /// True se o documento é uma resposta de falha `<failure>...`.
    public static bool EhFalha(string xml) => xml.Contains("<failure", StringComparison.Ordinal);

    /// (code, message) de um `<failure>`. code 0 se ausente.
    public static (int Code, string Message) Falha(string xml)
    {
        int.TryParse(Campo(xml, "code"), out var code);
        return (code, Campo(xml, "message") ?? "erro desconhecido");
    }

    /// Nome do primeiro filho de `<event>` (ex.: "agentloggedin"). null se não é evento.
    public static string? NomeEvento(string xml)
    {
        int ev = xml.IndexOf("<event", StringComparison.Ordinal);
        if (ev < 0) return null;
        int lt = xml.IndexOf('<', xml.IndexOf('>', ev) + 1);
        if (lt < 0) return null;
        int fim = lt + 1;
        while (fim < xml.Length && xml[fim] is not ('>' or ' ' or '/')) fim++;
        return xml[(lt + 1)..fim];
    }
}
