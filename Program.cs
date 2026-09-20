using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using PalNumbers;

// PalNumbers — reverse-and-add until a palindrome appears.
//
// The rule: add a number to its reverse and check whether the result is a
// palindrome. If it is not, repeat on the result, counting how many iterations
// it took.   (12 -> 12 + 21 = 33, one iteration)
//
// The sequence 0, 1, 2, 3, ... runs with no upper bound, storing every result
// in the database next to the executable, and stops only on Ctrl+C. The next
// run resumes from where it left off.
//
// Numbers that do not converge descend a cascade of increasing limits, worked
// by dedicated threads outside the core budget of the main sequence.

Console.OutputEncoding = Encoding.UTF8;

const int MainLimit = 10_000;               // main sequence
const int TriageLimit = 100_000;            // first step of reprocessing
const int DeepLimit = 1_000_000;            // second step
const int RowsPerHeader = 50;

string schemaPath = Path.Combine(AppContext.BaseDirectory, "Schema.sql");
string databasePath = Path.Combine(AppContext.BaseDirectory, "PalNumbers.db");

switch (args)
{
    // Used by the build to keep the database in the project root up to date;
    // creates the file if missing, applies the schema and exits without
    // computing anything.
    case ["--create-db", string target]:
        using (Repository creation = new(target, schemaPath))
        {
            Console.WriteLine($"Database ready: {Path.GetFullPath(target)}");
        }

        return 0;

    // Only prints the state of the database and exits: no number is computed.
    // Safe to use while the program runs in another window — SQLite in WAL mode
    // allows reading during writes.
    case ["--summary"]:
        WriteSummary(databasePath, schemaPath, TriageLimit, DeepLimit);
        return 0;

    case []:
        break;                  // normal run

    default:
        Console.Error.WriteLine($"Unknown option: {string.Join(' ', args)}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  PalNumbers                    run the sequence until interrupted");
        Console.Error.WriteLine("  PalNumbers --summary          print the state of the database and exit");
        Console.Error.WriteLine("  PalNumbers --create-db <path> create or update a database at that path");
        return 1;
}

// Half of the machine's logical processors for computing the sequence. The
// reprocessing threads and the orchestrator are NOT part of this budget.
int workers = Math.Max(1, Environment.ProcessorCount / 2);

// The steps of the cascade. Cost grows with the square of the iteration count —
// 100,000 take about 9s per number and 1,000,000 about 15.5 minutes, both
// measured — so the four cheap threads do the triage and a single expensive one
// receives what survived.
Stage[] stages =
[
    new("triage", Tables.TenThousand, Tables.HundredThousand, TriageLimit, Threads: 4),
    new("deep", Tables.HundredThousand, Tables.Million, DeepLimit, Threads: 1),
];

// Numbers are computed in blocks: the whole block is solved in parallel and
// only then stored and printed, in ascending order. That preserves two
// guarantees plain parallelism would break — the screen stays ordered, and the
// database never holds a number while a smaller one is missing, which is what
// makes resuming by the largest number stored correct.
//
// The block is deliberately large. Per-number cost is very uneven: those that
// do not converge burn all 10,000 iterations and cost about a thousand ordinary
// numbers. In a small block there are too few expensive numbers to spread
// around, and the block ends up lasting as long as the unluckiest worker.
// Measured on this machine with 16 workers: a block of 1,024 yields 9.6
// effective cores; 8,192 yields 14.2.
int blockSize = 512 * workers;

// Two instances on the same database trip over each other: both resume from the
// same point and the second hits the uniqueness constraint on Numero, bringing
// the process down in the middle of a block. The lock file prevents that, and
// the operating system releases it when the process ends, even if it ends
// badly.
//
// Only the normal run locks; --summary can still read at any time.
using FileStream? lockFile = TryLock(databasePath);

if (lockFile is null)
{
    Console.Error.WriteLine("Another instance of PalNumbers is already using this database:");
    Console.Error.WriteLine($"  {databasePath}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Stop the other one first, or use --summary to see the state without computing.");
    return 1;
}

using CancellationTokenSource cancellation = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;            // stops Windows from killing the process outright
    cancellation.Cancel();      // the main loop winds down and closes the batch
};

// With several cores computing, writing to the screen becomes the bottleneck.
// The buffer is flushed at the end of every block, so output stays live.
StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
{
    AutoFlush = false,
};
Console.SetOut(output);

using Repository repository = new(databasePath, schemaPath);

BigInteger number = repository.NextNumber();
int reprocessingThreads = stages.Sum(static stage => stage.Threads);

Console.WriteLine();
Console.WriteLine($"{Palette.Title}Palindromes by reverse-and-add{Palette.Reset} {Palette.Muted}— Ctrl+C to stop.{Palette.Reset}");
Console.WriteLine($"{Palette.Label}Database:{Palette.Reset} {Palette.Number}{databasePath}{Palette.Reset}");
Console.WriteLine($"{Palette.Label}Compute:{Palette.Reset} {Palette.Number}{workers}{Palette.Reset} of {Environment.ProcessorCount} cores {Palette.Muted}— blocks of {blockSize:N0} numbers, limit of {MainLimit:N0}.{Palette.Reset}");
Console.WriteLine($"{Palette.Label}Threads:{Palette.Reset} {Palette.Muted}1 orchestrator + {Palette.Reset}{Palette.Number}{workers}{Palette.Reset}{Palette.Muted} compute + {Palette.Reset}{Palette.Number}{reprocessingThreads}{Palette.Reset}{Palette.Muted} reprocessing = {1 + workers + reprocessingThreads}.{Palette.Reset}");

foreach (Stage stage in stages)
{
    Console.WriteLine($"  {Palette.Muted}{stage.Threads} thread(s) [{stage.Name}]{Palette.Reset} {Palette.Label}{stage.SourceTable}{Palette.Reset} {Palette.Muted}-> limit {stage.Limit:N0} ->{Palette.Reset} {Palette.Label}{stage.TargetTable}{Palette.Reset}");
}

Console.WriteLine(number.IsZero
    ? $"{Palette.Muted}No previous results: starting from zero.{Palette.Reset}"
    : $"{Palette.Muted}Resuming from{Palette.Reset} {Palette.Number}{number}{Palette.Reset}{Palette.Muted}.{Palette.Reset}");

Counts counts = repository.Count();

Console.WriteLine();
Console.WriteLine($"{Palette.Label}Rows per table{Palette.Reset}");
WriteCount(Tables.Solved, counts.Solved, Palette.Palindrome, "converged");
WriteCount(Tables.TenThousand, counts.TenThousand, Palette.Warning, $"triage queue ({TriageLimit:N0})");
WriteCount(Tables.HundredThousand, counts.HundredThousand, Palette.Iterations, $"deep stage queue ({DeepLimit:N0})");
WriteCount(Tables.Million, counts.Million, Palette.Iterations, "end of the cascade");
WriteCount("total", counts.Total, Palette.Number, null);

// The reprocessing threads report through this queue; the orchestrator is the
// only one that draws, and drains it at the end of every block.
ConcurrentQueue<Notice> notices = new();
List<Thread> reprocessors = [];

foreach (Stage stage in stages)
{
    for (int i = 1; i <= stage.Threads; i++)
    {
        Reprocessor reprocessor = new(repository, notices, stage, cancellation.Token);

        Thread thread = new(reprocessor.Run)
        {
            IsBackground = true,
            Name = $"reproc-{stage.Name}-{i}",
        };

        thread.Start();
        reprocessors.Add(thread);
    }
}

ParallelOptions options = new()
{
    MaxDegreeOfParallelism = workers,
    CancellationToken = cancellation.Token,
};

// Every thread needs its own Calculator: it carries the digit buffers between
// calls and is not safe for concurrent use. The pool hands them back from one
// block to the next, so the buffers — which reach thousands of digits — are not
// rebuilt every block.
ConcurrentBag<Calculator> pool = [];

string[] sources = new string[blockSize];
Outcome[] outcomes = new Outcome[blockSize];

Stopwatch stopwatch = Stopwatch.StartNew();
long processed = 0;

while (!cancellation.IsCancellationRequested)
{
    for (int i = 0; i < blockSize; i++)
    {
        sources[i] = (number + i).ToString(CultureInfo.InvariantCulture);
    }

    try
    {
        // Parallel.For uses the thread that calls it as one of its workers.
        // Running it inside a Task.Run, all 'workers' workers come from the
        // pool and this thread only waits — it is the orchestrator, and does
        // not consume one of the cores reserved for computing.
        await Task.Run(
            () => Parallel.For(
                0,
                blockSize,
                options,
                () => pool.TryTake(out Calculator? free) ? free : new Calculator(),
                (i, _, calculator) =>
                {
                    outcomes[i] = calculator.Compute(sources[i], MainLimit, cancellation.Token);
                    return calculator;
                },
                pool.Add),
            cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        break;                  // incomplete block: dropped and redone later
    }

    for (int i = 0; i < blockSize; i++)
    {
        Outcome outcome = outcomes[i];
        repository.Record(sources[i], outcome);

        if (processed % RowsPerHeader == 0)
        {
            WriteHeader();
        }

        WriteRow(sources[i], outcome);
        processed++;
    }

    // The block counts as done only once it is stored in full.
    repository.Commit();
    DrainNotices();
    Console.Out.Flush();

    number += blockSize;
}

// Gives the reprocessing threads a chance to leave on their own before the
// repository is closed under them. The deadline is total, not per thread.
DateTime deadline = DateTime.UtcNow.AddSeconds(5);

foreach (Thread thread in reprocessors)
{
    TimeSpan remaining = deadline - DateTime.UtcNow;

    if (remaining > TimeSpan.Zero)
    {
        thread.Join(remaining);
    }
}

repository.Commit();
DrainNotices();
stopwatch.Stop();

TimeSpan elapsed = stopwatch.Elapsed;

string lastNumber = processed == 0
    ? "none"
    : (number - BigInteger.One).ToString(CultureInfo.InvariantCulture);

double perSecond = elapsed.TotalSeconds > 0 ? processed / elapsed.TotalSeconds : 0;

Console.WriteLine();
Console.WriteLine(
    $"{Palette.Title}Stopped.{Palette.Reset} {Palette.Number}{processed:N0}{Palette.Reset} number(s) stored "
    + $"{Palette.Muted}— last:{Palette.Reset} {Palette.Number}{lastNumber}{Palette.Reset} "
    + $"{Palette.Muted}— elapsed:{Palette.Reset} {Palette.Number}{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}{Palette.Reset} "
    + $"{Palette.Muted}({perSecond:N0}/s).{Palette.Reset}");
Console.Out.Flush();

return 0;

// ---------------------------------------------------------------------------

// Opens the database's exclusive lock, or returns null if another instance
// already holds it. FileShare.None is what makes it exclusive; DeleteOnClose
// keeps the file from being left behind.
static FileStream? TryLock(string databasePath)
{
    try
    {
        return new FileStream(
            databasePath + ".lock",
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

static void WriteSummary(string databasePath, string schemaPath, int triageLimit, int deepLimit)
{
    bool existed = File.Exists(databasePath);

    using Repository repository = new(databasePath, schemaPath);
    Summary summary = repository.Summarise();

    Console.WriteLine();
    Console.WriteLine($"{Palette.Title}Database summary{Palette.Reset}");
    Console.WriteLine($"{Palette.Label}File:{Palette.Reset} {Palette.Number}{databasePath}{Palette.Reset} {Palette.Muted}({new FileInfo(databasePath).Length / 1024.0 / 1024.0:N1} MB){Palette.Reset}");

    if (!existed)
    {
        Console.WriteLine($"{Palette.Warning}The database did not exist and was just created, empty.{Palette.Reset}");
    }

    Console.WriteLine();
    Console.WriteLine($"{Palette.Label}Main sequence{Palette.Reset}");
    WriteItem("next number to compute", summary.NextNumber.ToString("N0", CultureInfo.InvariantCulture), Palette.Number);
    WriteItem("palindromes found", summary.Solved.ToString("N0", CultureInfo.InvariantCulture), Palette.Palindrome);

    WriteQueue(Tables.TenThousand, $"triage, limit of {triageLimit:N0}", summary.TenThousand, Palette.Warning);
    WriteQueue(Tables.HundredThousand, $"deep, limit of {deepLimit:N0}", summary.HundredThousand, Palette.Iterations);
    WriteQueue(Tables.Million, "end of the cascade, nothing reprocesses these", summary.Million, Palette.Iterations);

    Console.WriteLine();
    WriteItem("total rows", summary.Total.ToString("N0", CultureInfo.InvariantCulture), Palette.Number);
    Console.WriteLine();
}

static void WriteQueue(string table, string description, QueueState queue, string colour)
{
    Console.WriteLine();
    Console.WriteLine($"{Palette.Label}{table}{Palette.Reset} {Palette.Muted}— {description}{Palette.Reset}");
    WriteItem("how many", queue.Count.ToString("N0", CultureInfo.InvariantCulture), colour);
    WriteItem("next in the queue", queue.Next ?? "empty", Palette.Number);
    WriteItem("last one added", queue.Last ?? "empty", Palette.Muted);
}

static void WriteItem(string label, string value, string colour) =>
    Console.WriteLine($"  {Palette.Muted}{label,-28}{Palette.Reset} {colour}{value,16}{Palette.Reset}");

static void WriteCount(string table, long count, string colour, string? note)
{
    string line = $"  {Palette.Muted}{table,-20}{Palette.Reset} {colour}{count,12:N0}{Palette.Reset}";

    Console.WriteLine(note is null
        ? line
        : $"{line}  {Palette.Muted}{note}{Palette.Reset}");
}

static void WriteHeader()
{
    Console.WriteLine();
    Console.WriteLine($"{Palette.Label}{"Number",10} | {"Palindrome",28} | {"Iterations",12}{Palette.Reset}");
    Console.WriteLine($"{Palette.Rule}{new string('-', 10)}-+-{new string('-', 28)}-+-{new string('-', 12)}{Palette.Reset}");
}

static void WriteRow(string source, Outcome outcome)
{
    string value = outcome.Palindrome ?? "did not converge";
    string colour = outcome.Converged ? Palette.Palindrome : Palette.Warning;

    string iterations = outcome.Converged
        ? outcome.Iterations.ToString("N0", CultureInfo.InvariantCulture)
        : $"> {outcome.Iterations:N0}";

    string bar = $"{Palette.Rule}|{Palette.Reset}";

    Console.WriteLine(
        $"{Palette.Number}{source,10}{Palette.Reset} {bar} "
        + $"{colour}{value,28}{Palette.Reset} {bar} "
        + $"{colour}{iterations,12}{Palette.Reset}");
}

void DrainNotices()
{
    while (notices.TryDequeue(out Notice notice))
    {
        switch (notice.Level)
        {
            case NoticeLevel.Highlight:
                WriteHighlight(notice);
                break;

            case NoticeLevel.Warning:
                Console.WriteLine();
                Console.WriteLine($"{Palette.Iterations}  > {notice.Text}{Palette.Reset}");
                Console.WriteLine();
                break;

            default:
                Console.WriteLine($"{Palette.Muted}  . {notice.Text}{Palette.Reset}");
                break;
        }
    }
}

static void WriteHighlight(Notice notice)
{
    const string Heading = "NEW RESULT";

    string[] body = notice.Detail is null
        ? [notice.Text]
        : [notice.Text, notice.Detail];

    int width = Math.Max(Heading.Length, body.Max(static line => line.Length)) + 2;
    string rule = new('=', width);

    Console.WriteLine();
    Console.WriteLine($"{Palette.Border}+{rule}+{Palette.Reset}");
    Console.WriteLine($"{Palette.Border}|{Palette.Reset}{Palette.Highlight}{(' ' + Heading).PadRight(width)}{Palette.Reset}{Palette.Border}|{Palette.Reset}");

    foreach (string line in body)
    {
        Console.WriteLine($"{Palette.Border}|{Palette.Reset} {Palette.Palindrome}{line.PadRight(width - 1)}{Palette.Reset}{Palette.Border}|{Palette.Reset}");
    }

    Console.WriteLine($"{Palette.Border}+{rule}+{Palette.Reset}");
    Console.WriteLine();
}
