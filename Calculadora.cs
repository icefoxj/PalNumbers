namespace PalNumbers;

// Resultado de um número. Palindromo só é preenchido quando o número
// convergiu; quando não, fica null e resta Digitos, o tamanho alcançado
// quando o limite se esgotou.
//
// Não formatar o valor que não converge é deliberado: seria uma string de
// milhares de dígitos (centenas de milhares no limite ampliado) construída a
// cada número que falha, para ninguém ler.
internal readonly record struct Resultado(bool Convergiu, string? Palindromo, int Iteracoes, int Digitos);

// Aplica "inverter e somar" sobre um vetor de dígitos decimais, do menos
// significativo para o mais significativo.
//
// Trabalhar sobre os dígitos, e não sobre um BigInteger, é o que mantém o
// custo em O(d) por iteração: as conversões BigInteger <-> string custam
// O(d²) e d chega a milhares de dígitos nos números que não convergem.
//
// Os buffers são reaproveitados entre chamadas: crescem até o pior caso já
// visto e param de alocar.
internal sealed class Calculadora
{
    private byte[] atual = new byte[64];
    private byte[] proximo = new byte[64];
    private int comprimento;

    public Resultado Calcular(ReadOnlySpan<char> numero, int limite, CancellationToken token)
    {
        Carregar(numero);

        for (int iteracoes = 1; iteracoes <= limite; iteracoes++)
        {
            if (token.IsCancellationRequested)
            {
                break;          // mantém o Ctrl+C responsivo em números longos
            }

            SomarComInverso();

            if (EhPalindromo())
            {
                return new Resultado(Convergiu: true, Formatar(), iteracoes, comprimento);
            }
        }

        return new Resultado(Convergiu: false, Palindromo: null, limite, comprimento);
    }

    private void Carregar(ReadOnlySpan<char> numero)
    {
        Reservar(numero.Length + 1);
        comprimento = numero.Length;

        for (int i = 0; i < comprimento; i++)
        {
            atual[i] = (byte)(numero[comprimento - 1 - i] - '0');
        }
    }

    private void SomarComInverso()
    {
        Reservar(comprimento + 1);

        int n = comprimento;
        int transporte = 0;

        for (int i = 0; i < n; i++)
        {
            int soma = atual[i] + atual[n - 1 - i] + transporte;

            // soma <= 9 + 9 + 1, logo o transporte é sempre 0 ou 1.
            if (soma >= 10)
            {
                proximo[i] = (byte)(soma - 10);
                transporte = 1;
            }
            else
            {
                proximo[i] = (byte)soma;
                transporte = 0;
            }
        }

        if (transporte > 0)
        {
            proximo[n] = 1;
            n++;
        }

        (atual, proximo) = (proximo, atual);
        comprimento = n;
    }

    private bool EhPalindromo()
    {
        for (int i = 0, j = comprimento - 1; i < j; i++, j--)
        {
            if (atual[i] != atual[j])
            {
                return false;
            }
        }

        return true;
    }

    private string Formatar() =>
        string.Create(comprimento, this, static (destino, estado) =>
        {
            int n = estado.comprimento;

            for (int i = 0; i < n; i++)
            {
                destino[i] = (char)('0' + estado.atual[n - 1 - i]);
            }
        });

    private void Reservar(int capacidade)
    {
        if (atual.Length >= capacidade)
        {
            return;
        }

        int nova = Math.Max(capacidade, atual.Length * 2);
        Array.Resize(ref atual, nova);
        proximo = new byte[nova];
    }
}
