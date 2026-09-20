using System.Collections.Concurrent;

namespace PalNumbers;

internal enum NivelAviso
{
    Discreto,
    Destaque,
    Atencao,
}

// Mensagem de uma thread de reprocessamento para a orquestradora, que é quem
// desenha a tela. Elas não escrevem direto no console: com milhares de linhas
// saindo por segundo, várias threads no mesmo fluxo se atropelariam no meio de
// uma linha.
internal readonly record struct Aviso(NivelAviso Nivel, string Texto, string? Detalhe = null);

// Um degrau da cascata de reprocessamento: de onde os números vêm, com que
// limite são tentados e para onde vão se resistirem.
internal readonly record struct Estagio(
    string Nome,
    string TabelaOrigem,
    string TabelaDestino,
    int Limite,
    int Threads);

// Reabre, um a um, os números que não convergiram e tenta de novo com o limite
// do seu estágio.
//
// Cada instância é uma thread. O trabalho é dividido em degraus porque o custo
// cresce com o quadrado das iterações: 100.000 levam cerca de 9 segundos por
// número e 1.000.000 levam uns 15 minutos e meio (medido). Várias threads
// baratas fazem a triagem e uma só, cara, recebe o que sobrou.
internal sealed class Reprocessador(
    Repositorio repositorio,
    ConcurrentQueue<Aviso> avisos,
    Estagio estagio,
    CancellationToken token)
{
    private static readonly TimeSpan EsperaFilaVazia = TimeSpan.FromSeconds(2);

    public void Executar()
    {
        Calculadora calculadora = new();

        while (!token.IsCancellationRequested)
        {
            string? numero = repositorio.ReservarPendente(estagio.TabelaOrigem);

            if (numero is null)
            {
                // Nada livre na fila: ou o estágio anterior ainda não produziu,
                // ou as outras threads já pegaram tudo o que havia.
                token.WaitHandle.WaitOne(EsperaFilaVazia);
                continue;
            }

            try
            {
                avisos.Enqueue(new Aviso(
                    NivelAviso.Discreto,
                    $"[{estagio.Nome}] reprocessando {numero} com limite de {estagio.Limite:N0}..."));

                Resultado resultado = calculadora.Calcular(numero, estagio.Limite, token);

                if (token.IsCancellationRequested)
                {
                    break;      // cálculo incompleto: o número fica na fila
                }

                if (resultado.Convergiu)
                {
                    repositorio.Promover(estagio.TabelaOrigem, numero, resultado);

                    avisos.Enqueue(new Aviso(
                        NivelAviso.Destaque,
                        $"{numero} convergiu em {resultado.Iteracoes:N0} iterações",
                        $"palíndromo de {resultado.Digitos:N0} dígitos — movido para a tabela principal"));
                }
                else
                {
                    repositorio.Descer(estagio.TabelaOrigem, estagio.TabelaDestino, numero, resultado);

                    avisos.Enqueue(new Aviso(
                        NivelAviso.Atencao,
                        $"[{estagio.Nome}] {numero} resistiu a {estagio.Limite:N0} iterações "
                        + $"({resultado.Digitos:N0} dígitos) — foi para {estagio.TabelaDestino}"));
                }
            }
            finally
            {
                repositorio.LiberarReserva(numero);
            }
        }
    }
}
