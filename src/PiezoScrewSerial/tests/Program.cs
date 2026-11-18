using System.Text;
using System.Reflection;
using PiezoScrewSerial.Models;
using PiezoScrewSerial.Protocol;
using PiezoScrewSerial.Transport;

namespace PiezoScrewSerial.OfflineTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("finite motion frame appends ')' and not CR", TestFiniteDriveFrame),
            ("addressed mode prefixes the configured two-digit address", TestAddressedModeFrame),
            ("Micro-USB profile matches the documented line settings", TestSerialProfileSettings),
            ("finite drive rejects zero before writing", TestFiniteDriveRejectsZero),
            ("public API has no raw execute or continuous motion path", TestPublicApiSurface),
            ("response framer handles chunks and retains raw terminator", TestResponseFramer),
            ("STATE? parser maps documented state values", TestStateParser),
            ("RMS? parser accepts concatenated channel values", TestRmsParser),
            ("STEP? parser accepts comma-separated values", TestStepParser),
            ("empty STEP? response reports raw hex and addressed frame", TestEmptyStepResponseReportsRawHexAndAddressedFrame),
            ("client uses injected transport for a query", TestInjectedTransport),
            ("read-only queries use normal and addressed frames", TestReadOnlyQueryFrames),
            ("MOD? and STP use documented command frames", TestIdentifyAndStopFrames),
            ("ACK, motion state, remaining cycles, and cumulative cycles stay distinct", TestAckStateAndCycleCountsSeparate),
            ("client refuses to pair an extra frame with a later query", TestExtraFrame),
            ("motion timeout does not retry and requires resynchronization", TestMotionTimeoutDoesNotRetry)
        };

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} offline checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestFiniteDriveFrame()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = _ => [Encoding.ASCII.GetBytes("D1M [OK]\r")]
        };
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        var acknowledgement = await client.DriveCyclesAsync(
            PiezoScrewChannel.Channel1,
            1000,
            PiezoScrewDirection.Forward);

        var frame = fake.Writes.Single();
        Equal("D1M 1 1000)", Encoding.ASCII.GetString(frame));
        True(frame[^1] != 0x0D, "The request terminator must not be CR.");
        Equal("D1M", acknowledgement.CommandName);
        Equal("D1M [OK]", acknowledgement.Text);
    }

    private static async Task TestAddressedModeFrame()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = _ => [Encoding.ASCII.GetBytes("D0M [OK]\r")]
        };
        await using var client = new PiezoScrewClient(
            fake,
            TimeSpan.FromSeconds(1),
            addressingMode: PiezoScrewAddressingMode.Addressed,
            address: "03");

        await client.DriveCyclesAsync(
            PiezoScrewChannel.Channel1,
            10,
            PiezoScrewDirection.Reverse);

        Equal("03D0M 1 10)", Encoding.ASCII.GetString(fake.Writes.Single()));
    }

    private static Task TestSerialProfileSettings()
    {
        var profile = PiezoScrewSerialProfile.ForMicroUsb("PORT-FROM-CONFIG");

        Equal("PORT-FROM-CONFIG", profile.PortName);
        Equal(921_600, profile.BaudRate);
        Equal(8, profile.DataBits);
        Equal(1, profile.StopBits);
        True(!profile.ParityEnabled, "Micro-USB parity must be disabled.");
        True(!profile.HandshakeEnabled, "The transport must not enable hardware flow control.");
        Equal(PiezoScrewAddressingMode.Normal, profile.AddressingMode);
        Equal("00", profile.Address);
        True(!profile.UsesAddressPrefix, "Normal mode must not add an address prefix.");

        var addressed = PiezoScrewSerialProfile.ForMicroUsb(
            "PORT-FROM-CONFIG",
            PiezoScrewAddressingMode.Addressed);
        True(addressed.UsesAddressPrefix, "Addressed mode must add its configured prefix.");
        Equal("00", addressed.Address);
        return Task.CompletedTask;
    }

    private static async Task TestFiniteDriveRejectsZero()
    {
        var fake = new FakeByteTransport();
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        foreach (var direction in Enum.GetValues<PiezoScrewDirection>())
        {
            await ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.DriveCyclesAsync(
                    PiezoScrewChannel.Channel1,
                    0,
                    direction));
        }

        Equal(0, fake.Writes.Count);
    }

    private static Task TestPublicApiSurface()
    {
        var publicMethods = typeof(PiezoScrewClient)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.DeclaringType == typeof(PiezoScrewClient))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var expectedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "IdentifyAsync",
            "DriveCyclesAsync",
            "StopAsync",
            "StopAllAsync",
            "ReadStatesAsync",
            "ReadRemainingCyclesAsync",
            "ReadCumulativeCyclesAsync",
            "DisposeAsync"
        };

        True(publicMethods.SetEquals(expectedMethods), $"Unexpected public methods: {string.Join(", ", publicMethods)}");
        True(typeof(PiezoScrewClient).GetMethod("ExecuteCommandAsync", BindingFlags.Instance | BindingFlags.Public) is null,
            "Raw command execution must not be public.");
        True(typeof(PiezoScrewClient).GetMethod("RunContinuouslyAsync", BindingFlags.Instance | BindingFlags.Public) is null,
            "Continuous motion must not be public.");

        var encoderType = typeof(PiezoScrewClient).Assembly.GetType("PiezoScrewSerial.Protocol.PiezoScrewCommandEncoder")
            ?? throw new InvalidOperationException("The internal command encoder type is missing.");
        True(!encoderType.IsPublic, "The raw command encoder must be internal.");
        True(encoderType.GetMethod("EncodeSerialCommand", BindingFlags.Static | BindingFlags.Public) is null,
            "Raw command framing must not be public.");
        True(encoderType.GetMethod("BuildContinuousDriveCommand", BindingFlags.Static | BindingFlags.Public) is null,
            "Continuous command building must not be public.");
        return Task.CompletedTask;
    }

    private static Task TestResponseFramer()
    {
        var framer = PiezoScrewResponseFramer.ForDocumentedCarriageReturn();
        var first = framer.Append(Encoding.ASCII.GetBytes("STATE:1=0,2="));
        Equal(0, first.Count);

        var second = framer.Append(Encoding.ASCII.GetBytes("2\r"));
        Equal(1, second.Count);
        Equal("STATE:1=0,2=2", second[0].Text);
        Equal("STATE:1=0,2=2\r", Encoding.ASCII.GetString(second[0].RawBytes.Span));
        Equal("\r", Encoding.ASCII.GetString(second[0].TerminatorBytes.Span));
        return Task.CompletedTask;
    }

    private static Task TestStateParser()
    {
        var response = Response("STATE:1=0,2=2,3=1,4=2\r");
        var states = PiezoScrewResponseParsers.ParseStates(response);

        Equal(PiezoScrewMotionState.Reverse, states[PiezoScrewChannel.Channel1]);
        Equal(PiezoScrewMotionState.Stopped, states[PiezoScrewChannel.Channel2]);
        Equal(PiezoScrewMotionState.Forward, states[PiezoScrewChannel.Channel3]);
        Equal(PiezoScrewMotionState.Stopped, states[PiezoScrewChannel.Channel4]);
        return Task.CompletedTask;
    }

    private static Task TestRmsParser()
    {
        var response = Response("1=5002=03=04=0\r");
        var cycles = PiezoScrewResponseParsers.ParseRemainingCycles(response);

        Equal(500L, cycles[PiezoScrewChannel.Channel1]);
        Equal(0L, cycles[PiezoScrewChannel.Channel2]);
        Equal(0L, cycles[PiezoScrewChannel.Channel3]);
        Equal(0L, cycles[PiezoScrewChannel.Channel4]);
        return Task.CompletedTask;
    }

    private static Task TestStepParser()
    {
        var response = Response("STEP:1=500,2=0,3=17,4=23\r");
        var cycles = PiezoScrewResponseParsers.ParseCumulativeCycles(response);

        Equal(500L, cycles[PiezoScrewChannel.Channel1]);
        Equal(0L, cycles[PiezoScrewChannel.Channel2]);
        Equal(17L, cycles[PiezoScrewChannel.Channel3]);
        Equal(23L, cycles[PiezoScrewChannel.Channel4]);
        return Task.CompletedTask;
    }

    private static async Task TestEmptyStepResponseReportsRawHexAndAddressedFrame()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = _ => [new byte[] { 0x0D }]
        };
        await using var client = new PiezoScrewClient(
            fake,
            TimeSpan.FromSeconds(1),
            addressingMode: PiezoScrewAddressingMode.Addressed,
            address: "00");

        InvalidDataException? failure = null;
        try
        {
            await client.ReadCumulativeCyclesAsync().ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            failure = exception;
        }

        True(failure is not null, "An empty STEP? payload must not be interpreted as zero.");
        True(failure!.Message.Contains("0D", StringComparison.Ordinal), "The diagnostic must include the raw response bytes in hex.");
        Equal("00STEP?)", Encoding.ASCII.GetString(fake.Writes.Single()));
        Equal("3030535445503F29", Convert.ToHexString(fake.Writes.Single()));
    }

    private static async Task TestInjectedTransport()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = _ => [Encoding.ASCII.GetBytes("STATE:1=2\r")]
        };
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        var states = await client.ReadStatesAsync().ConfigureAwait(false);

        Equal("STATE?)", Encoding.ASCII.GetString(fake.Writes.Single()));
        Equal(PiezoScrewMotionState.Stopped, states[PiezoScrewChannel.Channel1]);
    }

    private static async Task TestAckStateAndCycleCountsSeparate()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = request => Encoding.ASCII.GetString(request) switch
            {
                "D1M 1 10)" => [Encoding.ASCII.GetBytes("D1M [OK]\r")],
                "STATE?)" => [Encoding.ASCII.GetBytes("STATE:1=2\r")],
                "RMS?)" => [Encoding.ASCII.GetBytes("1=0\r")],
                "STEP?)" => [Encoding.ASCII.GetBytes("STEP:1=10\r")],
                var unexpected => throw new InvalidOperationException($"Unexpected fake request: {unexpected}")
            }
        };
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        var acknowledgement = await client.DriveCyclesAsync(
            PiezoScrewChannel.Channel1,
            10,
            PiezoScrewDirection.Forward);
        var state = await client.ReadStatesAsync();
        var remaining = await client.ReadRemainingCyclesAsync();
        var cumulative = await client.ReadCumulativeCyclesAsync();

        Equal("D1M", acknowledgement.CommandName);
        Equal(PiezoScrewMotionState.Stopped, state[PiezoScrewChannel.Channel1]);
        Equal(0L, remaining[PiezoScrewChannel.Channel1]);
        Equal(10L, cumulative[PiezoScrewChannel.Channel1]);
        Equal(4, fake.Writes.Count);
        True(
            fake.Writes.Select(Encoding.ASCII.GetString).SequenceEqual(
                ["D1M 1 10)", "STATE?)", "RMS?)", "STEP?)"]),
            "ACK, state, remaining-cycle, and cumulative-cycle requests must stay distinct.");
    }

    private static async Task TestIdentifyAndStopFrames()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = request => Encoding.ASCII.GetString(request) switch
            {
                "MOD?)" => [Encoding.ASCII.GetBytes("PE201-2505008-SN1-V1.2.5\r")],
                "STP 2)" => [Encoding.ASCII.GetBytes("STP[OK]\r")],
                "STP)" => [Encoding.ASCII.GetBytes("STP[OK]\r")],
                var unexpected => throw new InvalidOperationException($"Unexpected fake request: {unexpected}")
            }
        };
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        var identity = await client.IdentifyAsync();
        var stopChannel = await client.StopAsync(PiezoScrewChannel.Channel2);
        var stopAll = await client.StopAllAsync();

        Equal("PE201-2505008-SN1-V1.2.5", identity.Text);
        Equal("STP", stopChannel.CommandName);
        Equal("STP", stopAll.CommandName);
        True(
            fake.Writes.Select(Encoding.ASCII.GetString).SequenceEqual(["MOD?)", "STP 2)", "STP)"]),
            "MOD? and STP frames must match the documented serial syntax.");
    }

    private static async Task TestReadOnlyQueryFrames()
    {
        static FakeByteTransport CreateQueryTransport() => new()
        {
            ResponseFactory = request => Encoding.ASCII.GetString(request) switch
            {
                "STATE?)" or "00STATE?)" => [Encoding.ASCII.GetBytes("STATE:1=2\r")],
                "RMS?)" or "00RMS?)" => [Encoding.ASCII.GetBytes("1=0\r")],
                "STEP?)" or "00STEP?)" => [Encoding.ASCII.GetBytes("STEP:1=123\r")],
                var unexpected => throw new InvalidOperationException($"Unexpected fake request: {unexpected}")
            }
        };

        var normalTransport = CreateQueryTransport();
        await using (var normalClient = new PiezoScrewClient(normalTransport, TimeSpan.FromSeconds(1)))
        {
            await normalClient.ReadStatesAsync();
            await normalClient.ReadRemainingCyclesAsync();
            await normalClient.ReadCumulativeCyclesAsync();
        }
        True(
            normalTransport.Writes.Select(Encoding.ASCII.GetString).SequenceEqual(
                ["STATE?)", "RMS?)", "STEP?)"]),
            "Normal mode read-only frames must not include an address.");

        var addressedTransport = CreateQueryTransport();
        await using (var addressedClient = new PiezoScrewClient(
            addressedTransport,
            TimeSpan.FromSeconds(1),
            addressingMode: PiezoScrewAddressingMode.Addressed,
            address: "00"))
        {
            await addressedClient.ReadStatesAsync();
            await addressedClient.ReadRemainingCyclesAsync();
            await addressedClient.ReadCumulativeCyclesAsync();
        }
        True(
            addressedTransport.Writes.Select(Encoding.ASCII.GetString).SequenceEqual(
                ["00STATE?)", "00RMS?)", "00STEP?)"]),
            "Addressed mode read-only frames must use the configured two-digit prefix.");
    }

    private static async Task TestExtraFrame()
    {
        var fake = new FakeByteTransport
        {
            ResponseFactory = _ => [Encoding.ASCII.GetBytes("STATE:1=2\rEXTRA\r")]
        };
        await using var client = new PiezoScrewClient(fake, TimeSpan.FromSeconds(1));

        await client.ReadStatesAsync().ConfigureAwait(false);
        await ThrowsAsync<InvalidDataException>(() => client.IdentifyAsync());

        Equal(1, fake.Writes.Count);
    }

    private static async Task TestMotionTimeoutDoesNotRetry()
    {
        var transport = new HangingByteTransport();
        await using var client = new PiezoScrewClient(transport, TimeSpan.FromMilliseconds(25));

        await ThrowsAsync<TimeoutException>(() => client.DriveCyclesAsync(
            PiezoScrewChannel.Channel1,
            10,
            PiezoScrewDirection.Forward));

        Equal(1, transport.Writes.Count);
        await ThrowsAsync<InvalidOperationException>(() => client.ReadStatesAsync());
        Equal(1, transport.Writes.Count);
    }

    private static PiezoScrewResponse Response(string raw)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        return new PiezoScrewResponse(bytes, terminatorLength: 1);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class HangingByteTransport : IByteTransport
    {
        private bool _disposed;

        public List<byte[]> Writes { get; } = [];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            Writes.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
