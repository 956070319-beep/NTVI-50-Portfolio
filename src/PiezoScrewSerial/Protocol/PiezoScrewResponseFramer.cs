namespace PiezoScrewSerial.Protocol;

public sealed class PiezoScrewResponseFramer
{
    private readonly byte[] _terminator;
    private readonly List<byte> _pending = [];
    private readonly int _maximumFrameLength;

    public PiezoScrewResponseFramer(
        ReadOnlySpan<byte> terminator,
        int maximumFrameLength = 4096)
    {
        if (terminator.IsEmpty)
        {
            throw new ArgumentException("A response terminator is required.", nameof(terminator));
        }

        if (maximumFrameLength < terminator.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
        }

        _terminator = terminator.ToArray();
        _maximumFrameLength = maximumFrameLength;
    }

    public static PiezoScrewResponseFramer ForDocumentedCarriageReturn() =>
        new([0x0D]);

    public IReadOnlyList<PiezoScrewResponse> Append(ReadOnlySpan<byte> bytes)
    {
        var completed = new List<PiezoScrewResponse>();

        foreach (var value in bytes)
        {
            _pending.Add(value);

            if (_pending.Count > _maximumFrameLength)
            {
                _pending.Clear();
                throw new InvalidDataException($"Response exceeded {_maximumFrameLength} bytes without a terminator.");
            }

            if (!EndsWithTerminator())
            {
                continue;
            }

            completed.Add(new PiezoScrewResponse(_pending.ToArray(), _terminator.Length));
            _pending.Clear();
        }

        return completed;
    }

    public void Reset() => _pending.Clear();

    private bool EndsWithTerminator()
    {
        if (_pending.Count < _terminator.Length)
        {
            return false;
        }

        var offset = _pending.Count - _terminator.Length;
        for (var index = 0; index < _terminator.Length; index++)
        {
            if (_pending[offset + index] != _terminator[index])
            {
                return false;
            }
        }

        return true;
    }
}
