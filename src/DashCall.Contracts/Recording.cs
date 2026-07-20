namespace DashCall.Contracts;

/// Uma chamada gravada, como aparece na lista do supervisor. Vem de `call_recording`
/// enriquecida por `call_entry` (join por uniqueid); campos anuláveis não casam para
/// chamadas fora de fila (discada direta), que não existem em `call_entry`.
public record RecordingRow(
    int Id,
    DateTimeOffset Quando,
    string Callerid,
    string? Fila,
    string? Agente,
    int? DuracaoSeg,
    int? EsperaSeg,
    string Direcao,        // "entrada" | "saida"
    bool Disponivel);      // arquivo ainda no disco (derivado da retenção)

/// Pedido de listagem (hub->coletor). Filtros opcionais; paginação obrigatória.
/// O período é limitado no hub à janela de retenção — ver spec do Módulo 6.
public record RecordingListRequest(
    string CorrelationId,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    string? Fila,
    int? AgentId,
    string? Callerid,
    int Pagina,
    int PorPagina);

/// Resposta da listagem (coletor->hub). Rows+Total OU Error.
public record RecordingListResult(
    string CorrelationId,
    IReadOnlyList<RecordingRow>? Rows = null,
    int Total = 0,
    string? Error = null);

/// Pedido de download de UMA gravação (hub->coletor).
/// O Id vem da linha listada; o coletor valida o caminho antes de ler (anti path traversal).
public record RecordingDownloadRequest(string CorrelationId, int Id);

/// Resposta do download (coletor->hub). Filename+Conteudo OU Erro.
/// Conteudo é o binário do arquivo (.wav49); o hub o repassa como attachment.
public record RecordingDownloadResult(
    string CorrelationId,
    string? Filename = null,
    byte[]? Conteudo = null,
    string? Erro = null);

/// Erros de download que a interface trata de forma específica.
public static class RecordingErrors
{
    /// O registro existe, mas o arquivo já saiu do disco (retenção de ~30 dias) entre
    /// a listagem e o clique. A tela diz "gravação não está mais disponível", não "erro".
    public const string Expirada = "GRAVACAO_EXPIRADA";

    /// O caminho do arquivo escaparia da pasta de gravações — registro adulterado no banco
    /// do cliente. Nunca deve acontecer em operação normal; é a rede de segurança.
    public const string CaminhoInvalido = "CAMINHO_INVALIDO";

    /// Arquivo maior que o teto do canal. Baixar exigiria outro transporte.
    public const string ArquivoGrande = "ARQUIVO_GRANDE";
}
