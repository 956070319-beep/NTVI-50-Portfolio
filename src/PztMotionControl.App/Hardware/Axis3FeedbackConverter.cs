using System;

namespace PztMotionControl.App.Hardware;

public static class Axis3FeedbackConverter
{
	private const double MaximumExactIntegerFloat = 16_777_216;

	public static (long FeedbackCount, long EncoderRawCount) Convert(float encoder, float mpos, float units)
	{
		if (!float.IsFinite(units) || Math.Abs(units - 1f) > 0.000001f)
			throw new InvalidOperationException($"轴 3 UNITS={units}，与已确认的 1 count/控制器单位不符；闭环停止。");
		return (ExactCount(mpos, "MPOS"), ExactCount(encoder, "ENCODER"));
	}

	private static long ExactCount(float value, string label)
	{
		if (!float.IsFinite(value) || Math.Abs((double)value) > MaximumExactIntegerFloat || MathF.Truncate(value) != value)
			throw new InvalidOperationException($"轴 3 {label} 不是可精确表示的整数计数；闭环停止。");
		return checked((long)value);
	}
}
