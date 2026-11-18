using NeedleInspection.Infrastructure.Models;

namespace NeedleInspection.Infrastructure.Services;

public class VisionCenteringService
{
    private readonly RecipeService _recipeService;
    private readonly HikVisionModbusService _hikVision;
    private readonly NeedleInspection.Infrastructure.Services.Motion.ZMotionService _zMotion;

    public VisionCenteringService(
        RecipeService recipeService,
        HikVisionModbusService hikVision,
        NeedleInspection.Infrastructure.Services.Motion.ZMotionService zMotion)
    {
        _recipeService = recipeService;
        _hikVision = hikVision;
        _zMotion = zMotion;
    }
    public async Task<VisionCenteringResult> CenterMaterialByYAxisAsync(
        CancellationToken token = default)
    {
        var recipe = _recipeService.CurrentRecipe;

        if (!recipe.EnableYCentering)
        {
            return new VisionCenteringResult
            {
                Success = true,
                Message = "Y轴视觉居中未启用，跳过。"
            };
        }

        for (int i = 1; i <= recipe.YCenterMaxRetry; i++)
        {
            token.ThrowIfCancellationRequested();

            await _hikVision.TriggerCenteringCaptureAsync(token);

            var offset = await _hikVision.ReadCenteringOffsetAsync(token);

            if (!offset.Found)
            {
                return new VisionCenteringResult
                {
                    Success = false,
                    RetryCount = i,
                    Message = "视觉未找到物料，Y轴居中失败。"
                };
            }

            var offsetPixel = offset.OffsetYPixel;

            if (Math.Abs(offsetPixel) <= recipe.YCenterTolerancePixel)
            {
                return new VisionCenteringResult
                {
                    Success = true,
                    RetryCount = i,
                    FinalOffsetPixel = offsetPixel,
                    Message = $"Y轴居中完成，偏差={offsetPixel:F3} pixel。"
                };
            }

            var moveUm = offsetPixel
                         * recipe.YPixelToUm
                         * recipe.YCenterDirection;

            if (Math.Abs(moveUm) > recipe.YCenterMaxMoveUm)
            {
                moveUm = Math.Sign(moveUm) * recipe.YCenterMaxMoveUm;
            }

            var moveResult = await _zMotion.MoveRelativeWithResultAsync(
                _zMotion.YAxisNo,
                moveUm,
                token);

            if (!moveResult.Success)
            {
                return new VisionCenteringResult
                {
                    Success = false,
                    RetryCount = i,
                    FinalOffsetPixel = offsetPixel,
                    Message = $"Y轴移动失败：{moveResult.Message}"
                };
            }
        }

        return new VisionCenteringResult
        {
            Success = false,
            RetryCount = recipe.YCenterMaxRetry,
            Message = "Y轴居中超过最大次数，仍未达到中心范围。"
        };
    }
}
