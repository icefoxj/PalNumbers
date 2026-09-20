using System.Runtime.InteropServices;

namespace PalNumbers;

// Cores ANSI para a saída do console.
//
// As sequências vão no próprio fluxo de texto, e não pela API
// Console.ForegroundColor: a saída é bufferizada, então mudar a cor pela API
// teria efeito imediato enquanto o texto ainda estaria na fila, pintando as
// linhas erradas.
//
// Ficam desligadas quando a saída é redirecionada, para não sujar arquivos e
// pipes com códigos de escape. NO_COLOR desliga e FORCE_COLOR liga à força,
// seguindo a convenção usada pela maioria das ferramentas de linha de comando.
internal static class Cores
{
    private const int HandleSaidaPadrao = -11;
    private const uint ProcessamentoDeTerminalVirtual = 0x0004;

    public static bool Ativas { get; }

    public static readonly string Reset;
    public static readonly string Titulo;
    public static readonly string Rotulo;
    public static readonly string Separador;
    public static readonly string Numero;
    public static readonly string Palindromo;
    public static readonly string Iteracoes;
    public static readonly string Alerta;
    public static readonly string Discreto;
    public static readonly string Destaque;
    public static readonly string Borda;

    static Cores()
    {
        Ativas = Decidir();

        if (Ativas && OperatingSystem.IsWindows())
        {
            HabilitarTerminalVirtual();
        }

        Reset      = Pintar("\e[0m");
        Titulo     = Pintar("\e[1;96m");    // ciano forte, negrito
        Rotulo     = Pintar("\e[96m");      // ciano
        Separador  = Pintar("\e[90m");      // cinza
        Numero     = Pintar("\e[97m");      // branco forte
        Palindromo = Pintar("\e[92m");      // verde
        Iteracoes  = Pintar("\e[93m");      // amarelo
        Alerta     = Pintar("\e[91m");      // vermelho
        Discreto   = Pintar("\e[90m");      // cinza
        Destaque   = Pintar("\e[1;30;102m");// preto sobre verde, negrito
        Borda      = Pintar("\e[1;92m");    // verde forte, negrito
    }

    private static string Pintar(string sequencia) => Ativas ? sequencia : string.Empty;

    private static bool Decidir()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_COLOR")))
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        return !Console.IsOutputRedirected;
    }

    private static void HabilitarTerminalVirtual()
    {
        nint handle = GetStdHandle(HandleSaidaPadrao);

        if (handle != 0 && handle != -1 && GetConsoleMode(handle, out uint modo))
        {
            SetConsoleMode(handle, modo | ProcessamentoDeTerminalVirtual);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
