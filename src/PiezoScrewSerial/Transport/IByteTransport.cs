namespace PiezoScrewSerial.Transport;

public interface IByteTransport : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
