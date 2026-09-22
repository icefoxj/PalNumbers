# PalNumbers

Searches for delayed palindromes by reverse-and-add, and collects the numbers
that refuse to produce one.

Take a number, add it to its reverse, and check whether the result is a
palindrome. If it isn't, repeat on the result:

```
12  ->  12 + 21 = 33                        1 iteration
89  ->  ... -> 8813200023188               24 iterations
196 ->  ... still not a palindrome after 1,000,000 iterations
```

Most numbers converge in a handful of steps. A few need dozens. And some —
starting with 196, the smallest known candidate — have never been shown to
converge at all. Those are called Lychrel candidates, and this program is built
to find and catalogue them.

## What it does

It walks `0, 1, 2, 3, ...` with no upper bound, writing every result to a
SQLite database next to the executable, and stops only on `Ctrl+C`. The next run
resumes from where it left off.

Numbers that fail the first pass are not discarded. They descend a cascade of
increasingly expensive retries, each handled by its own dedicated threads:

```
main sequence (limit 10,000)
     |  failed
     v
NaoConvergiram  --( 4 threads, limit 100,000 )-->  converged: Palindromos
     |  failed
     v
NaoConvergiram100k  --( 1 thread, limit 1,000,000 )-->  converged: Palindromos
     |  failed
     v
NaoConvergiram1M  (terminal)
```

The cascade exists because cost grows with the *square* of the iteration count.
Measured on the reference machine, a single number costs:

| iterations | digits reached | time |
|---|---|---|
| 10,000 | 4,159 | ~0.02 s |
| 100,000 | 41,490 | 9.2 s |
| 500,000 | 207,040 | 232.1 s |
| 1,000,000 | 414,302 | 931.4 s |

Four cheap threads sift the queue; the one expensive thread only ever sees what
survived. A number reaching the bottom of the cascade has had a million
reverse-and-add steps applied to it and is over 400,000 digits long.

## Usage

```
PalNumbers                    run the sequence until interrupted
PalNumbers --summary          print the state of the database and exit
PalNumbers --stop             ask every running instance to stop, and wait
PalNumbers --create-db <path> create or update a database at that path
```

`--stop` finds every running instance, whatever database it is working on, and
asks it to wind down through the same path `Ctrl+C` takes: the block in flight
is dropped rather than half written, the batch is closed and the instance prints
its own summary. It is a request, not a kill — each instance is signalled
through a named event it listens on, and `--stop` waits and reports what
actually went:

```
Found 2 running instance(s).
  pid 83376: could not be reached — stop it with Ctrl+C in its own window
  pid 41520: stop requested
  pid 41520: stopped

1 of 2 instance(s) stopped.
```

An instance is out of reach when it is an older build with no listener, or when
it runs in a different logon session. The exit code is 0 only when every
instance found was stopped.

`--summary` performs no computation and can be run while another instance is
working — SQLite's WAL mode allows reading during writes:

```
Database summary
File: ...\PalNumbers.db (158.0 MB)

Main sequence
  next number to compute              2,129,920
  palindromes found                   1,803,827

NaoConvergiram — triage, limit of 100,000
  how many                              326,091
  next in the queue                         394

NaoConvergiram100k — deep, limit of 1,000,000
  how many                                    0

NaoConvergiram1M — end of the cascade, nothing reprocesses these
  how many                                    2
  next in the queue                         196
```

Console output is colour-coded via ANSI escapes. Colours switch off
automatically when output is redirected, and honour the `NO_COLOR` and
`FORCE_COLOR` conventions.

Nothing that computes writes to the console. A console can stop accepting output
at any moment and for as long as it likes — a selection in the window, Mark
mode, `Ctrl+S`, the Pause key, a terminal that stops draining — and every write
then blocks. Printing inline from the main loop would not pause the display, it
would pause the program: the sequence stops mid-block, and even `Ctrl+C` does
nothing, because the thread that would notice the cancellation is the one stuck
writing. That happened twice in production, once for hours.

So the screen has a thread of its own and a bounded queue. When the console
stops accepting output the queue fills, lines are dropped and counted, and the
computation carries on; the run says afterwards how many lines it never showed.
A gap in the listing is never a gap in the work — the database has everything.
The run also turns QuickEdit selection off, which removes the most common
trigger, at the cost of mouse selection in that window.

## Thread layout

On a machine with 32 logical processors:

```
 1  orchestrator   builds blocks, writes to the database, draws the screen
16  calculation    50% of the logical processors, the main sequence
 4  triage         reprocessing at the 100,000 limit
 1  deep           reprocessing at the 1,000,000 limit
--
22  threads
```

Only the 16 calculation threads count against the core budget. The
orchestrator is deliberately kept out of it: `Parallel.For` normally runs work
on its calling thread, so it is invoked inside a `Task.Run` and the main thread
does nothing but coordinate.

Numbers are computed in blocks of 8,192. The whole block is solved in parallel,
then written and printed in ascending order. That preserves two properties
plain parallelism would break: the screen stays ordered, and the database never
holds a number while a smaller one is missing — which is exactly what makes
"resume from the largest number stored" correct.

Blocks are large on purpose. Per-number cost is wildly uneven: a number that
fails the first pass costs about a thousand ordinary numbers. In a small block
there are too few expensive items to spread across the workers, and the block
ends up lasting as long as the unluckiest thread. Measured with 16 workers:

| block size | effective cores | throughput |
|---|---|---|
| 1,024 | 9.6 | 2,414/s |
| 4,096 | 13.4 | 3,095/s |
| 8,192 | 14.2 | 4,949/s |

## How the arithmetic works

The naive approach — hold the value in a `BigInteger` and call `ToString()` to
reverse it and to test for a palindrome — is quadratic per iteration, because
the binary-to-decimal conversion is. With the digit count growing linearly, the
total cost grows with the *cube* of the iteration limit. Extrapolated, a single
number at the 1,000,000 limit would take about nine days.

`Calculadora` instead keeps the number as a vector of decimal digits, least
significant first. Reverse-and-add becomes one pass adding each position to its
mirror, and the palindrome test walks the same vector from both ends. Both are
linear, and nothing is allocated per iteration. That is what makes the million
tractable at about fifteen minutes instead of days.

The value reached by a number that fails is not stored, and not even formatted.
It is not a result — it is an intermediate the program never reads back, since
reprocessing restarts from the original number. Keeping it cost around 4 KB per
row and accounted for roughly 87% of the database size. The digit count is kept
instead.

## Database

SQLite, created next to the executable on first run from `Database/Schema.sql`,
which is copied alongside the binary. The schema is idempotent, so adding a
table to a database that already holds data is a no-op for the existing rows.

Table and column names are in Portuguese. They stay that way because there are
millions of rows behind them, and renaming would mean migrating a live database
for nothing but spelling.

| table | holds |
|---|---|
| `Palindromos` | number, palindrome found, iterations, timestamp |
| `NaoConvergiram` | failed at 10,000 — queue for triage |
| `NaoConvergiram100k` | failed at 100,000 — queue for the deep stage |
| `NaoConvergiram1M` | failed at 1,000,000 — end of the cascade |

Writes are batched into transactions: every commit costs a disk flush, and
committing row by row would drop throughput from thousands to hundreds of
numbers per second. A batch closes at 500 rows or 2 seconds, and a block is
never reported as done until it is written in full.

All threads share one connection, serialised by a lock. SQLite permits only one
writer at a time, and a second connection would hit `SQLITE_BUSY` while the
orchestrator's batch was open.

Reprocessing takes the smallest pending number first, which means ordering by
`(LENGTH(Numero), Numero)` — numeric order for text holding non-negative
integers. Each queue carries an index on that expression. Without it SQLite
scans the table and builds a temporary B-tree on every claim: 68 ms on a queue
of 781,000 rows, held under the lock every thread shares, and growing with the
queue.

Two processes sharing a database would both resume from the same point and the
second would crash on the uniqueness constraint. A lock file next to the
database prevents it; the operating system releases it even if the process dies
badly.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
dotnet publish -c Release        # lands in Producao/
```

`publish` produces a self-contained folder with the executable, the schema
script and its database. Publishing over a folder already in use updates the
binaries without touching the data.

The build is framework-dependent and needs the .NET 10 runtime on the target
machine. For a machine without it:

```
dotnet publish -c Release -r win-x64 --self-contained
```

## Results so far

From a run reaching 2.1 million numbers:

```
196 | 1,000,000 iterations | 414,302 digits
295 | 1,000,000 iterations | 414,302 digits
```

Both stop at the same value, which is correct rather than suspicious: 196 and
295 each sum to 887 after one step and share every step from then on.
