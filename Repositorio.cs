using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace PalNumbers;

// Nomes das tabelas em um lugar só: o repositório os interpola em SQL, então
// precisam ser constantes do código e nunca vir de fora.
internal static class Tabelas
{
    public const string Palindromos = "Palindromos";
    public const string DezMil = "NaoConvergiram";
    public const string CemMil = "NaoConvergiram100k";
    public const string UmMilhao = "NaoConvergiram1M";

    // Todas as que guardam números já calculados, para descobrir onde a
    // sequência parou.
    public static readonly string[] Todas = [Palindromos, DezMil, CemMil, UmMilhao];
}

// Estado de uma fila de reprocessamento.
internal readonly record struct Fila(long Quantidade, string? Proximo, string? Ultimo);

// Quantidade de registros em cada tabela.
internal readonly record struct Contagens(
    long Palindromos,
    long DezMil,
    long CemMil,
    long UmMilhao)
{
    public long Total => Palindromos + DezMil + CemMil + UmMilhao;
}

// Retrato do banco para a opção --resumo.
internal readonly record struct Resumo(
    BigInteger ProximoNumero,
    long Palindromos,
    Fila DezMil,
    Fila CemMil,
    Fila UmMilhao)
{
    public long Total => Palindromos + DezMil.Quantidade + CemMil.Quantidade + UmMilhao.Quantidade;
}

// Persistência em SQLite, no arquivo indicado por 'caminhoBanco'.
//
// As gravações do laço principal são agrupadas em transações: cada commit
// custa um flush em disco, e confirmar linha a linha derrubaria a vazão de
// milhares para algumas centenas de números por segundo. O lote fecha por
// quantidade ou por tempo — o limite de tempo importa porque a vazão despenca
// nos trechos com muitos números que não convergem, e sem ele o progresso
// ficaria minutos sem ser gravado.
//
// A classe é usada por várias threads: a orquestradora, que grava os números
// novos, e as cinco de reprocessamento. Uma única conexão serializada por
// trava é mais simples e mais segura do que várias conexões disputando o
// arquivo — o SQLite só admite um escritor por vez, e as demais esbarrariam
// em SQLITE_BUSY enquanto o lote da orquestradora estivesse aberto. A trava
// custa pouco: o reprocessamento escreve uma vez a cada vários segundos, no
// estágio mais rápido.
internal sealed class Repositorio : IDisposable
{
    private const int TamanhoLote = 500;
    private const int JanelaDeReserva = 64;
    private static readonly TimeSpan IntervaloLote = TimeSpan.FromSeconds(2);

    private readonly Lock trava = new();
    private readonly SqliteConnection conexao;
    private readonly SqliteCommand inserirPalindromo;
    private readonly SqliteCommand inserirNaoConvergiu;
    private readonly Stopwatch cronometroLote = Stopwatch.StartNew();

    // Números que alguma thread de reprocessamento já pegou. Sem isto, as
    // quatro threads do primeiro estágio leriam todas a mesma cabeça da fila e
    // calculariam o mesmo número.
    private readonly HashSet<string> emAndamento = [];

    private SqliteTransaction? transacao;
    private int pendentes;

    public Repositorio(string caminhoBanco, string caminhoEsquema)
    {
        string? pasta = Path.GetDirectoryName(Path.GetFullPath(caminhoBanco));

        if (!string.IsNullOrEmpty(pasta))
        {
            Directory.CreateDirectory(pasta);
        }

        SqliteConnectionStringBuilder construtor = new()
        {
            DataSource = caminhoBanco,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        conexao = new SqliteConnection(construtor.ToString());
        conexao.Open();

        AplicarEsquema(caminhoEsquema);

        inserirPalindromo = conexao.CreateCommand();
        inserirPalindromo.CommandText =
            $"""
            INSERT INTO {Tabelas.Palindromos} (Numero, Palindromo, Iteracoes, CalculadoEm)
            VALUES ($numero, $palindromo, $iteracoes, $momento);
            """;
        inserirPalindromo.Parameters.Add("$numero", SqliteType.Text);
        inserirPalindromo.Parameters.Add("$palindromo", SqliteType.Text);
        inserirPalindromo.Parameters.Add("$iteracoes", SqliteType.Integer);
        inserirPalindromo.Parameters.Add("$momento", SqliteType.Text);

        inserirNaoConvergiu = conexao.CreateCommand();
        inserirNaoConvergiu.CommandText =
            $"""
            INSERT INTO {Tabelas.DezMil} (Numero, Iteracoes, Digitos, CalculadoEm)
            VALUES ($numero, $iteracoes, $digitos, $momento);
            """;
        inserirNaoConvergiu.Parameters.Add("$numero", SqliteType.Text);
        inserirNaoConvergiu.Parameters.Add("$iteracoes", SqliteType.Integer);
        inserirNaoConvergiu.Parameters.Add("$digitos", SqliteType.Integer);
        inserirNaoConvergiu.Parameters.Add("$momento", SqliteType.Text);
    }

    // O próximo número a calcular: o maior já gravado, mais um. Todas as
    // tabelas entram na conta, porque um número que desceu a cascata já foi
    // calculado e não deve voltar à fila.
    //
    // A ordenação é pelo VALOR, não por Id. Pela ordem de inserção daria
    // errado: quando o reprocessamento promove um pendente, ele acrescenta um
    // número antigo ao fim de Palindromos, e a última linha inserida deixa de
    // ser a maior. O retomar cairia atrás do ponto real e a sequência
    // recalcularia números já gravados, batendo na restrição de unicidade.
    //
    // Numero é TEXT e pode crescer sem limite, então comparar como inteiro não
    // serve. Para inteiros não negativos e sem zeros à esquerda, ordenar por
    // (comprimento, texto) é exatamente a ordem numérica.
    public BigInteger ProximoNumero()
    {
        lock (trava)
        {
            BigInteger ultimo = BigInteger.MinusOne;

            foreach (string tabela in Tabelas.Todas)
            {
                using SqliteCommand comando = conexao.CreateCommand();
                comando.CommandText =
                    $"SELECT Numero FROM {tabela} ORDER BY LENGTH(Numero) DESC, Numero DESC LIMIT 1;";

                if (comando.ExecuteScalar() is string texto
                    && BigInteger.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger valor)
                    && valor > ultimo)
                {
                    ultimo = valor;
                }
            }

            return ultimo + BigInteger.One;
        }
    }

    public void Registrar(string numero, Resultado resultado)
    {
        lock (trava)
        {
            transacao ??= conexao.BeginTransaction();

            string momento = Momento();

            if (resultado.Palindromo is string palindromo)
            {
                inserirPalindromo.Transaction = transacao;
                inserirPalindromo.Parameters["$numero"].Value = numero;
                inserirPalindromo.Parameters["$palindromo"].Value = palindromo;
                inserirPalindromo.Parameters["$iteracoes"].Value = resultado.Iteracoes;
                inserirPalindromo.Parameters["$momento"].Value = momento;
                inserirPalindromo.ExecuteNonQuery();
            }
            else
            {
                inserirNaoConvergiu.Transaction = transacao;
                inserirNaoConvergiu.Parameters["$numero"].Value = numero;
                inserirNaoConvergiu.Parameters["$iteracoes"].Value = resultado.Iteracoes;
                inserirNaoConvergiu.Parameters["$digitos"].Value = resultado.Digitos;
                inserirNaoConvergiu.Parameters["$momento"].Value = momento;
                inserirNaoConvergiu.ExecuteNonQuery();
            }

            pendentes++;

            if (pendentes >= TamanhoLote || cronometroLote.Elapsed >= IntervaloLote)
            {
                Confirmar();
            }
        }
    }

    // Fecha o lote em aberto. O que não for confirmado é descartado pelo
    // SQLite, e o número volta a ser calculado na próxima execução.
    public void Confirmar()
    {
        lock (trava)
        {
            if (transacao is null)
            {
                return;
            }

            transacao.Commit();
            transacao.Dispose();
            transacao = null;
            pendentes = 0;
            cronometroLote.Restart();
        }
    }

    public Contagens Contar()
    {
        lock (trava)
        {
            using SqliteCommand comando = conexao.CreateCommand();
            comando.CommandText =
                $"""
                SELECT (SELECT COUNT(*) FROM {Tabelas.Palindromos}),
                       (SELECT COUNT(*) FROM {Tabelas.DezMil}),
                       (SELECT COUNT(*) FROM {Tabelas.CemMil}),
                       (SELECT COUNT(*) FROM {Tabelas.UmMilhao});
                """;

            using SqliteDataReader leitor = comando.ExecuteReader();
            leitor.Read();

            return new Contagens(
                leitor.GetInt64(0),
                leitor.GetInt64(1),
                leitor.GetInt64(2),
                leitor.GetInt64(3));
        }
    }

    // Retrato do banco para a opção --resumo. Uma consulta só, porque as
    // partes precisam ser coerentes entre si: com o programa rodando em outra
    // janela, consultas separadas pegariam instantes diferentes.
    public Resumo Resumir()
    {
        lock (trava)
        {
            using SqliteCommand comando = conexao.CreateCommand();
            comando.CommandText =
                $"""
                SELECT (SELECT COUNT(*) FROM {Tabelas.Palindromos}),
                       (SELECT COUNT(*) FROM {Tabelas.DezMil}),
                       (SELECT Numero FROM {Tabelas.DezMil}   ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tabelas.DezMil}   ORDER BY Id DESC LIMIT 1),
                       (SELECT COUNT(*) FROM {Tabelas.CemMil}),
                       (SELECT Numero FROM {Tabelas.CemMil}   ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tabelas.CemMil}   ORDER BY Id DESC LIMIT 1),
                       (SELECT COUNT(*) FROM {Tabelas.UmMilhao}),
                       (SELECT Numero FROM {Tabelas.UmMilhao} ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tabelas.UmMilhao} ORDER BY Id DESC LIMIT 1);
                """;

            long palindromos;
            Fila dezMil, cemMil, umMilhao;

            // O leitor é fechado antes de ProximoNumero() abrir outro comando:
            // a conexão é a mesma, e não convém ter dois leitores em aberto.
            using (SqliteDataReader leitor = comando.ExecuteReader())
            {
                leitor.Read();

                palindromos = leitor.GetInt64(0);
                dezMil = new Fila(leitor.GetInt64(1), Texto(leitor, 2), Texto(leitor, 3));
                cemMil = new Fila(leitor.GetInt64(4), Texto(leitor, 5), Texto(leitor, 6));
                umMilhao = new Fila(leitor.GetInt64(7), Texto(leitor, 8), Texto(leitor, 9));
            }

            return new Resumo(ProximoNumero(), palindromos, dezMil, cemMil, umMilhao);
        }

        static string? Texto(SqliteDataReader leitor, int coluna) =>
            leitor.IsDBNull(coluna) ? null : leitor.GetString(coluna);
    }

    // Reserva o próximo candidato da tabela indicada, pulando os que outra
    // thread já pegou, ou devolve null se não houver nada livre. Quem reserva
    // é obrigado a chamar LiberarReserva depois.
    //
    // A fila anda do menor número para o maior, e não por ordem de inserção:
    // as quatro threads da triagem terminam fora de ordem, então quem desce
    // para o estágio profundo chega embaralhado. Como lá cada número custa uns
    // 15 minutos, convém atacar sempre o menor primeiro.
    public string? ReservarPendente(string tabela)
    {
        lock (trava)
        {
            using SqliteCommand comando = conexao.CreateCommand();
            comando.CommandText =
                $"SELECT Numero FROM {tabela} ORDER BY LENGTH(Numero), Numero LIMIT {JanelaDeReserva};";

            using SqliteDataReader leitor = comando.ExecuteReader();

            while (leitor.Read())
            {
                string numero = leitor.GetString(0);

                if (emAndamento.Add(numero))
                {
                    return numero;
                }
            }

            return null;
        }
    }

    public void LiberarReserva(string numero)
    {
        lock (trava)
        {
            emAndamento.Remove(numero);
        }
    }

    // O número convergiu: entra na tabela principal e sai da fila de onde
    // veio, em uma transação só.
    public void Promover(string tabelaOrigem, string numero, Resultado resultado) =>
        MoverPendente(
            $"""
            INSERT INTO {Tabelas.Palindromos} (Numero, Palindromo, Iteracoes, CalculadoEm)
            VALUES ($numero, $palindromo, $iteracoes, $momento);
            DELETE FROM {tabelaOrigem} WHERE Numero = $numero;
            """,
            comando =>
            {
                comando.Parameters.AddWithValue("$numero", numero);
                comando.Parameters.AddWithValue("$palindromo", resultado.Palindromo!);
                comando.Parameters.AddWithValue("$iteracoes", resultado.Iteracoes);
                comando.Parameters.AddWithValue("$momento", Momento());
            });

    // O número resistiu ao limite do estágio: desce um degrau da cascata.
    public void Descer(string tabelaOrigem, string tabelaDestino, string numero, Resultado resultado) =>
        MoverPendente(
            $"""
            INSERT INTO {tabelaDestino} (Numero, Iteracoes, Digitos, CalculadoEm)
            VALUES ($numero, $iteracoes, $digitos, $momento);
            DELETE FROM {tabelaOrigem} WHERE Numero = $numero;
            """,
            comando =>
            {
                comando.Parameters.AddWithValue("$numero", numero);
                comando.Parameters.AddWithValue("$iteracoes", resultado.Iteracoes);
                comando.Parameters.AddWithValue("$digitos", resultado.Digitos);
                comando.Parameters.AddWithValue("$momento", Momento());
            });

    private void MoverPendente(string sql, Action<SqliteCommand> parametros)
    {
        lock (trava)
        {
            // Fecha o lote da orquestradora antes de abrir a transação
            // própria: a conexão é uma só e não aninha transações.
            Confirmar();

            using SqliteTransaction propria = conexao.BeginTransaction();
            using SqliteCommand comando = conexao.CreateCommand();

            comando.Transaction = propria;
            comando.CommandText = sql;
            parametros(comando);
            comando.ExecuteNonQuery();

            propria.Commit();
        }
    }

    private static string Momento() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private void AplicarEsquema(string caminhoEsquema)
    {
        if (!File.Exists(caminhoEsquema))
        {
            throw new FileNotFoundException(
                $"Script de esquema não encontrado: {caminhoEsquema}", caminhoEsquema);
        }

        using SqliteCommand comando = conexao.CreateCommand();
        comando.CommandText = File.ReadAllText(caminhoEsquema);
        comando.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Confirmar();
        inserirPalindromo.Dispose();
        inserirNaoConvergiu.Dispose();
        conexao.Dispose();
    }
}
