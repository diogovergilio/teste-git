namespace DashCall.Collector.Sources.MariaDb;

/// Uma linha do cadastro `call_center.agent`.
public sealed record AgentRow(int Id, string Number, string Name, string Type, string Estatus);

/// Traduz o canal do Asterisk gravado na pesquisa (`Agent/24`) para o nome do atendente.
///
/// Por que não é um simples JOIN em SQL:
///  - `pesquisa.avaliado` guarda o CANAL (`Agent/24`), não o ramal — `number='Agent/24'` nunca casa.
///  - O mesmo `number` aparece VÁRIAS vezes em `agent`: cadastros duplicados da mesma pessoa e,
///    pior, ramais que trocaram de dono (ex.: 12 já foi da Andreia e hoje é da Giselly).
///  - Os dois bancos têm collation diferente (`pesquisa` é latin1, `call_center` é utf8), o que
///    torna comparação em SQL um campo minado. Aqui isso não existe.
///
/// LIMITAÇÃO ASSUMIDA: o schema não guarda desde quando cada pessoa ocupa o ramal, então uma
/// avaliação antiga é indistinguível de uma recente. A nota é atribuída ao ocupante ATIVO —
/// e a tela diz isso ao gestor, em vez de creditar a nota a alguém em silêncio.
public static class AgentResolver
{
    /// Nome do atendente por trás do canal, ou null quando não há candidato.
    public static string? Resolver(string canal, IReadOnlyList<AgentRow> agentes)
    {
        if (string.IsNullOrWhiteSpace(canal)) return null;

        // "Agent/24" → tipo "Agent", número "24". Sem barra, trata tudo como número.
        var barra = canal.IndexOf('/');
        string tipo = barra > 0 ? canal[..barra] : string.Empty;
        string numero = (barra >= 0 ? canal[(barra + 1)..] : canal).Trim();
        if (numero.Length == 0) return null;

        var candidatos = agentes
            .Where(a => string.Equals(a.Number?.Trim(), numero, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // O prefixo do canal diz o tipo do cadastro; usar isso evita pegar o homônimo SIP.
        if (tipo.Length > 0)
        {
            var doTipo = candidatos
                .Where(a => string.Equals(a.Type, tipo, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (doTipo.Count > 0) candidatos = doTipo;
        }

        // Ativo primeiro; entre iguais, o cadastro mais recente (maior id). Determinístico:
        // o mesmo dado sempre produz o mesmo nome, relatório após relatório.
        return candidatos
            .OrderByDescending(a => a.Estatus == "A")
            .ThenByDescending(a => a.Id)
            .FirstOrDefault()
            ?.Name;
    }
}
