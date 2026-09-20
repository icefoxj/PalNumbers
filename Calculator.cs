namespace PalNumbers;

// The outcome for one number. Palindrome is filled in only when the number
// converged; otherwise it stays null and Digits carries the size reached when
// the limit ran out.
//
// Not formatting the value of a number that fails is deliberate: it would be a
// string thousands of digits long — hundreds of thousands at the extended
// limit — built for every failure and read by nobody.
internal readonly record struct Outcome(bool Converged, string? Palindrome, int Iterations, int Digits);

// Applies reverse-and-add over a vector of decimal digits, least significant
// first.
//
// Working on the digits rather than on a BigInteger is what keeps each
// iteration linear. Converting between BigInteger and text costs O(d²), and
// with d growing by roughly one digit every two iterations the total would grow
// with the cube of the limit — about nine days for a single number at a million
// iterations, against the fifteen minutes this takes.
//
// The buffers are reused between calls: they grow to the worst case seen so far
// and then stop allocating.
internal sealed class Calculator
{
    private byte[] current = new byte[64];
    private byte[] next = new byte[64];
    private int length;

    public Outcome Compute(ReadOnlySpan<char> number, int limit, CancellationToken token)
    {
        Load(number);

        for (int iterations = 1; iterations <= limit; iterations++)
        {
            if (token.IsCancellationRequested)
            {
                break;          // keeps Ctrl+C responsive on long numbers
            }

            AddReverse();

            if (IsPalindrome())
            {
                return new Outcome(Converged: true, Format(), iterations, length);
            }
        }

        return new Outcome(Converged: false, Palindrome: null, limit, length);
    }

    private void Load(ReadOnlySpan<char> number)
    {
        Reserve(number.Length + 1);
        length = number.Length;

        for (int i = 0; i < length; i++)
        {
            current[i] = (byte)(number[length - 1 - i] - '0');
        }
    }

    private void AddReverse()
    {
        Reserve(length + 1);

        int n = length;
        int carry = 0;

        for (int i = 0; i < n; i++)
        {
            int sum = current[i] + current[n - 1 - i] + carry;

            // sum <= 9 + 9 + 1, so the carry is always 0 or 1.
            if (sum >= 10)
            {
                next[i] = (byte)(sum - 10);
                carry = 1;
            }
            else
            {
                next[i] = (byte)sum;
                carry = 0;
            }
        }

        if (carry > 0)
        {
            next[n] = 1;
            n++;
        }

        (current, next) = (next, current);
        length = n;
    }

    private bool IsPalindrome()
    {
        for (int i = 0, j = length - 1; i < j; i++, j--)
        {
            if (current[i] != current[j])
            {
                return false;
            }
        }

        return true;
    }

    private string Format() =>
        string.Create(length, this, static (destination, state) =>
        {
            int n = state.length;

            for (int i = 0; i < n; i++)
            {
                destination[i] = (char)('0' + state.current[n - 1 - i]);
            }
        });

    private void Reserve(int capacity)
    {
        if (current.Length >= capacity)
        {
            return;
        }

        int size = Math.Max(capacity, current.Length * 2);
        Array.Resize(ref current, size);
        next = new byte[size];
    }
}
