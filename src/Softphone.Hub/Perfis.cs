namespace Softphone.Hub;

/// <summary>
/// Quem pediu algo à IA (resumo de gravação). 1.3.3 (11/11/2026, pedido do Ana): os perfis para a IA, a base de conhecimento, os artigos e o
/// copiloto saíram do Softphone (vão para a ferramenta nova de vendas). Arquivos e tabelas já gravados no servidor ficam guardados, sem uso.
/// </summary>
public sealed record PessoaIa(string Login, string Nome, string? Setor)
{
    public string? Assunto { get; init; }
}