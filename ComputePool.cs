namespace PalNumbers;

// The workers that compute a block, on threads of their own.
//
// This used to be a Parallel.For, which draws its workers from the thread pool.
// That turned out to be a liability: when the console window is suspended the
// pool stops being able to create worker threads, and a Parallel.For left
// waiting for replicas that never arrive takes the whole sequence down with it.
// Seen in production with the pool holding three workers against a minimum of
// thirty-two, one of them stuck creating the next one.
//
// The threads here are created once, at startup, and live for the life of the
// process. Nothing is ever asked of the pool again, so nothing the pool does or
// fails to do can stop the work — which is exactly why the reprocessing
// threads, dedicated from the start, kept running through every freeze.
//
// Work is handed out one index at a time rather than in fixed slices. Cost per
// number is very uneven, and dynamic hand-out is what keeps the block from
// lasting as long as its unluckiest worker.
internal sealed class ComputePool : IDisposable
{
    private readonly Thread[] workers;
    private readonly SemaphoreSlim start = new(0);
    private readonly CountdownEvent finished;
    private readonly CancellationToken token;
    private readonly int limit;

    private string[] sources = [];
    private Outcome[] outcomes = [];
    private int count;
    private int cursor;
    private volatile bool closing;

    public ComputePool(int size, int limit, CancellationToken token)
    {
        this.limit = limit;
        this.token = token;

        finished = new CountdownEvent(size);
        workers = new Thread[size];

        for (int i = 0; i < size; i++)
        {
            workers[i] = new Thread(Work)
            {
                IsBackground = true,
                Name = $"compute-{i + 1}",
            };

            workers[i].Start();
        }
    }

    // Computes sources[0..count) into outcomes and returns once the block is
    // done, or once cancellation cut it short.
    public void Run(string[] sources, Outcome[] outcomes, int count)
    {
        this.sources = sources;
        this.outcomes = outcomes;
        this.count = count;

        Volatile.Write(ref cursor, 0);
        finished.Reset();
        start.Release(workers.Length);
        finished.Wait();
    }

    private void Work()
    {
        // One calculator per thread, kept for the life of the thread: it
        // carries the digit buffers between calls and is not safe to share.
        Calculator calculator = new();

        while (true)
        {
            start.Wait();

            if (closing)
            {
                finished.Signal();
                return;
            }

            while (!token.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref cursor) - 1;

                if (index >= count)
                {
                    break;
                }

                outcomes[index] = calculator.Compute(sources[index], limit, token);
            }

            finished.Signal();
        }
    }

    public void Dispose()
    {
        closing = true;
        finished.Reset();
        start.Release(workers.Length);
        finished.Wait(TimeSpan.FromSeconds(5));
        finished.Dispose();
        start.Dispose();
    }
}
