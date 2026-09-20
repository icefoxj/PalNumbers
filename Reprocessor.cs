using System.Collections.Concurrent;

namespace PalNumbers;

internal enum NoticeLevel
{
    Muted,
    Highlight,
    Warning,
}

// A message from a reprocessing thread to the orchestrator, which is the only
// one that draws on screen. They never write to the console directly: with
// thousands of lines a second coming out, several threads on the same stream
// would collide in the middle of a line.
internal readonly record struct Notice(NoticeLevel Level, string Text, string? Detail = null);

// One step of the reprocessing cascade: where the numbers come from, the limit
// they are retried with, and where they go if they survive it.
internal readonly record struct Stage(
    string Name,
    string SourceTable,
    string TargetTable,
    int Limit,
    int Threads);

// Reopens, one at a time, the numbers that did not converge, and tries again
// with the limit of its stage.
//
// Each instance is one thread. The work is split into steps because cost grows
// with the square of the iteration count: 100,000 iterations take about nine
// seconds per number and 1,000,000 take about fifteen and a half minutes, both
// measured. Several cheap threads sift the queue and a single expensive one
// receives only what survived.
internal sealed class Reprocessor(
    Repository repository,
    ConcurrentQueue<Notice> notices,
    Stage stage,
    CancellationToken token)
{
    private static readonly TimeSpan EmptyQueueWait = TimeSpan.FromSeconds(2);

    public void Run()
    {
        Calculator calculator = new();

        while (!token.IsCancellationRequested)
        {
            string? number = repository.ClaimPending(stage.SourceTable);

            if (number is null)
            {
                // Nothing free in the queue: either the previous stage has not
                // produced yet, or the other threads took everything there was.
                token.WaitHandle.WaitOne(EmptyQueueWait);
                continue;
            }

            try
            {
                notices.Enqueue(new Notice(
                    NoticeLevel.Muted,
                    $"[{stage.Name}] reprocessing {number} with a limit of {stage.Limit:N0}..."));

                Outcome outcome = calculator.Compute(number, stage.Limit, token);

                if (token.IsCancellationRequested)
                {
                    break;      // incomplete run: the number stays in the queue
                }

                if (outcome.Converged)
                {
                    repository.Promote(stage.SourceTable, number, outcome);

                    notices.Enqueue(new Notice(
                        NoticeLevel.Highlight,
                        $"{number} converged after {outcome.Iterations:N0} iterations",
                        $"palindrome of {outcome.Digits:N0} digits — moved to the main table"));
                }
                else
                {
                    repository.Demote(stage.SourceTable, stage.TargetTable, number, outcome);

                    notices.Enqueue(new Notice(
                        NoticeLevel.Warning,
                        $"[{stage.Name}] {number} survived {stage.Limit:N0} iterations "
                        + $"({outcome.Digits:N0} digits) — moved to {stage.TargetTable}"));
                }
            }
            finally
            {
                repository.ReleaseClaim(number);
            }
        }
    }
}
