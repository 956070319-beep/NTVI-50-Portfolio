using PiezoScrewSerial.Transport;

namespace PiezoScrewSerial.OfflineTests;

internal sealed class FakeByteTransport : IByteTransport
{
    private readonly Queue<byte[]> _reads = new();
    private bool _disposed;

    public Func<byte[], IEnumerable<byte[]>>? ResponseFactory { get; set; }

    public List<byte[]> Writes { get; } = [];

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = bytes.ToArray();
        Writes.Add(request);
        foreach (var chunk in ResponseFactory?.Invoke(request) ?? [])
        {
            _reads.Enqueue(chunk);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reads.Count == 0)
        {
            return ValueTask.FromResult(0);
        }

        var next = _reads.Dequeue();
        var count = Math.Min(buffer.Length, next.Length);
        next.AsMemory(0, count).CopyTo(buffer);
        if (count < next.Length)
        {
            _reads.Enqueue(next[count..]);
        }

        return ValueTask.FromResult(count);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _reads.Clear();
        return ValueTask.CompletedTask;
    }
}
