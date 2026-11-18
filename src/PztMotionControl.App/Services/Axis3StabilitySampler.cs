using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using PztMotionControl.App.Hardware;

namespace PztMotionControl.App.Services;

public sealed record Axis3StableWindow(double Median, long RangeCounts, Axis3Sample LastSample);

public static class Axis3StabilitySampler
{
    private const long MaximumExactIntegerFloat = 16_777_216;

    public static async Task<Axis3StableWindow> WaitForStableWindowAsync(
        IAxis3ScaleReader reader,
        int sampleCount,
        double maximumSpanCounts,
        TimeSpan sampleInterval,
        TimeSpan timeout,
        System.Action<Axis3Sample>? validateSample = null,
        System.Func<bool>? cancellationRequested = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (sampleCount < 2)
            throw new System.ArgumentOutOfRangeException(nameof(sampleCount), "稳定窗口至少需要 2 个样本。");
        if (!double.IsFinite(maximumSpanCounts) || maximumSpanCounts < 0)
            throw new System.ArgumentOutOfRangeException(nameof(maximumSpanCounts));
        if (sampleInterval <= TimeSpan.Zero || timeout <= TimeSpan.Zero)
            throw new System.ArgumentOutOfRangeException(nameof(timeout), "采样间隔与稳定判定超时必须大于 0。");

        var minimumWindowDuration = TimeSpan.FromTicks(sampleInterval.Ticks * (sampleCount - 1));
        if (timeout < minimumWindowDuration)
            throw new System.InvalidOperationException(
                $"稳定判定超时至少需要 {minimumWindowDuration.TotalMilliseconds:0} ms，才能采集 {sampleCount} 个样本。");

        var timer = Stopwatch.StartNew();
        var samples = new Queue<Axis3Sample>(sampleCount);
        long lastRange = long.MaxValue;
        long lastSequence = 0;

        while (timer.Elapsed < timeout)
        {
            if (cancellationRequested?.Invoke() == true)
                throw new System.OperationCanceledException("收到停止请求，已中止光栅尺稳定采样。");

            var sample = await Task.Run(reader.ReadRawCount).ConfigureAwait(false);
            if (sample.Sequence <= lastSequence || sample.Sequence <= 0 ||
                DateTimeOffset.UtcNow - sample.CapturedAtUtc > TimeSpan.FromSeconds(1))
                throw new System.InvalidOperationException("光栅尺样本无效、重复或过期。");
            if (sample.RawCount < -MaximumExactIntegerFloat || sample.RawCount > MaximumExactIntegerFloat)
                throw new System.InvalidOperationException("光栅尺计数超出运动卡浮点读数的精确范围。");
            validateSample?.Invoke(sample);
            lastSequence = sample.Sequence;

            samples.Enqueue(sample);
            if (samples.Count > sampleCount)
                samples.Dequeue();

            if (samples.Count == sampleCount)
            {
                var ordered = samples.Select(item => item.RawCount).Order().ToArray();
                lastRange = ordered[^1] - ordered[0];
                if (lastRange <= maximumSpanCounts)
                {
                    var middle = ordered.Length / 2;
                    var median = ordered.Length % 2 == 0
                        ? (ordered[middle - 1] + ordered[middle]) / 2.0
                        : ordered[middle];
                    return new Axis3StableWindow(median, lastRange, samples.Last());
                }
            }

            if (timer.Elapsed < timeout)
                await Task.Delay(sampleInterval).ConfigureAwait(false);
        }

        throw new System.TimeoutException(
            $"等待光栅尺稳定超时；最近 {sampleCount} 点跨度 {lastRange} count，要求不超过 {maximumSpanCounts:0.###} count。");
    }
}
