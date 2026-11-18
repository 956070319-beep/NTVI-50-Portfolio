using System.Text.RegularExpressions;
using PiezoScrewSerial.Models;
using PiezoScrewSerial.Protocol;
using PiezoScrewSerial.Transport;

namespace PiezoScrewSerial;

public sealed class PiezoScrewClient : IAsyncDisposable
{
    private static readonly byte[] CarriageReturn = [0x0D];
    private readonly IByteTransport _transport;
    private readonly PiezoScrewResponseFramer _framer;
    private readonly TimeSpan _responseTimeout;
    private readonly PiezoScrewAddressingMode _addressingMode;
    private readonly string _address;
    private readonly bool _appendSerialTerminator;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Queue<PiezoScrewResponse> _completedFrames = new();
    private readonly byte[] _readBuffer = new byte[256];
    private bool _requiresReconnect;

    public PiezoScrewClient(
        IByteTransport transport,
        TimeSpan responseTimeout,
        ReadOnlyMemory<byte>? responseTerminator = null,
        PiezoScrewAddressingMode addressingMode = PiezoScrewAddressingMode.Normal,
        string address = "00",
        bool appendSerialTerminator = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout), "Timeout must be positive.");
        }

        if (addressingMode is not PiezoScrewAddressingMode.Normal and not PiezoScrewAddressingMode.Addressed)
        {
            throw new ArgumentOutOfRangeException(nameof(addressingMode));
        }

        _responseTimeout = responseTimeout;
        _addressingMode = addressingMode;
        _address = PiezoScrewSerialProfile.ValidateAddress(address);
        _appendSerialTerminator = appendSerialTerminator;
        var terminator = responseTerminator ?? CarriageReturn;
        _framer = new PiezoScrewResponseFramer(terminator.Span);
    }

    private async Task<PiezoScrewResponse> SendWhitelistedCommandAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        var request = _appendSerialTerminator
            ? PiezoScrewCommandEncoder.EncodeSerialCommand(commandText, _addressingMode, _address)
            : PiezoScrewCommandEncoder.EncodeTcpCommand(commandText, _addressingMode, _address);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_requiresReconnect)
            {
                throw new InvalidOperationException("Response synchronization was lost; discard and recreate the client before sending another command.");
            }

            if (_completedFrames.Count > 0)
            {
                _requiresReconnect = true;
                var orphanedFrames = string.Join(
                    " | ",
                    _completedFrames.Select(frame =>
                        $"text='{frame.Text.Trim()}',hex={Convert.ToHexString(frame.RawBytes.Span)}"));
                throw new InvalidDataException(
                    "An additional complete frame arrived without a request identifier; refusing to pair it with a later command. " +
                    $"Orphaned frame(s): {orphanedFrames}");
            }

            try
            {
                await _transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                return await ReadNextResponseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _requiresReconnect = true;
                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public Task<PiezoScrewResponse> IdentifyAsync(CancellationToken cancellationToken = default) =>
        SendWhitelistedCommandAsync("MOD?", cancellationToken);

    /// <summary>
    /// Sends a finite D0M/D1M request and returns its ACK. The ACK is not a
    /// motion-completion signal; query STATE? and RMS? separately when needed.
    /// </summary>
    public async Task<PiezoScrewCommandAcknowledgement> DriveCyclesAsync(
        PiezoScrewChannel channel,
        int cycles,
        PiezoScrewDirection direction,
        CancellationToken cancellationToken = default)
    {
        var command = PiezoScrewCommandEncoder.BuildFiniteDriveCommand(channel, cycles, direction);
        return await ExecuteAcknowledgedCommandAsync(command, direction == PiezoScrewDirection.Forward ? "D1M" : "D0M", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sends STP and returns its ACK; read STATE? separately to confirm stop.</summary>
    public async Task<PiezoScrewCommandAcknowledgement> StopAsync(
        PiezoScrewChannel channel,
        CancellationToken cancellationToken = default)
    {
        var command = PiezoScrewCommandEncoder.BuildChannelStopCommand(channel);
        return await ExecuteAcknowledgedCommandAsync(command, "STP", cancellationToken).ConfigureAwait(false);
    }

    public Task<PiezoScrewCommandAcknowledgement> StopAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAcknowledgedCommandAsync(
            PiezoScrewCommandEncoder.BuildStopAllCommand(),
            "STP",
            cancellationToken);

    public async Task<IReadOnlyDictionary<PiezoScrewChannel, PiezoScrewMotionState>> ReadStatesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendWhitelistedCommandAsync("STATE?", cancellationToken).ConfigureAwait(false);
        return PiezoScrewResponseParsers.ParseStates(response);
    }

    public async Task<IReadOnlyDictionary<PiezoScrewChannel, long>> ReadRemainingCyclesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendWhitelistedCommandAsync("RMS?", cancellationToken).ConfigureAwait(false);
        return PiezoScrewResponseParsers.ParseRemainingCycles(response);
    }

    public async Task<IReadOnlyDictionary<PiezoScrewChannel, long>> ReadCumulativeCyclesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendWhitelistedCommandAsync("STEP?", cancellationToken).ConfigureAwait(false);
        if (response.PayloadBytes.IsEmpty)
        {
            throw new InvalidDataException(
                $"STEP? returned an empty payload; raw bytes (hex)={Convert.ToHexString(response.RawBytes.Span)}.");
        }

        return PiezoScrewResponseParsers.ParseCumulativeCycles(response);
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }

    private async Task<PiezoScrewCommandAcknowledgement> ExecuteAcknowledgedCommandAsync(
        string command,
        string expectedResponseName,
        CancellationToken cancellationToken)
    {
        var response = await SendWhitelistedCommandAsync(command, cancellationToken).ConfigureAwait(false);
        var expected = new Regex(
            $"^{Regex.Escape(expectedResponseName)}\\s*\\[OK\\]$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!expected.IsMatch(response.Text.Trim()))
        {
            throw new FormatException(
                $"Expected {expectedResponseName}[OK] acknowledgement, received '{response.Text}'.");
        }

        return new PiezoScrewCommandAcknowledgement(expectedResponseName, response);
    }

    private async Task<PiezoScrewResponse> ReadNextResponseAsync(CancellationToken cancellationToken)
    {
        if (_completedFrames.TryDequeue(out var completed))
        {
            return completed;
        }

        using var timeoutSource = new CancellationTokenSource(_responseTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            while (true)
            {
                var bytesRead = await _transport.ReadAsync(_readBuffer, linkedSource.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("The injected transport ended before a complete response arrived.");
                }

                foreach (var frame in _framer.Append(_readBuffer.AsSpan(0, bytesRead)))
                {
                    // PZTScrew firmware 3.1.0 on COM8 emits a standalone
                    // tilde marker after STATE? (observed as "~~\r"). The
                    // serial manual also documents the tilde suffix for the
                    // RS-485 interface. It is framing metadata, not a reply
                    // payload, so discard only these exact marker frames;
                    // continue rejecting all other unpaired frames.
                    if (frame.Text.Trim() is "~~" or "~~~")
                    {
                        continue;
                    }

                    _completedFrames.Enqueue(frame);
                }

                if (_completedFrames.TryDequeue(out completed))
                {
                    return completed;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No complete response arrived before the configured timeout.");
        }
    }
}
