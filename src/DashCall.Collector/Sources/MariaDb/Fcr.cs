using DashCall.Contracts;

namespace DashCall.Collector.Sources.MariaDb;

/// Uma ligação, no mínimo necessário para o cálculo de FCR.
/// <paramref name="Atendida"/> = status 'terminada' (as abandonadas entram na lista porque
/// provam que o cliente voltou, mas não são avaliadas).
public sealed record CallRow(string Callerid, DateTime Entrada, bool Atendida);

/// "Resolvido no 1º contato": das ligações ATENDIDAS, quantas não tiveram nova ligação
/// do mesmo número dentro da janela (24h).
///
/// Por que em C# e não em SQL: `call_entry` não tem índice em `callerid`, então a subquery
/// correlacionada equivalente é quadrática — medido em 31,8 s para 12 meses, contra 3,4 ms
/// lendo ordenado e calculando aqui. A premissa é zero impacto no MariaDB de produção.
public static class Fcr
{
    /// <param name="linhas">Ligações do período. A ordem não importa (ordenamos aqui).</param>
    /// <param name="janela">Tempo máximo para uma nova ligação contar como retorno.</param>
    /// <param name="topN">Quantos números listar em TopRetornos.</param>
    public static FcrBlock Calcular(IEnumerable<CallRow> linhas, TimeSpan janela, int topN = 5)
    {
        // Anônimos ficam de fora — e isto NÃO é cosmético: "anonymous" agrupa dezenas de pessoas
        // diferentes sob um único "número", o que inventaria retornos que nunca existiram e
        // derrubaria o FCR artificialmente. Mesmo motivo do callerid vazio.
        var porNumero = linhas
            .Where(l => !EhAnonimo(l.Callerid))
            .GroupBy(l => l.Callerid);

        int atendidas = 0, primeiroContato = 0;
        var retornos = new List<RepeatCaller>();

        foreach (var grupo in porNumero)
        {
            var doNumero = grupo.OrderBy(l => l.Entrada).ToList();
            bool teveRetorno = false;

            for (int i = 0; i < doNumero.Count; i++)
            {
                if (!doNumero[i].Atendida) continue; // só as atendidas são avaliadas
                atendidas++;

                // Olha só para FRENTE: a próxima ligação dentro da janela é um retorno.
                // Como a lista está ordenada, basta checar a seguinte.
                var proxima = i + 1 < doNumero.Count ? doNumero[i + 1] : null;
                bool voltou = proxima is not null && proxima.Entrada - doNumero[i].Entrada <= janela;

                if (voltou) teveRetorno = true;
                else primeiroContato++;
            }

            if (teveRetorno) retornos.Add(new RepeatCaller(grupo.Key, doNumero.Count));
        }

        double percent = atendidas > 0 ? Math.Round(100.0 * primeiroContato / atendidas, 2) : 0;

        var top = retornos
            .OrderByDescending(r => r.Ligacoes)
            .ThenBy(r => r.Callerid, StringComparer.Ordinal)
            .Take(topN)
            .ToList();

        return new FcrBlock(atendidas, primeiroContato, percent, top);
    }

    /// Chamador não identificável: vazio ou sem nenhum dígito ("anonymous", "unknown",
    /// "restricted"). Um ramal interno curto continua valendo — é um chamador real.
    private static bool EhAnonimo(string? callerid) =>
        string.IsNullOrWhiteSpace(callerid) || !callerid.Any(char.IsDigit);
}
