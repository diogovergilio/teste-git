namespace DashCall.Collector.Sources.MariaDb;

/// Resolve o caminho de uma gravação com segurança.
///
/// O `recordingfile` vem do MariaDB do CLIENTE — que o coletor lê, mas não controla. Um registro
/// adulterado (ou um bug do Issabel) poderia trazer `../../etc/shadow` ou um caminho absoluto.
/// Como o coletor vai ABRIR esse arquivo, isto é a fronteira que impede ler qualquer coisa fora da
/// pasta de gravações. Função pura para se provar sem disco.
public static class RecordingPath
{
    /// Junta a base com o caminho do banco e devolve o caminho absoluto SÓ se ele ficar dentro
    /// da base. Fora dela (via `..`, caminho absoluto, symlink textual) → null.
    public static string? ResolverSeguro(string baseDir, string? recordingFile)
    {
        if (string.IsNullOrWhiteSpace(recordingFile)) return null;

        // Normaliza a base com separador final, para o StartsWith não casar "monitor-x" com "monitor".
        var raiz = Path.GetFullPath(baseDir);
        if (!raiz.EndsWith(Path.DirectorySeparatorChar))
            raiz += Path.DirectorySeparatorChar;

        // Path.Combine com um caminho ABSOLUTO descartaria a base — o que é exatamente um dos
        // ataques. Barra ou contrabarra no início já denuncia intenção de escapar.
        var rel = recordingFile.Replace('\\', '/').TrimStart();
        if (rel.StartsWith('/') || (rel.Length > 1 && rel[1] == ':')) return null;

        string combinado;
        try { combinado = Path.GetFullPath(Path.Combine(raiz, rel)); }
        catch { return null; } // caracteres inválidos no caminho

        // A prova final: depois de resolver os "..", o caminho AINDA precisa estar sob a raiz.
        return combinado.StartsWith(raiz, StringComparison.Ordinal) ? combinado : null;
    }
}
