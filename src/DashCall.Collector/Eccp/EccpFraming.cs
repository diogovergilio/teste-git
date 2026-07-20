using System.Text;

namespace DashCall.Collector.Eccp;

/// Enquadramento de mensagens ECCP: o stream TCP é uma CONCATENAÇÃO de documentos XML
/// (`<response>...` e `<event>...`) SEM delimitador de tamanho nem separador — só whitespace
/// eventual entre eles. O daemon original conta profundidade de elemento e corta quando volta a 0
/// (`ECCP.class.php` xmlStart/EndHandler). Reproduzido aqui como acumulador testável.
///
/// Alimenta bytes com <see cref="Feed"/>; cada documento completo sai por <see cref="TryReadDocument"/>.
public sealed class EccpFraming
{
    private readonly StringBuilder _buffer = new();

    public void Feed(string chunk) => _buffer.Append(chunk);

    /// Extrai o próximo documento XML de topo, ou null se ainda não há um completo.
    /// Conta `<tag ...>` como +1 e `</tag>`/`<tag/>` como neutro/-1, ignorando o prólogo,
    /// comentários e o conteúdo de atributos/texto — sem depender de um XmlReader (que exigiria
    /// um documento único e completo de antemão).
    public string? TryReadDocument()
    {
        var s = _buffer.ToString();
        int i = 0, depth = 0;
        bool viuRaiz = false;

        while (i < s.Length)
        {
            int lt = s.IndexOf('<', i);
            if (lt < 0) break;

            // Prólogo <?xml ... ?> e comentários <!-- --> não contam.
            if (Match(s, lt, "<?"))
            {
                int end = s.IndexOf("?>", lt, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 2;
                continue;
            }
            if (Match(s, lt, "<!--"))
            {
                int end = s.IndexOf("-->", lt, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 3;
                continue;
            }

            int gt = FecharTag(s, lt);
            if (gt < 0) break; // tag incompleta — espera mais bytes

            bool fechamento = s[lt + 1] == '/';
            bool autoFechado = s[gt - 1] == '/';

            if (fechamento)
            {
                depth--;
            }
            else
            {
                viuRaiz = true;
                if (!autoFechado) depth++;
            }

            i = gt + 1;

            // Documento de topo completo: profundidade voltou a 0 depois de ter aberto a raiz.
            if (viuRaiz && depth == 0)
            {
                var doc = s[..i];
                _buffer.Clear();
                _buffer.Append(s[i..].TrimStart());
                return doc;
            }
        }

        return null;
    }

    /// Índice do '>' que fecha a tag iniciada em <paramref name="lt"/>, pulando '>' dentro de
    /// aspas de atributo. -1 se a tag ainda não terminou no buffer.
    private static int FecharTag(string s, int lt)
    {
        char aspas = '\0';
        for (int j = lt + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (aspas != '\0')
            {
                if (c == aspas) aspas = '\0';
            }
            else if (c is '"' or '\'')
            {
                aspas = c;
            }
            else if (c == '>')
            {
                return j;
            }
        }
        return -1;
    }

    private static bool Match(string s, int pos, string token) =>
        pos + token.Length <= s.Length && s.AsSpan(pos, token.Length).SequenceEqual(token);
}
