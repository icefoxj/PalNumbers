using System.Collections.Concurrent;
using System.Text;

namespace PalNumbers;

// Owns the console and writes to it from a thread of its own.
//
// Nothing that computes may write to the console directly. A console can stop
// accepting output at any moment and for as long as it likes: a selection in
// the window, Mark mode, Ctrl+S, the Pause key, a terminal that stops draining.
// While it is suspended, every write blocks. With printing done inline from the
// main loop that does not pause the display — it pauses the program, mid-block,
// and even Ctrl+C has no effect, because the thread that would notice the
// cancellation is the one stuck writing. That happened twice in production,
// once for hours.
//
// So lines go into a bounded queue and this thread drains it. If the console
// stops accepting output the queue fills and new lines are dropped, counted,
// and the computation carries on. The screen is a view of the work, never a
// condition for it — and the database has everything regardless.
internal sealed class Screen : IDisposable
{
    private readonly BlockingCollection<string> queue;
    private readonly TextWriter output;
    private readonly Thread worker;

    private long dropped;

    public Screen(int capacity)
    {
        queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), capacity);

        output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
        {
            AutoFlush = false,
        };

        worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "screen",
        };

        worker.Start();
    }

    // Lines thrown away because the console was not accepting output.
    public long Dropped => Interlocked.Read(ref dropped);

    // Never blocks, never throws: the caller is computing and must not be held
    // up by the screen.
    public void Write(string line)
    {
        if (queue.IsAddingCompleted || !queue.TryAdd(line))
        {
            Interlocked.Increment(ref dropped);
        }
    }

    public void WriteBlank() => Write(string.Empty);

    private void Pump()
    {
        foreach (string line in queue.GetConsumingEnumerable())
        {
            output.WriteLine(line);

            // Flushing only once the queue has run dry keeps the syscalls down
            // while still showing everything as soon as there is nothing left.
            if (queue.Count == 0)
            {
                output.Flush();
            }
        }

        output.Flush();
    }

    // Closes the queue and gives the writer a moment to land what is left. The
    // wait is bounded because the console may still be suspended, and the
    // program must be able to exit anyway — the thread is a background one.
    public void Dispose()
    {
        queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(5));
    }
}
