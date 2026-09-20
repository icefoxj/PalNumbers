using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using PalNumbers;

// PalNumbers — inverter e somar até obter um palíndromo.
//
// Regra: soma-se o número ao seu inverso e verifica-se se o resultado é um
// palíndromo. Se não for, repete-se a operação sobre o resultado, contando
// quantas iterações foram necessárias.   (12 -> 12 + 21 = 33, 1 iteração)
//
// A sequência 0, 1, 2, 3, ... corre sem limite, gravando cada resultado no
// banco ao lado do executável, e só para com Ctrl+C. Na execução seguinte
// retoma do ponto onde parou.
//
// Quem não converge desce uma cascata de limites crescentes, tocada por
// threads dedicadas fora do orçamento de núcleos da sequência principal.

Console.OutputEncoding = Encoding.UTF8;

const int LimiteIteracoes = 10_000;         // sequência principal
const int LimiteTriagem = 100_000;          // primeiro degrau do reprocessamento
const int LimiteAmpliado = 1_000_000;       // segundo degrau
const int LinhasPorCabecalho = 50;

string caminhoEsquema = Path.Combine(AppContext.BaseDirectory, "Esquema.sql");
string caminhoBanco = Path.Combine(AppContext.BaseDirectory, "PalNumbers.db");

switch (args)
{
    // Usado pelo build para manter o banco da raiz do projeto em dia; cria o
    // arquivo se não existir, aplica o esquema e sai sem calcular nada.
    case ["--criar-banco", string destino]:
        using (Repositorio criacao = new(destino, caminhoEsquema))
        {
            Console.WriteLine($"Banco pronto: {Path.GetFullPath(destino)}");
        }

        return 0;

    // Só mostra o estado do banco e sai: nenhum número é calculado. Pode ser
    // usado com o programa rodando em outra janela — o SQLite em modo WAL
    // deixa ler enquanto se grava.
    case ["--resumo"]:
        EscreverResumo(caminhoBanco, caminhoEsquema, LimiteTriagem, LimiteAmpliado);
        return 0;

    case []:
        break;                  // execução normal

    default:
        Console.Error.WriteLine($"Opção desconhecida: {string.Join(' ', args)}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Uso:");
        Console.Error.WriteLine("  PalNumbers                      calcula a sequência até ser interrompido");
        Console.Error.WriteLine("  PalNumbers --resumo             mostra o estado do banco e sai");
        Console.Error.WriteLine("  PalNumbers --criar-banco <arq>  cria ou atualiza o banco no caminho dado");
        return 1;
}

// Metade dos processadores lógicos da máquina para o cálculo da sequência.
// As threads de reprocessamento e a orquestradora ficam FORA desta conta.
int trabalhadores = Math.Max(1, Environment.ProcessorCount / 2);

// Os degraus da cascata. O custo cresce com o quadrado das iterações — 100.000
// levam ~9s por número e 1.000.000 levam ~15,5 min, medidos — então as quatro
// threads baratas fazem a triagem e uma só, cara, recebe o que sobrou.
Estagio[] estagios =
[
    new("triagem", Tabelas.DezMil, Tabelas.CemMil, LimiteTriagem, Threads: 4),
    new("profundo", Tabelas.CemMil, Tabelas.UmMilhao, LimiteAmpliado, Threads: 1),
];

// Os números são calculados em blocos: o bloco inteiro é resolvido em
// paralelo e só depois gravado e impresso, em ordem crescente. Isso mantém
// duas garantias que o paralelismo quebraria de outra forma — a tela continua
// em ordem, e o banco nunca fica com um número gravado e outro menor
// faltando, que é o que permite retomar pelo maior número gravado.
//
// O bloco é grande de propósito. O custo por número é muito desigual: os que
// não convergem gastam as 10.000 iterações e custam uns mil números comuns.
// Num bloco pequeno sobram poucos números caros para distribuir, e o bloco
// acaba durando o tempo do worker mais azarado. Medido nesta máquina, com 16
// workers: bloco de 1.024 rende 9,6 núcleos efetivos; 8.192 rende 14,2.
int tamanhoBloco = 512 * trabalhadores;

// Duas instâncias sobre o mesmo banco se atropelam: as duas retomam do mesmo
// ponto e a segunda esbarra na restrição de unicidade de Numero, derrubando o
// processo no meio de um bloco. O arquivo de trava impede isso, e o sistema o
// libera sozinho quando o processo termina, inclusive se terminar mal.
//
// Só a execução normal trava; --resumo continua podendo ler a qualquer hora.
using FileStream? trava = TentarTravar(caminhoBanco);

if (trava is null)
{
    Console.Error.WriteLine("Já há uma instância do PalNumbers usando este banco:");
    Console.Error.WriteLine($"  {caminhoBanco}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Encerre a outra antes de começar, ou use --resumo para ver o estado sem calcular.");
    return 1;
}

using CancellationTokenSource cancelamento = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;            // impede o encerramento abrupto do processo
    cancelamento.Cancel();      // o laço principal encerra e fecha o lote
};

// Com vários núcleos calculando, a escrita na tela passa a ser o gargalo.
// O buffer é descarregado ao fim de cada bloco, então a saída continua viva.
StreamWriter saida = new(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
{
    AutoFlush = false,
};
Console.SetOut(saida);

using Repositorio repositorio = new(caminhoBanco, caminhoEsquema);

BigInteger numero = repositorio.ProximoNumero();
int threadsDeReprocessamento = estagios.Sum(static estagio => estagio.Threads);

Console.WriteLine();
Console.WriteLine($"{Cores.Titulo}Palíndromos por inversão e soma{Cores.Reset} {Cores.Discreto}— Ctrl+C para parar.{Cores.Reset}");
Console.WriteLine($"{Cores.Rotulo}Banco:{Cores.Reset} {Cores.Numero}{caminhoBanco}{Cores.Reset}");
Console.WriteLine($"{Cores.Rotulo}Cálculo:{Cores.Reset} {Cores.Numero}{trabalhadores}{Cores.Reset} de {Environment.ProcessorCount} núcleos {Cores.Discreto}— blocos de {tamanhoBloco:N0} números, limite de {LimiteIteracoes:N0}.{Cores.Reset}");
Console.WriteLine($"{Cores.Rotulo}Threads:{Cores.Reset} {Cores.Discreto}1 orquestradora + {Cores.Reset}{Cores.Numero}{trabalhadores}{Cores.Reset}{Cores.Discreto} de cálculo + {Cores.Reset}{Cores.Numero}{threadsDeReprocessamento}{Cores.Reset}{Cores.Discreto} de reprocessamento = {1 + trabalhadores + threadsDeReprocessamento}.{Cores.Reset}");

foreach (Estagio estagio in estagios)
{
    Console.WriteLine($"  {Cores.Discreto}{estagio.Threads} thread(s) [{estagio.Nome}]{Cores.Reset} {Cores.Rotulo}{estagio.TabelaOrigem}{Cores.Reset} {Cores.Discreto}-> limite {estagio.Limite:N0} ->{Cores.Reset} {Cores.Rotulo}{estagio.TabelaDestino}{Cores.Reset}");
}

Console.WriteLine(numero.IsZero
    ? $"{Cores.Discreto}Nenhum resultado anterior: começando do zero.{Cores.Reset}"
    : $"{Cores.Discreto}Retomando a partir de{Cores.Reset} {Cores.Numero}{numero}{Cores.Reset}{Cores.Discreto}.{Cores.Reset}");

Contagens contagens = repositorio.Contar();

Console.WriteLine();
Console.WriteLine($"{Cores.Rotulo}Registros por tabela{Cores.Reset}");
EscreverContagem(Tabelas.Palindromos, contagens.Palindromos, Cores.Palindromo, "convergiram");
EscreverContagem(Tabelas.DezMil, contagens.DezMil, Cores.Alerta, $"fila da triagem ({LimiteTriagem:N0})");
EscreverContagem(Tabelas.CemMil, contagens.CemMil, Cores.Iteracoes, $"fila do estágio profundo ({LimiteAmpliado:N0})");
EscreverContagem(Tabelas.UmMilhao, contagens.UmMilhao, Cores.Iteracoes, "fim da cascata");
EscreverContagem("total", contagens.Total, Cores.Numero, null);

// As threads de reprocessamento avisam por esta fila; quem desenha a tela é
// sempre a orquestradora, que a esvazia ao fim de cada bloco.
ConcurrentQueue<Aviso> avisos = new();
List<Thread> threadsReprocessamento = [];

foreach (Estagio estagio in estagios)
{
    for (int i = 1; i <= estagio.Threads; i++)
    {
        Reprocessador reprocessador = new(repositorio, avisos, estagio, cancelamento.Token);

        Thread thread = new(reprocessador.Executar)
        {
            IsBackground = true,
            Name = $"reproc-{estagio.Nome}-{i}",
        };

        thread.Start();
        threadsReprocessamento.Add(thread);
    }
}

ParallelOptions opcoes = new()
{
    MaxDegreeOfParallelism = trabalhadores,
    CancellationToken = cancelamento.Token,
};

// Cada thread precisa da sua própria Calculadora: ela carrega os buffers de
// dígitos entre as chamadas e não é segura para uso simultâneo. A reserva as
// devolve de um bloco para o outro, para não recriar os buffers (que chegam a
// milhares de dígitos) a cada bloco.
ConcurrentBag<Calculadora> reserva = [];

string[] origens = new string[tamanhoBloco];
Resultado[] resultados = new Resultado[tamanhoBloco];

Stopwatch cronometro = Stopwatch.StartNew();
long processados = 0;

while (!cancelamento.IsCancellationRequested)
{
    for (int i = 0; i < tamanhoBloco; i++)
    {
        origens[i] = (numero + i).ToString(CultureInfo.InvariantCulture);
    }

    try
    {
        // O Parallel.For usa como worker a thread que o chama. Rodando dentro
        // de um Task.Run, os 'trabalhadores' workers são todos do pool e esta
        // thread fica só esperando — ela é a orquestradora, e não consome um
        // dos núcleos reservados ao cálculo.
        await Task.Run(
            () => Parallel.For(
                0,
                tamanhoBloco,
                opcoes,
                () => reserva.TryTake(out Calculadora? disponivel) ? disponivel : new Calculadora(),
                (i, _, calculadora) =>
                {
                    resultados[i] = calculadora.Calcular(origens[i], LimiteIteracoes, cancelamento.Token);
                    return calculadora;
                },
                reserva.Add),
            cancelamento.Token);
    }
    catch (OperationCanceledException)
    {
        break;                  // bloco incompleto: descartado e refeito depois
    }

    for (int i = 0; i < tamanhoBloco; i++)
    {
        Resultado resultado = resultados[i];
        repositorio.Registrar(origens[i], resultado);

        if (processados % LinhasPorCabecalho == 0)
        {
            EscreverCabecalho();
        }

        EscreverLinha(origens[i], resultado);
        processados++;
    }

    // O bloco só é dado como concluído depois de gravado por inteiro.
    repositorio.Confirmar();
    EsvaziarAvisos();
    Console.Out.Flush();

    numero += tamanhoBloco;
}

// Dá às threads de reprocessamento a chance de sair sozinhas antes de o
// repositório ser fechado debaixo delas. O prazo é total, não por thread.
DateTime prazo = DateTime.UtcNow.AddSeconds(5);

foreach (Thread thread in threadsReprocessamento)
{
    TimeSpan restante = prazo - DateTime.UtcNow;

    if (restante > TimeSpan.Zero)
    {
        thread.Join(restante);
    }
}

repositorio.Confirmar();
EsvaziarAvisos();
cronometro.Stop();

TimeSpan tempo = cronometro.Elapsed;

string ultimo = processados == 0
    ? "nenhum"
    : (numero - BigInteger.One).ToString(CultureInfo.InvariantCulture);

double porSegundo = tempo.TotalSeconds > 0 ? processados / tempo.TotalSeconds : 0;

Console.WriteLine();
Console.WriteLine(
    $"{Cores.Titulo}Parado.{Cores.Reset} {Cores.Numero}{processados:N0}{Cores.Reset} número(s) gravado(s) "
    + $"{Cores.Discreto}— último:{Cores.Reset} {Cores.Numero}{ultimo}{Cores.Reset} "
    + $"{Cores.Discreto}— tempo:{Cores.Reset} {Cores.Numero}{(int)tempo.TotalHours:D2}:{tempo.Minutes:D2}:{tempo.Seconds:D2}{Cores.Reset} "
    + $"{Cores.Discreto}({porSegundo:N0}/s).{Cores.Reset}");
Console.Out.Flush();

return 0;

// ---------------------------------------------------------------------------

// Abre a trava exclusiva do banco, ou devolve null se outra instância já a
// tem. FileShare.None é o que garante a exclusividade; DeleteOnClose evita
// deixar o arquivo para trás.
static FileStream? TentarTravar(string caminhoBanco)
{
    try
    {
        return new FileStream(
            caminhoBanco + ".lock",
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }
    catch (IOException)
    {
        return null;
    }
    catch (UnauthorizedAccessException)
    {
        return null;
    }
}

static void EscreverResumo(string caminhoBanco, string caminhoEsquema, int limiteTriagem, int limiteAmpliado)
{
    bool existia = File.Exists(caminhoBanco);

    using Repositorio repositorio = new(caminhoBanco, caminhoEsquema);
    Resumo resumo = repositorio.Resumir();

    Console.WriteLine();
    Console.WriteLine($"{Cores.Titulo}Resumo do banco{Cores.Reset}");
    Console.WriteLine($"{Cores.Rotulo}Arquivo:{Cores.Reset} {Cores.Numero}{caminhoBanco}{Cores.Reset} {Cores.Discreto}({new FileInfo(caminhoBanco).Length / 1024.0 / 1024.0:N1} MB){Cores.Reset}");

    if (!existia)
    {
        Console.WriteLine($"{Cores.Alerta}O banco não existia e foi criado agora, vazio.{Cores.Reset}");
    }

    Console.WriteLine();
    Console.WriteLine($"{Cores.Rotulo}Sequência principal{Cores.Reset}");
    EscreverItem("próximo número a calcular", resumo.ProximoNumero.ToString("N0", CultureInfo.InvariantCulture), Cores.Numero);
    EscreverItem("palíndromos encontrados", resumo.Palindromos.ToString("N0", CultureInfo.InvariantCulture), Cores.Palindromo);

    EscreverFila(Tabelas.DezMil, $"triagem, limite de {limiteTriagem:N0}", resumo.DezMil, Cores.Alerta);
    EscreverFila(Tabelas.CemMil, $"profundo, limite de {limiteAmpliado:N0}", resumo.CemMil, Cores.Iteracoes);
    EscreverFila(Tabelas.UmMilhao, "fim da cascata, nada os reprocessa", resumo.UmMilhao, Cores.Iteracoes);

    Console.WriteLine();
    EscreverItem("total de registros", resumo.Total.ToString("N0", CultureInfo.InvariantCulture), Cores.Numero);
    Console.WriteLine();
}

static void EscreverFila(string tabela, string descricao, Fila fila, string cor)
{
    Console.WriteLine();
    Console.WriteLine($"{Cores.Rotulo}{tabela}{Cores.Reset} {Cores.Discreto}— {descricao}{Cores.Reset}");
    EscreverItem("quantos", fila.Quantidade.ToString("N0", CultureInfo.InvariantCulture), cor);
    EscreverItem("próximo da fila", fila.Proximo ?? "fila vazia", Cores.Numero);
    EscreverItem("último que entrou", fila.Ultimo ?? "fila vazia", Cores.Discreto);
}

static void EscreverItem(string rotulo, string valor, string cor) =>
    Console.WriteLine($"  {Cores.Discreto}{rotulo,-28}{Cores.Reset} {cor}{valor,16}{Cores.Reset}");

static void EscreverContagem(string tabela, long quantidade, string cor, string? nota)
{
    string linha = $"  {Cores.Discreto}{tabela,-20}{Cores.Reset} {cor}{quantidade,12:N0}{Cores.Reset}";

    Console.WriteLine(nota is null
        ? linha
        : $"{linha}  {Cores.Discreto}{nota}{Cores.Reset}");
}

static void EscreverCabecalho()
{
    Console.WriteLine();
    Console.WriteLine($"{Cores.Rotulo}{"Número",10} | {"Palíndromo",28} | {"Iterações",12}{Cores.Reset}");
    Console.WriteLine($"{Cores.Separador}{new string('-', 10)}-+-{new string('-', 28)}-+-{new string('-', 12)}{Cores.Reset}");
}

static void EscreverLinha(string origem, Resultado resultado)
{
    string valor = resultado.Palindromo ?? "não convergiu";
    string cor = resultado.Convergiu ? Cores.Palindromo : Cores.Alerta;

    string iteracoes = resultado.Convergiu
        ? resultado.Iteracoes.ToString("N0", CultureInfo.InvariantCulture)
        : $"> {resultado.Iteracoes:N0}";

    string barra = $"{Cores.Separador}|{Cores.Reset}";

    Console.WriteLine(
        $"{Cores.Numero}{origem,10}{Cores.Reset} {barra} "
        + $"{cor}{valor,28}{Cores.Reset} {barra} "
        + $"{cor}{iteracoes,12}{Cores.Reset}");
}

void EsvaziarAvisos()
{
    while (avisos.TryDequeue(out Aviso aviso))
    {
        switch (aviso.Nivel)
        {
            case NivelAviso.Destaque:
                EscreverDestaque(aviso);
                break;

            case NivelAviso.Atencao:
                Console.WriteLine();
                Console.WriteLine($"{Cores.Iteracoes}  > {aviso.Texto}{Cores.Reset}");
                Console.WriteLine();
                break;

            default:
                Console.WriteLine($"{Cores.Discreto}  . {aviso.Texto}{Cores.Reset}");
                break;
        }
    }
}

static void EscreverDestaque(Aviso aviso)
{
    const string Titulo = "NOVO RESULTADO";

    string[] corpo = aviso.Detalhe is null
        ? [aviso.Texto]
        : [aviso.Texto, aviso.Detalhe];

    int largura = Math.Max(Titulo.Length, corpo.Max(static linha => linha.Length)) + 2;
    string traco = new('=', largura);

    Console.WriteLine();
    Console.WriteLine($"{Cores.Borda}+{traco}+{Cores.Reset}");
    Console.WriteLine($"{Cores.Borda}|{Cores.Reset}{Cores.Destaque}{(' ' + Titulo).PadRight(largura)}{Cores.Reset}{Cores.Borda}|{Cores.Reset}");

    foreach (string linha in corpo)
    {
        Console.WriteLine($"{Cores.Borda}|{Cores.Reset} {Cores.Palindromo}{linha.PadRight(largura - 1)}{Cores.Reset}{Cores.Borda}|{Cores.Reset}");
    }

    Console.WriteLine($"{Cores.Borda}+{traco}+{Cores.Reset}");
    Console.WriteLine();
}
