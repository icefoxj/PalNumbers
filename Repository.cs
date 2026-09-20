using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace PalNumbers;

// Table names in one place: the repository interpolates them into SQL, so they
// have to be constants of the code and never come from outside.
//
// The names themselves stay as they were created. Renaming them would mean
// migrating a live database that already holds millions of rows, for no gain
// beyond spelling.
internal static class Tables
{
    public const string Solved = "Palindromos";
    public const string TenThousand = "NaoConvergiram";
    public const string HundredThousand = "NaoConvergiram100k";
    public const string Million = "NaoConvergiram1M";

    // Every table holding numbers already computed, used to find out where the
    // sequence stopped.
    public static readonly string[] All = [Solved, TenThousand, HundredThousand, Million];
}

// State of one reprocessing queue.
internal readonly record struct QueueState(long Count, string? Next, string? Last);

// Row counts per table.
internal readonly record struct Counts(
    long Solved,
    long TenThousand,
    long HundredThousand,
    long Million)
{
    public long Total => Solved + TenThousand + HundredThousand + Million;
}

// Snapshot of the database for the --summary option.
internal readonly record struct Summary(
    BigInteger NextNumber,
    long Solved,
    QueueState TenThousand,
    QueueState HundredThousand,
    QueueState Million)
{
    public long Total => Solved + TenThousand.Count + HundredThousand.Count + Million.Count;
}

// SQLite persistence, in the file given by 'databasePath'.
//
// Writes from the main sequence are grouped into transactions: every commit
// costs a disk flush, and committing row by row would drop throughput from
// thousands to a few hundred numbers per second. A batch closes on count or on
// time — the time limit matters because throughput collapses in stretches with
// many non-converging numbers, and without it progress would go minutes without
// being stored.
//
// The class is used by several threads: the orchestrator, which writes the new
// numbers, and the five reprocessing ones. A single connection serialised by a
// lock is simpler and safer than several connections fighting over the file —
// SQLite allows only one writer at a time, and the others would hit
// SQLITE_BUSY while the orchestrator's batch was open. The lock costs little:
// reprocessing writes once every several seconds, in the fastest stage.
internal sealed class Repository : IDisposable
{
    private const int BatchSize = 500;
    private const int ClaimWindow = 64;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);

    private readonly Lock gate = new();
    private readonly SqliteConnection connection;
    private readonly SqliteCommand insertSolved;
    private readonly SqliteCommand insertUnsolved;
    private readonly Stopwatch batchTimer = Stopwatch.StartNew();

    // Numbers some reprocessing thread has already taken. Without this, the
    // four threads of the first stage would all read the same head of the queue
    // and compute the same number.
    private readonly HashSet<string> claimed = [];

    private SqliteTransaction? transaction;
    private int pending;

    public Repository(string databasePath, string schemaPath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        connection = new SqliteConnection(builder.ToString());
        connection.Open();

        ApplySchema(schemaPath);

        insertSolved = connection.CreateCommand();
        insertSolved.CommandText =
            $"""
            INSERT INTO {Tables.Solved} (Numero, Palindromo, Iteracoes, CalculadoEm)
            VALUES ($number, $palindrome, $iterations, $moment);
            """;
        insertSolved.Parameters.Add("$number", SqliteType.Text);
        insertSolved.Parameters.Add("$palindrome", SqliteType.Text);
        insertSolved.Parameters.Add("$iterations", SqliteType.Integer);
        insertSolved.Parameters.Add("$moment", SqliteType.Text);

        insertUnsolved = connection.CreateCommand();
        insertUnsolved.CommandText =
            $"""
            INSERT INTO {Tables.TenThousand} (Numero, Iteracoes, Digitos, CalculadoEm)
            VALUES ($number, $iterations, $digits, $moment);
            """;
        insertUnsolved.Parameters.Add("$number", SqliteType.Text);
        insertUnsolved.Parameters.Add("$iterations", SqliteType.Integer);
        insertUnsolved.Parameters.Add("$digits", SqliteType.Integer);
        insertUnsolved.Parameters.Add("$moment", SqliteType.Text);
    }

    // The next number to compute: the largest one stored, plus one. Every table
    // counts, because a number that went down the cascade has already been
    // computed and must not return to the queue.
    //
    // The ordering is by VALUE, not by Id. Insertion order would be wrong: when
    // reprocessing promotes a pending number it appends an old number to the end
    // of the main table, and the last row inserted stops being the largest.
    // Resuming would land behind the real point and the sequence would recompute
    // numbers already stored, hitting the uniqueness constraint.
    //
    // Numero is TEXT and can grow without bound, so comparing as an integer will
    // not do. For non-negative integers with no leading zeros, ordering by
    // (length, text) is exactly numeric order.
    public BigInteger NextNumber()
    {
        lock (gate)
        {
            BigInteger last = BigInteger.MinusOne;

            foreach (string table in Tables.All)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT Numero FROM {table} ORDER BY LENGTH(Numero) DESC, Numero DESC LIMIT 1;";

                if (command.ExecuteScalar() is string text
                    && BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger value)
                    && value > last)
                {
                    last = value;
                }
            }

            return last + BigInteger.One;
        }
    }

    public void Record(string number, Outcome outcome)
    {
        lock (gate)
        {
            transaction ??= connection.BeginTransaction();

            string moment = Moment();

            if (outcome.Palindrome is string palindrome)
            {
                insertSolved.Transaction = transaction;
                insertSolved.Parameters["$number"].Value = number;
                insertSolved.Parameters["$palindrome"].Value = palindrome;
                insertSolved.Parameters["$iterations"].Value = outcome.Iterations;
                insertSolved.Parameters["$moment"].Value = moment;
                insertSolved.ExecuteNonQuery();
            }
            else
            {
                insertUnsolved.Transaction = transaction;
                insertUnsolved.Parameters["$number"].Value = number;
                insertUnsolved.Parameters["$iterations"].Value = outcome.Iterations;
                insertUnsolved.Parameters["$digits"].Value = outcome.Digits;
                insertUnsolved.Parameters["$moment"].Value = moment;
                insertUnsolved.ExecuteNonQuery();
            }

            pending++;

            if (pending >= BatchSize || batchTimer.Elapsed >= BatchInterval)
            {
                Commit();
            }
        }
    }

    // Closes the open batch. Anything not committed is dropped by SQLite, and
    // the number is computed again on the next run.
    public void Commit()
    {
        lock (gate)
        {
            if (transaction is null)
            {
                return;
            }

            transaction.Commit();
            transaction.Dispose();
            transaction = null;
            pending = 0;
            batchTimer.Restart();
        }
    }

    public Counts Count()
    {
        lock (gate)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT (SELECT COUNT(*) FROM {Tables.Solved}),
                       (SELECT COUNT(*) FROM {Tables.TenThousand}),
                       (SELECT COUNT(*) FROM {Tables.HundredThousand}),
                       (SELECT COUNT(*) FROM {Tables.Million});
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();

            return new Counts(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3));
        }
    }

    // Snapshot of the database for the --summary option. A single query, because
    // the parts have to agree with each other: with the program running in
    // another window, separate queries would catch different instants.
    public Summary Summarise()
    {
        lock (gate)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT (SELECT COUNT(*) FROM {Tables.Solved}),
                       (SELECT COUNT(*) FROM {Tables.TenThousand}),
                       (SELECT Numero FROM {Tables.TenThousand}     ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tables.TenThousand}     ORDER BY Id DESC LIMIT 1),
                       (SELECT COUNT(*) FROM {Tables.HundredThousand}),
                       (SELECT Numero FROM {Tables.HundredThousand} ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tables.HundredThousand} ORDER BY Id DESC LIMIT 1),
                       (SELECT COUNT(*) FROM {Tables.Million}),
                       (SELECT Numero FROM {Tables.Million}         ORDER BY LENGTH(Numero), Numero LIMIT 1),
                       (SELECT Numero FROM {Tables.Million}         ORDER BY Id DESC LIMIT 1);
                """;

            long solved;
            QueueState tenThousand, hundredThousand, million;

            // The reader is closed before NextNumber() opens another command:
            // the connection is the same one, and two open readers on it are
            // best avoided.
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                reader.Read();

                solved = reader.GetInt64(0);
                tenThousand = new QueueState(reader.GetInt64(1), Text(reader, 2), Text(reader, 3));
                hundredThousand = new QueueState(reader.GetInt64(4), Text(reader, 5), Text(reader, 6));
                million = new QueueState(reader.GetInt64(7), Text(reader, 8), Text(reader, 9));
            }

            return new Summary(NextNumber(), solved, tenThousand, hundredThousand, million);
        }

        static string? Text(SqliteDataReader reader, int column) =>
            reader.IsDBNull(column) ? null : reader.GetString(column);
    }

    // Claims the next candidate from the given table, skipping the ones another
    // thread already took, or returns null when nothing is free. Whoever claims
    // is required to call ReleaseClaim afterwards.
    //
    // The queue advances from the smallest number to the largest, not by
    // insertion order: the four triage threads finish out of order, so what
    // descends to the deep stage arrives shuffled. Since each number costs about
    // fifteen minutes there, it is worth always attacking the smallest first.
    public string? ClaimPending(string table)
    {
        lock (gate)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"SELECT Numero FROM {table} ORDER BY LENGTH(Numero), Numero LIMIT {ClaimWindow};";

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                string number = reader.GetString(0);

                if (claimed.Add(number))
                {
                    return number;
                }
            }

            return null;
        }
    }

    public void ReleaseClaim(string number)
    {
        lock (gate)
        {
            claimed.Remove(number);
        }
    }

    // The number converged: it enters the main table and leaves the queue it
    // came from, in a single transaction.
    public void Promote(string sourceTable, string number, Outcome outcome) =>
        MovePending(
            $"""
            INSERT INTO {Tables.Solved} (Numero, Palindromo, Iteracoes, CalculadoEm)
            VALUES ($number, $palindrome, $iterations, $moment);
            DELETE FROM {sourceTable} WHERE Numero = $number;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$number", number);
                command.Parameters.AddWithValue("$palindrome", outcome.Palindrome!);
                command.Parameters.AddWithValue("$iterations", outcome.Iterations);
                command.Parameters.AddWithValue("$moment", Moment());
            });

    // The number survived the stage's limit: it goes one step down the cascade.
    public void Demote(string sourceTable, string targetTable, string number, Outcome outcome) =>
        MovePending(
            $"""
            INSERT INTO {targetTable} (Numero, Iteracoes, Digitos, CalculadoEm)
            VALUES ($number, $iterations, $digits, $moment);
            DELETE FROM {sourceTable} WHERE Numero = $number;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$number", number);
                command.Parameters.AddWithValue("$iterations", outcome.Iterations);
                command.Parameters.AddWithValue("$digits", outcome.Digits);
                command.Parameters.AddWithValue("$moment", Moment());
            });

    private void MovePending(string sql, Action<SqliteCommand> bind)
    {
        lock (gate)
        {
            // Closes the orchestrator's batch before opening its own
            // transaction: the connection is a single one and does not nest
            // transactions.
            Commit();

            using SqliteTransaction own = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();

            command.Transaction = own;
            command.CommandText = sql;
            bind(command);
            command.ExecuteNonQuery();

            own.Commit();
        }
    }

    private static string Moment() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private void ApplySchema(string schemaPath)
    {
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"Schema script not found: {schemaPath}", schemaPath);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Commit();
        insertSolved.Dispose();
        insertUnsolved.Dispose();
        connection.Dispose();
    }
}
