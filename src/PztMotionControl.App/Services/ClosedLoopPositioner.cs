using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiezoScrewSerial.Models;
using PztMotionControl.App.Hardware;
using PztMotionControl.App.Models;

namespace PztMotionControl.App.Services;

public sealed class ClosedLoopPositioner
{
	private readonly IAxis3ScaleReader _scale;

	private readonly PztDeviceConnection _pzt;

	private readonly MotionSettings _settings;
	private readonly long _connectionEpoch;

	private readonly Action<Axis3Sample>? _traceSample;
	private readonly Action<Exception>? _traceSampleError;

	private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);

	private int _running;

	private int _stopRequested;

	private int _faultLatched;

	public bool IsRunning => Volatile.Read(in _running) != 0;

	public bool FaultLatched => Volatile.Read(in _faultLatched) != 0;

	private bool StopRequested => Volatile.Read(in _stopRequested) != 0;

	public ClosedLoopPositioner(IAxis3ScaleReader scale, PztDeviceConnection pzt, MotionSettings settings,
		Action<Axis3Sample>? traceSample = null, Action<Exception>? traceSampleError = null)
	{
		_scale = scale;
		_pzt = pzt;
		_settings = settings;
		_connectionEpoch = scale.ConnectionEpoch;
		_traceSample = traceSample;
		_traceSampleError = traceSampleError;
	}

	public Task<MoveResult> MoveRelativeAsync(double displacementMicrometers, Action<string>? report = null)
		=> MoveAsync(displacementMicrometers, absoluteTarget: false, report, initialCycles: null);

	public Task<MoveResult> MoveRelativeWithInitialCyclesAsync(
		double displacementMicrometers,
		int initialCycles,
		Action<string>? report = null)
		=> MoveAsync(displacementMicrometers, absoluteTarget: false, report, initialCycles);

	public Task<MoveResult> MoveAbsoluteAsync(double targetPositionMicrometers, Action<string>? report = null)
		=> MoveAsync(targetPositionMicrometers, absoluteTarget: true, report, initialCycles: null);

	private async Task<MoveResult> MoveAsync(
		double targetMicrometers,
		bool absoluteTarget,
		Action<string>? report,
		int? initialCycles)
	{
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
		{
			throw new InvalidOperationException("定位任务正在运行。");
		}
		Interlocked.Exchange(ref _stopRequested, 0);
		int corrections = 0;
		int totalCycles = 0;
		long? lastRaw = null;
		bool mayBeMoving = false;
		bool commandIssued = false;
		double? forwardResponse = PositiveFinite(_settings.ForwardCountsPerCycle);
		double? reverseResponse = PositiveFinite(_settings.ReverseCountsPerCycle);
		int forwardSamples = (forwardResponse.HasValue ? 2 : 0);
		int reverseSamples = (reverseResponse.HasValue ? 2 : 0);
		int forwardNoResponseBatches = 0;
		int reverseNoResponseBatches = 0;
		try
		{
			EnsureConnectionUnchanged();
			if (FaultLatched)
			{
				throw new InvalidOperationException("故障已锁定；断开设备并重新连接后才能重新开始。");
			}
			IReadOnlyList<string> readOnlyList = _settings.ValidateForClosedLoop();
			if (readOnlyList.Count > 0)
			{
				throw new InvalidOperationException(string.Join(" ", readOnlyList));
			}
			if (!double.IsFinite(targetMicrometers))
			{
				throw new InvalidOperationException("目标位置必须是有效数字。");
			}
			if (initialCycles.HasValue &&
				(initialCycles.Value < PztDeviceConnection.MinimumFiniteCycles ||
				 initialCycles.Value > PztDeviceConnection.MaximumManualJogCycles))
			{
				throw new InvalidOperationException($"输入周期必须介于 {PztDeviceConnection.MinimumFiniteCycles}–{PztDeviceConnection.MaximumManualJogCycles}。");
			}
			if (!absoluteTarget && targetMicrometers == 0.0)
			{
				throw new InvalidOperationException("相对移动量必须非零。");
			}
			if (!absoluteTarget && !initialCycles.HasValue && Math.Abs(targetMicrometers) <= _settings.TargetToleranceMicrometers)
			{
				throw new InvalidOperationException("相对移动量必须大于目标公差。");
			}
			if (absoluteTarget && !_settings.ScaleZeroCount.HasValue)
			{
				throw new InvalidOperationException("绝对定位需要先设置光栅尺软件零点。");
			}
			EnsureStopped(await _pzt.ReadStatusAsync().ConfigureAwait(continueOnCapturedContext: false));
			Axis3Sample start = await ReadStableSampleAsync().ConfigureAwait(continueOnCapturedContext: false);
			lastRaw = start.RawCount;
			double finalTargetRaw = absoluteTarget
				? (double)_settings.ScaleZeroCount!.Value + targetMicrometers * _settings.CountsPerMicrometer * _settings.ScaleDirection
				: (double)start.RawCount + targetMicrometers * _settings.CountsPerMicrometer * _settings.ScaleDirection;
			if (Math.Abs(finalTargetRaw) > 16_777_216.0)
				throw new InvalidOperationException("目标计数超出运动卡浮点接口可精确读取的范围。");
			if ((_settings.MinimumRawCount.HasValue && finalTargetRaw < _settings.MinimumRawCount.Value) ||
				(_settings.MaximumRawCount.HasValue && finalTargetRaw > _settings.MaximumRawCount.Value))
				throw new InvalidOperationException("目标超出光栅尺设置的原始计数范围。");
			double toleranceCounts = _settings.TargetToleranceMicrometers * _settings.CountsPerMicrometer;
			double targetErrorAtStartCounts = finalTargetRaw - start.RawCount;
			if (absoluteTarget && Math.Abs(targetErrorAtStartCounts) <= toleranceCounts)
			{
				double currentPosition = _settings.ToNominalPosition(start.RawCount)!.Value;
				report?.Invoke($"绝对目标已在公差内：当前位置 {currentPosition:+0.000;-0.000} μm，目标 {targetMicrometers:+0.000;-0.000} μm，未发送动作。");
				return new MoveResult(Reached: true, StoppedByUser: false, "目标已在公差范围内，无需运动。", start.RawCount, corrections, totalCycles);
			}
			double targetRaw = finalTargetRaw;
			if ((_settings.MinimumRawCount.HasValue && targetRaw < _settings.MinimumRawCount.Value) ||
				(_settings.MaximumRawCount.HasValue && targetRaw > _settings.MaximumRawCount.Value))
			{
				throw new InvalidOperationException("目标超出光栅尺设置的原始计数范围。");
			}
			double startPositionMicrometers = _settings.ToNominalPosition(start.RawCount)!.Value;
			double targetPositionMicrometers = absoluteTarget
				? targetMicrometers
				: startPositionMicrometers + targetMicrometers;
			report?.Invoke(absoluteTarget
				? $"绝对定位开始：起点 {startPositionMicrometers:+0.000;-0.000} μm / {start.RawCount} count；目标 {targetPositionMicrometers:+0.000;-0.000} μm / {finalTargetRaw:0.##} count。"
				: $"相对定位开始：起点 {startPositionMicrometers:+0.000;-0.000} μm / {start.RawCount} count；位移 {targetMicrometers:+0.000;-0.000} μm，目标 {targetPositionMicrometers:+0.000;-0.000} μm / {targetRaw:0.##} count。");
			int iteration = 0;
			if (initialCycles is int requestedCycles)
			{
				if (requestedCycles > _settings.MaximumCyclesPerCommand || requestedCycles > _settings.MaximumTotalCycles)
					throw new InvalidOperationException("输入周期超过本次闭环运动预算；未发送运动指令。");

				bool initialForward = targetErrorAtStartCounts * _settings.PztForwardCountDirection > 0.0;
				PiezoScrewDirection initialDirection = initialForward ? PiezoScrewDirection.Forward : PiezoScrewDirection.Reverse;
				int expectedRawSign = initialForward ? _settings.PztForwardCountDirection : -_settings.PztForwardCountDirection;
				EnsureStopped(await _pzt.ReadStatusAsync().ConfigureAwait(false));
				if (StopRequested)
					return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);

				mayBeMoving = true;
				commandIssued = true;
				await _commandGate.WaitAsync().ConfigureAwait(false);
				try
				{
					if (StopRequested)
						return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
					EnsureConnectionUnchanged();
					report?.Invoke($"准备发送初始指令：{(initialForward ? "D1M" : "D0M")}，{requestedCycles} 周期；起点 raw={start.RawCount}，目标 raw={targetRaw:0.##}。");
					await _pzt.DriveManualJogAsync(requestedCycles, initialDirection).ConfigureAwait(false);
				}
				finally
				{
					_commandGate.Release();
				}
				corrections++;
				totalCycles += requestedCycles;
				report?.Invoke($"按输入周期先运动：{(initialForward ? "正向" : "反向")} {requestedCycles} 周期；名义目标位移 {targetMicrometers:+0.000;-0.000} μm，随后读取光栅尺并按余差纠偏。");
				await WaitUntilStoppedAsync(GetCycleMotionTimeout(requestedCycles)).ConfigureAwait(false);
				mayBeMoving = false;
				if (StopRequested)
					return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);

				Axis3Sample initialFeedback = await ReadStableSampleAsync().ConfigureAwait(false);
				lastRaw = initialFeedback.RawCount;
				long initialDelta = initialFeedback.RawCount - start.RawCount;
				report?.Invoke($"初始指令停稳读数：起点 raw={start.RawCount}，终点 raw={initialFeedback.RawCount}，变化 {initialDelta:+#;-#;0} count，目标 raw={targetRaw:0.##}。");
				bool initialDeltaIsNoise = Math.Abs((double)initialDelta) <= _settings.StableSampleSpanCounts;
				if (initialDelta != 0 && Math.Sign(initialDelta) != expectedRawSign && !initialDeltaIsNoise)
					throw new InvalidOperationException($"输入周期运动后光栅尺变化 {initialDelta:+#;-#;0} count，与指令方向不符；已停止并锁定定位。");
				if (!initialDeltaIsNoise)
				{
					double observed = (double)Math.Abs(initialDelta) / requestedCycles;
					if (initialForward)
					{
						forwardResponse = BlendResponse(forwardResponse, observed);
						forwardSamples++;
						forwardNoResponseBatches = 0;
					}
					else
					{
						reverseResponse = BlendResponse(reverseResponse, observed);
						reverseSamples++;
						reverseNoResponseBatches = 0;
					}
				}
				else if (initialForward)
					forwardNoResponseBatches++;
				else
					reverseNoResponseBatches++;

				iteration = 1;
				double initialErrorCounts = targetRaw - initialFeedback.RawCount;
				double initialErrorMicrometers = initialErrorCounts / _settings.CountsPerMicrometer * _settings.ScaleDirection;
				report?.Invoke($"初始运动反馈：命令 {requestedCycles} 周期；光栅尺变化 {initialDelta:+#;-#;0} count（{initialDelta / _settings.CountsPerMicrometer * _settings.ScaleDirection:+0.000;-0.000;0.000} μm）；目标余差 {initialErrorMicrometers:+0.000;-0.000;0.000} μm。");
				if (Math.Abs(initialErrorCounts) <= toleranceCounts)
					return new MoveResult(Reached: true, StoppedByUser: false, "输入周期运动后，光栅尺反馈已在目标公差内。", initialFeedback.RawCount, corrections, totalCycles);
			}
			while (iteration < _settings.MaximumCorrections)
			{
				if (StopRequested)
				{
					return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
				}
				Axis3Sample axis3Sample = ((iteration != 0) ? (await ReadStableSampleAsync().ConfigureAwait(continueOnCapturedContext: false)) : start);
				Axis3Sample sample = axis3Sample;
				lastRaw = sample.RawCount;
				double num = targetRaw - (double)sample.RawCount;
				double num2 = Math.Abs(num);
				if (num2 <= toleranceCounts)
				{
					double finalErrorCounts = finalTargetRaw - sample.RawCount;
					report?.Invoke($"到位确认：raw={sample.RawCount} count，最终目标误差 {finalErrorCounts:+0.##;-0.##;0} count，位置 {(_settings.ToNominalPosition(sample.RawCount) ?? 0.0):+0.000;-0.000} μm。");
					return new MoveResult(Reached: true, StoppedByUser: false, "已到达目标公差范围。", sample.RawCount, corrections, totalCycles);
				}
				bool forward = num * (double)_settings.PztForwardCountDirection > 0.0;
				PiezoScrewDirection direction = (forward ? PiezoScrewDirection.Forward : PiezoScrewDirection.Reverse);
				int expectedRawSign = (forward ? _settings.PztForwardCountDirection : (-_settings.PztForwardCountDirection));
				double? num3 = (forward ? forwardResponse : reverseResponse);
				int num4 = (forward ? forwardNoResponseBatches : reverseNoResponseBatches);
				int num5 = (forward ? forwardSamples : reverseSamples);
				int num6 = _settings.MaximumTotalCycles - totalCycles;
				if (num6 <= 0)
				{
					throw new InvalidOperationException("已达到本次定位的总周期预算。");
				}
				if (!num3.HasValue && num4 >= 4)
				{
					throw new InvalidOperationException("连续有限点动后光栅尺未检测到位置变化，定位已锁定。");
				}
				int cycles;
				if (num3.HasValue)
				{
					double valueOrDefault3 = num3.GetValueOrDefault();
					if (valueOrDefault3 > 0.0)
					{
						double num7 = Math.Max(1.0, num2 - Math.Max(toleranceCounts * 0.5, _settings.StableSampleSpanCounts));
						cycles = Math.Max(1, (int)Math.Floor(num7 * 0.8 / valueOrDefault3));
						int num8 = (int)Math.Floor(Math.Max(0.0, num2 + toleranceCounts - _settings.StableSampleSpanCounts) / (valueOrDefault3 * 1.5));
						if (num8 >= 1)
						{
							cycles = Math.Min(cycles, num8);
						}
						if (num5 < 2)
						{
							cycles = Math.Min(cycles, 3);
						}
						goto IL_06ee;
					}
				}
				cycles = num4 switch
				{
					0 => 1,
					1 => 2,
					2 => 4,
					_ => 8,
				};
				goto IL_06ee;
				IL_06ee:
				cycles = Math.Min(cycles, Math.Min(_settings.MaximumCyclesPerCommand, num6));
				if (cycles < PztDeviceConnection.MinimumFiniteCycles)
				{
					throw new InvalidOperationException("剩余周期预算不足，已停止定位。");
				}
				EnsureStopped(await _pzt.ReadStatusAsync().ConfigureAwait(continueOnCapturedContext: false));
				if (DateTimeOffset.UtcNow - sample.CapturedAtUtc > TimeSpan.FromSeconds(1L))
				{
					throw new InvalidOperationException("光栅尺样本已过期，已停止定位。");
				}
				if (StopRequested)
				{
					return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
				}
				await _commandGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					if (StopRequested)
					{
						return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
					}
					EnsureConnectionUnchanged();
					mayBeMoving = true;
					commandIssued = true;
					report?.Invoke($"准备发送纠偏指令：{(forward ? "D1M" : "D0M")}，{cycles} 周期；命令前 raw={sample.RawCount}，目标 raw={targetRaw:0.##}。");
					await _pzt.DriveCyclesAsync(cycles, direction).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					_commandGate.Release();
				}
				corrections++;
				totalCycles += cycles;
				report?.Invoke($"第 {corrections} 次有限纠偏：{((direction == PiezoScrewDirection.Forward) ? "正向" : "反向")} {cycles} 周期；命令前 raw={sample.RawCount}，目标 raw={targetRaw:0.##}，误差={num:+0.##;-0.##;0} count。");
				await WaitUntilStoppedAsync(GetCycleMotionTimeout(cycles)).ConfigureAwait(continueOnCapturedContext: false);
				if (!StopRequested)
				{
					mayBeMoving = false;
					Axis3Sample axis3Sample2 = await ReadStableSampleAsync().ConfigureAwait(continueOnCapturedContext: false);
					lastRaw = axis3Sample2.RawCount;
					long num9 = axis3Sample2.RawCount - sample.RawCount;
					report?.Invoke($"纠偏停稳读数：命令前 raw={sample.RawCount}，命令后 raw={axis3Sample2.RawCount}，变化 {num9:+#;-#;0} count，目标 raw={targetRaw:0.##}。");
					bool flag = Math.Abs((double)num9) <= _settings.StableSampleSpanCounts;
					if (num9 != 0L && Math.Sign(num9) != expectedRawSign && !flag)
					{
						throw new InvalidOperationException($"{((direction == PiezoScrewDirection.Forward) ? "正向" : "反向")}指令后光栅尺计数变化 {num9:+#;-#;0}，与方向设置不符；已停止并锁定定位。");
					}
					if (num9 != 0L && !flag)
					{
						double observed = (double)Math.Abs(num9) / (double)cycles;
						if (forward)
						{
							forwardResponse = BlendResponse(forwardResponse, observed);
							forwardSamples++;
							forwardNoResponseBatches = 0;
						}
						else
						{
							reverseResponse = BlendResponse(reverseResponse, observed);
							reverseSamples++;
							reverseNoResponseBatches = 0;
						}
					}
					else
					{
						if (forward)
						{
							forwardNoResponseBatches++;
						}
						else
						{
							reverseNoResponseBatches++;
						}
						if (num9 != 0L)
						{
							report?.Invoke($"光栅尺变化 {num9:+#;-#;0} count 未超过稳定采样跨度 {_settings.StableSampleSpanCounts:F1} count，本批按无有效响应处理。");
						}
					}
					double errorCounts = targetRaw - axis3Sample2.RawCount;
					double num10 = Math.Abs(errorCounts);
					double errorMicrometers = errorCounts / _settings.CountsPerMicrometer * _settings.ScaleDirection;
					double finalErrorCountsAfterBatch = finalTargetRaw - axis3Sample2.RawCount;
					double finalErrorMicrometersAfterBatch = finalErrorCountsAfterBatch / _settings.CountsPerMicrometer * _settings.ScaleDirection;
					report?.Invoke($"闭环反馈：raw={axis3Sample2.RawCount} count，本批Δ={num9:+#;-#;0} count；分段误差={errorCounts:+0.##;-0.##;0} count（{errorMicrometers:+0.000;-0.000;0.000} μm）；最终目标余差={finalErrorCountsAfterBatch:+0.##;-0.##;0} count（{finalErrorMicrometersAfterBatch:+0.000;-0.000;0.000} μm）。");
					if (num10 <= toleranceCounts)
					{
						report?.Invoke($"到位确认：实际 {(_settings.ToNominalPosition(axis3Sample2.RawCount) ?? 0.0):+0.000;-0.000} μm，最终目标 {targetPositionMicrometers:+0.000;-0.000} μm，公差 ±{_settings.TargetToleranceMicrometers:F3} μm。");
						return new MoveResult(Reached: true, StoppedByUser: false, "已到达目标公差范围。", axis3Sample2.RawCount, corrections, totalCycles);
					}
					iteration++;
					continue;
				}
				return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
			}
			throw new InvalidOperationException("已达到纠偏或周期上限，目标仍未进入设定公差。");
		}
		catch (OperationCanceledException) when (StopRequested)
		{
			return new MoveResult(Reached: false, StoppedByUser: true, "已停止。", lastRaw, corrections, totalCycles);
		}
		catch (Exception ex2)
		{
			if (commandIssued)
			{
				Interlocked.Exchange(ref _faultLatched, 1);
			}
			report?.Invoke("定位停止：" + ex2.Message);
			try
			{
				if (mayBeMoving)
				{
					await RequestStopAndVerifyAsync(TimeSpan.FromSeconds(3L)).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (Exception ex3)
			{
				report?.Invoke("停止状态未能确认：" + ex3.Message);
			}
			return new MoveResult(Reached: false, StoppedByUser: false, ex2.Message, lastRaw, corrections, totalCycles);
		}
		finally
		{
			Interlocked.Exchange(ref _running, 0);
		}
	}

	public async Task RequestStopAndVerifyAsync(TimeSpan timeout)
	{
		Interlocked.Exchange(ref _stopRequested, 1);
		await _commandGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			await _pzt.StopAndVerifyAsync(timeout).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_commandGate.Release();
		}
	}

	private static double? PositiveFinite(double? value)
	{
		if (value.HasValue)
		{
			double valueOrDefault = value.GetValueOrDefault();
			if (double.IsFinite(valueOrDefault) && valueOrDefault != 0.0)
			{
				return Math.Abs(valueOrDefault);
			}
		}
		return null;
	}

	private static double BlendResponse(double? previous, double observed)
	{
		if (previous.HasValue)
		{
			double valueOrDefault = previous.GetValueOrDefault();
			if (double.IsFinite(valueOrDefault) && valueOrDefault > 0.0)
			{
				return (valueOrDefault + observed) / 2.0;
			}
		}
		return observed;
	}

	private async Task<Axis3Sample> ReadStableSampleAsync()
	{
		var stableWindow = await Axis3StabilitySampler.WaitForStableWindowAsync(
			_scale,
			5,
			_settings.StableSampleSpanCounts,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(_settings.SettleMilliseconds),
			ValidateScaleSample,
			() => StopRequested).ConfigureAwait(false);
		return stableWindow.LastSample with { RawCount = (long)Math.Round(stableWindow.Median) };
	}

	private void ValidateScaleSample(Axis3Sample sample)
	{
		EnsureConnectionUnchanged();
		if (sample.ConnectionEpoch != _connectionEpoch)
			throw new InvalidOperationException("光栅尺样本来自不同的运动卡连接；定位已停止，请重新初始化。");
		if (_settings.MinimumRawCount.HasValue && sample.RawCount < _settings.MinimumRawCount.Value ||
			_settings.MaximumRawCount.HasValue && sample.RawCount > _settings.MaximumRawCount.Value)
			throw new InvalidOperationException("光栅尺原始计数超出设置范围。");
	}

	private void EnsureConnectionUnchanged()
	{
		if (!_scale.IsConnected || _scale.ConnectionEpoch != _connectionEpoch)
			throw new InvalidOperationException("运动卡连接已变化；定位已停止，请重新初始化。");
	}

	private async Task WaitUntilStoppedAsync(TimeSpan timeout)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline)
		{
			PztDeviceStatus pztDeviceStatus = await _pzt.ReadStatusAsync().ConfigureAwait(continueOnCapturedContext: false);
			if ((pztDeviceStatus.State == PiezoScrewMotionState.Stopped && pztDeviceStatus.RemainingCycles == 0L) || StopRequested)
			{
				return;
			}
			if (_traceSample != null && !StopRequested)
			{
				try
				{
					Axis3Sample sample = await Task.Run(_scale.ReadRawCount).ConfigureAwait(false);
					ValidateScaleSample(sample);
					_traceSample(sample);
				}
				catch (Exception ex)
				{
					_traceSampleError?.Invoke(ex);
					throw;
				}
			}
			await Task.Delay(100).ConfigureAwait(continueOnCapturedContext: false);
		}
		throw new TimeoutException("有限周期命令后未确认 PZTScrew 已停止（STATE=Stopped、RMS=0）。");
	}

	private static TimeSpan GetCycleMotionTimeout(int cycles) =>
		TimeSpan.FromSeconds(Math.Clamp(5.0 + cycles * 0.05, 15.0, 300.0));

	private static void EnsureStopped(PztDeviceStatus status)
	{
		if (status.State != PiezoScrewMotionState.Stopped || status.RemainingCycles != 0L)
		{
			throw new InvalidOperationException($"PZTScrew 尚未停止（状态 {status.State}，剩余周期 {status.RemainingCycles}）。");
		}
	}
}
