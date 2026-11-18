using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NeedleInspection.Infrastructure.Models;
using NeedleInspection.Infrastructure.Services.Motion;

namespace NeedleInspection.Infrastructure.Services;

public class AutoFocusWorkflowResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = "";

    /// <summary>
    /// 视觉返回的最清晰图片编号：1~10
    /// </summary>
    public int BestIndex { get; set; }

    /// <summary>
    /// 最清晰图片对应的Z轴位置，单位：μm
    /// </summary>
    public double BestZUm { get; set; }

    public string RootFolder { get; set; } = "";

    public string FocusImagesFolder { get; set; } = "";

    public string BestImageFolder { get; set; } = "";

    public string BestImagePath { get; set; } = "";
}

public class AutoFocusWorkflowService
{
    private readonly ZMotionService _zMotion;
    private readonly HikCameraSdkService _camera;
    private readonly RecipeService _recipeService;
    private readonly VisionVmService _visionVm;
    private readonly MachineStateService _machineState;

    private const int DefaultFocusImageCount = 10;

    private const double DefaultFocusStepUm = 0.8;

    private const string AutoFocusRootFolder = @"<示例路径>"; // 展示版：原本地路径已替换

    private const string FocusImagesFolderName = "FocusImages";

    private const string BestImageFolderName = "BestImage";

    private const string BestImageFileName = "Best.bmp";

    public AutoFocusWorkflowService(
        ZMotionService zMotion,
        HikCameraSdkService camera,
        RecipeService recipeService,
        VisionVmService visionVm,
        MachineStateService machineState)
    {
        _zMotion = zMotion;
        _camera = camera;
        _recipeService = recipeService;
        _visionVm = visionVm;
        _machineState = machineState;
    }

    /// <summary>
    /// 相机对焦页面：Z轴自动对焦流程。
    ///
    /// 流程：
    /// 1. 校验控制器、相机、实时采集、视觉VM
    /// 2. Z轴移动到配方中的对焦基准Z
    /// 3. 在基准位置拍第1张
    /// 4. 后续每次Z轴向下步进，再拍图，共10张
    /// 5. 发送 b 给视觉软件
    /// 6. 视觉返回 Number:1~10
    /// 7. 复制最佳图片到 BestImage\Best.bmp
    /// 8. Z轴移动回最佳图片拍摄时的位置
    /// </summary>
    public async Task<AutoFocusWorkflowResult> RunFromCameraPageAsync(
        int _,
        Action<string>? log = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (!_zMotion.IsConnected)
            throw new InvalidOperationException("正运动控制器未连接，无法自动对焦。");

        if (!_camera.IsOpened)
            throw new InvalidOperationException("海康相机未连接，无法自动对焦。");

        if (!_camera.IsGrabbing)
            throw new InvalidOperationException("相机未开始实时采集，请先点击实时采集。");

        var recipe = _recipeService.LoadOrCreateDefault();

        await EnsureVisionConnectedAsync(recipe, log, token);

        log?.Invoke("开始执行Z轴自动对焦流程。");

        return await RunZAutoFocusAsync(recipe, log, token);
    }

    private async Task EnsureVisionConnectedAsync(
        MachineRecipe recipe,
        Action<string>? log,
        CancellationToken token)
    {
        if (_visionVm.IsConnected)
            return;

        log?.Invoke($"连接海康视觉VM：{recipe.HikVisionIp}:{recipe.HikVisionPort}");

        var msg = await _visionVm.ConnectAsync(
            recipe.HikVisionIp,
            recipe.HikVisionPort,
            token);

        log?.Invoke(msg);

        if (!_visionVm.IsConnected)
        {
            throw new InvalidOperationException(
                $"海康视觉VM连接失败：{recipe.HikVisionIp}:{recipe.HikVisionPort}");
        }
    }

    private async Task<AutoFocusWorkflowResult> RunZAutoFocusAsync(
        MachineRecipe recipe,
        Action<string>? log,
        CancellationToken token)
    {
        PrepareAutoFocusFolders(
            out var rootFolder,
            out var focusImagesFolder,
            out var bestImageFolder,
            out var bestImagePath);

        var focusImageCount = recipe.FocusImageCount > 0
      ? recipe.FocusImageCount
      : DefaultFocusImageCount;

        if (focusImageCount <= 0)
            throw new InvalidOperationException("自动对焦采图数量必须大于 0。");

        if (focusImageCount > 99)
            throw new InvalidOperationException("自动对焦采图数量不建议超过 99，请检查配方设置。");

        var focusStepUm = recipe.AutoFocusFineStepUm > 0
            ? Math.Abs(recipe.AutoFocusFineStepUm)
            : DefaultFocusStepUm;

        var baseZUm = recipe.AutoFocusBaseZUm;

        log?.Invoke($"自动对焦根目录：{rootFolder}");
        log?.Invoke($"10张图片目录：{focusImagesFolder}");
        log?.Invoke($"最佳图片目录：{bestImageFolder}");
        log?.Invoke($"对焦基准Z：{baseZUm:F3} μm");
        log?.Invoke($"自动对焦拍图数量：{focusImageCount}");
        log?.Invoke($"自动对焦Z步距：{focusStepUm:F3} μm");

        // 1. Z轴先回到人工设定的对焦基准位置
        log?.Invoke("Z轴开始移动到对焦基准位置。");

        var moveBaseResult = await _zMotion.MoveAbsoluteWithResultAsync(
            _zMotion.ZAxisNo,
            baseZUm,
            token);

        log?.Invoke(moveBaseResult.Message);

        if (!moveBaseResult.Success)
            throw new InvalidOperationException(moveBaseResult.Message);

        if (!_zMotion.TryGetDpos(
                _zMotion.ZAxisNo,
                out var actualBaseZ,
                out var basePosMsg))
        {
            throw new InvalidOperationException(basePosMsg);
        }

        log?.Invoke($"Z轴已到达对焦基准位置，当前Z={actualBaseZ:F3} μm。");

        // 保存每张图片对应的真实Z轴位置
        var zPositions = new List<double>();

        for (int i = 1; i <= focusImageCount; i++)
        {
            token.ThrowIfCancellationRequested();

            // 第1张：直接在基准Z位置拍照
            // 第2~10张：先向下走一步，再拍照
            if (i > 1)
            {
                var moveDistance = -focusStepUm;

                log?.Invoke($"第 {i} 张前：Z轴向下步进 {moveDistance:F3} μm。");

                var moveResult = await _zMotion.MoveRelativeWithResultAsync(
                    _zMotion.ZAxisNo,
                    moveDistance,
                    token);

                log?.Invoke(moveResult.Message);

                if (!moveResult.Success)
                    throw new InvalidOperationException(moveResult.Message);
            }

            await Task.Delay(120, token);

            if (!_zMotion.TryGetDpos(
                    _zMotion.ZAxisNo,
                    out var currentZ,
                    out var currentZMsg))
            {
                throw new InvalidOperationException(currentZMsg);
            }

            zPositions.Add(currentZ);

            // 每张图单独一个文件夹
            // FocusImages\01\Current.bmp
            // FocusImages\02\Current.bmp
            var indexFolder = Path.Combine(
                focusImagesFolder,
                i.ToString("00"));

            Directory.CreateDirectory(indexFolder);

            var imagePath = Path.Combine(indexFolder, "Current.bmp");

            var savedPath = _camera.SaveLatestFrameToFile(imagePath);

            log?.Invoke($"第 {i} 张对焦图已保存：Z={currentZ:F3} μm，Path={savedPath}");
        }

        // 2. 发送 b 给视觉，等待视觉返回最清晰图片编号
        log?.Invoke("10张对焦图片保存完成，发送 b 给视觉软件获取最清晰图片编号。");

        var bestIndex = await _visionVm.TriggerAutoFocusAndReadBestIndexAsync(token);

        if (bestIndex < 1 || bestIndex > focusImageCount)
        {
            throw new InvalidOperationException(
                $"视觉返回最佳图片编号无效：{bestIndex}，有效范围应为 1~{focusImageCount}");
        }

        // 3. 找到对应图片，复制到 BestImage\Best.bmp
        var sourceBestImagePath = Path.Combine(
            focusImagesFolder,
            bestIndex.ToString("00"),
            "Current.bmp");

        if (!File.Exists(sourceBestImagePath))
        {
            throw new FileNotFoundException(
                $"视觉返回的最佳编号图片不存在：{sourceBestImagePath}");
        }

        File.Copy(sourceBestImagePath, bestImagePath, true);

        // 4. 根据编号找到该图片拍摄时的真实Z轴位置
        var bestZUm = zPositions[bestIndex - 1];

        log?.Invoke($"视觉返回最清晰图片编号：{bestIndex}");
        log?.Invoke($"最清晰原图路径：{sourceBestImagePath}");
        log?.Invoke($"最清晰图片已覆盖复制到：{bestImagePath}");
        log?.Invoke($"最清晰图片拍摄时Z位置：{bestZUm:F3} μm");

        // 5. Z轴回到最清晰图片拍摄时的位置
        log?.Invoke("Z轴开始回到最清晰图片对应位置。");

        var moveBestResult = await _zMotion.MoveAbsoluteWithResultAsync(
            _zMotion.ZAxisNo,
            bestZUm,
            token);

        log?.Invoke(moveBestResult.Message);

        if (!moveBestResult.Success)
            throw new InvalidOperationException(moveBestResult.Message);

        _machineState.AppendOperationLog(
            $"自动对焦完成：视觉返回最佳编号={bestIndex}，Z轴回到最佳位置={bestZUm:F3} μm。");

        return new AutoFocusWorkflowResult
        {
            Success = true,
            BestIndex = bestIndex,
            BestZUm = bestZUm,
            RootFolder = rootFolder,
            FocusImagesFolder = focusImagesFolder,
            BestImageFolder = bestImageFolder,
            BestImagePath = bestImagePath,
            Message = $"自动对焦完成，最佳编号={bestIndex}，Z轴已回到 {bestZUm:F3} μm。"
        };
    }

    private static void PrepareAutoFocusFolders(
        out string rootFolder,
        out string focusImagesFolder,
        out string bestImageFolder,
        out string bestImagePath)
    {
        rootFolder = AutoFocusRootFolder;
        focusImagesFolder = Path.Combine(rootFolder, FocusImagesFolderName);
        bestImageFolder = Path.Combine(rootFolder, BestImageFolderName);
        bestImagePath = Path.Combine(bestImageFolder, BestImageFileName);

        Directory.CreateDirectory(rootFolder);

        if (Directory.Exists(focusImagesFolder))
            Directory.Delete(focusImagesFolder, true);

        if (Directory.Exists(bestImageFolder))
            Directory.Delete(bestImageFolder, true);

        Directory.CreateDirectory(focusImagesFolder);
        Directory.CreateDirectory(bestImageFolder);
    }
}
