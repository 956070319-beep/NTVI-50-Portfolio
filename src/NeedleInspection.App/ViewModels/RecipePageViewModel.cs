using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NeedleInspection.Infrastructure.Models;
using NeedleInspection.Infrastructure.Services;
using NeedleInspection.Infrastructure.Services.Motion;

namespace NeedleInspection.App.ViewModels;

/// <summary>
/// Holds the editable recipe draft and coordinates the existing save/apply
/// operations. Validation labels, order, and device side effects match V0.1.
/// </summary>
public partial class RecipePageViewModel : ObservableObject
{
    private readonly RecipeService _recipes;
    private readonly MachineStateService _machineState;
    private readonly VisionVmService _visionVm;
    private readonly ZMotionService _motion;
    private readonly RuntimeLogService _runtimeLogs;
    private readonly IUserDialogService _dialogs;
    private int _xAxisNo = 2;
    private int _yAxisNo;
    private int _zAxisNo = 1;

    [ObservableProperty] private string recipeName = "Default";
    [ObservableProperty] private string slotCount = "50";
    [ObservableProperty] private string firstSlotX = "0.000";
    [ObservableProperty] private string slotPitchX = "5000.000";
    [ObservableProperty] private bool isXDirectionNegative;
    [ObservableProperty] private string xAxisUnitsPerUm = "1.000000";
    [ObservableProperty] private string yAxisUnitsPerUm = "20.000000";
    [ObservableProperty] private string zAxisUnitsPerUm = "1.000000";
    [ObservableProperty] private string pztTargetPositionUm = "0.200";
    [ObservableProperty] private string manualSpeed = "10000.000";
    [ObservableProperty] private string manualAccel = "50000.000";
    [ObservableProperty] private string manualDecel = "50000.000";
    [ObservableProperty] private bool enableSoftLimit;
    [ObservableProperty] private string xMin = "-100000";
    [ObservableProperty] private string xMax = "100000";
    [ObservableProperty] private string yMin = "-100000";
    [ObservableProperty] private string yMax = "100000";
    [ObservableProperty] private string zMin = "-100000";
    [ObservableProperty] private string zMax = "100000";
    [ObservableProperty] private string exposure = "5000.000";
    [ObservableProperty] private string gain = "0.000";
    [ObservableProperty] private string focusImageCount = "10";
    [ObservableProperty] private string focusRange = "5000.000";
    [ObservableProperty] private string coarseStep = "10.000";
    [ObservableProperty] private string fineStep = "500.000";
    [ObservableProperty] private string autoFocusBaseZ = "0.000";
    [ObservableProperty] private bool enableYCentering = true;
    [ObservableProperty] private string yCenterTolerancePixel = "5.000";
    [ObservableProperty] private string yPixelToUm = "1.000000";
    [ObservableProperty] private bool isYCenterDirectionNegative;
    [ObservableProperty] private string yCenterMaxMoveUm = "500.000";
    [ObservableProperty] private string yCenterMaxRetry = "5";
    [ObservableProperty] private string pageStatus = "配方已加载";
    [ObservableProperty] private string statusBrushKey = "Brush.Success";

    public event Action<string>? LogMessageRequested;

    public bool IsXDirectionPositive
    {
        get => !IsXDirectionNegative;
        set => IsXDirectionNegative = !value;
    }

    public bool IsYCenterDirectionPositive
    {
        get => !IsYCenterDirectionNegative;
        set => IsYCenterDirectionNegative = !value;
    }

    partial void OnIsXDirectionNegativeChanged(bool value) => OnPropertyChanged(nameof(IsXDirectionPositive));
    partial void OnIsYCenterDirectionNegativeChanged(bool value) => OnPropertyChanged(nameof(IsYCenterDirectionPositive));

    public RecipePageViewModel(
        RecipeService recipes,
        MachineStateService machineState,
        VisionVmService visionVm,
        ZMotionService motion,
        RuntimeLogService runtimeLogs,
        IUserDialogService dialogs)
    {
        _recipes = recipes;
        _machineState = machineState;
        _visionVm = visionVm;
        _motion = motion;
        _runtimeLogs = runtimeLogs;
        _dialogs = dialogs;

        var recipe = _recipes.LoadOrCreateDefault();
        ApplyRecipePageDisplayDefaults(recipe);
        LoadRecipeIntoDraft(recipe);
        _recipes.ApplyToMotion();
    }

    public void LogPageLoaded() => Log("配方管理页面已加载。");

    [RelayCommand]
    private void LoadRecipe()
    {
        var recipe = _recipes.LoadOrCreateDefault();
        ApplyRecipePageDisplayDefaults(recipe);
        LoadRecipeIntoDraft(recipe);
        SetStatus("配方已加载", "Brush.Success");
        _machineState.AppendOperationLog("配方已加载。");
        Log("配方已加载。");
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        if (!TryBuildRecipe(out var recipe))
            return;

        _recipes.Save(recipe);
        _visionVm.Disconnect();
        SetStatus("配方已保存", "Brush.Success");
        _machineState.AppendOperationLog("配方已保存。");
        Log($"配方已保存：{_recipes.CurrentRecipePath}");
        Log($"自动对焦参数已保存：基准Z={recipe.AutoFocusBaseZUm:F3} μm，精步距={recipe.AutoFocusFineStepUm:F3} μm。");
    }

    [RelayCommand]
    private void ApplyRecipe()
    {
        if (!TryBuildRecipe(out var recipe))
            return;

        _recipes.Save(recipe);
        _recipes.ApplyToMotion();
        _visionVm.Disconnect();
        SetStatus("配方已应用", "Brush.Success");
        _machineState.AppendOperationLog("配方已应用到设备参数。");
        Log("配方已应用到设备参数。");
        Log($"自动对焦参数已应用：基准Z={recipe.AutoFocusBaseZUm:F3} μm，采图数量={recipe.FocusImageCount}，精步距={recipe.AutoFocusFineStepUm:F3} μm。");
    }

    [RelayCommand]
    private void ApplySoftLimit()
    {
        if (!TryGetDouble(XMin, "X最小软限位", out var xMin)) return;
        if (!TryGetDouble(XMax, "X最大软限位", out var xMax)) return;
        if (!TryGetDouble(YMin, "Y最小软限位", out var yMin)) return;
        if (!TryGetDouble(YMax, "Y最大软限位", out var yMax)) return;
        if (!TryGetDouble(ZMin, "Z最小软限位", out var zMin)) return;
        if (!TryGetDouble(ZMax, "Z最大软限位", out var zMax)) return;

        if (xMin >= xMax || yMin >= yMax || zMin >= zMax)
        {
            ShowWarning("软限位最小值必须小于最大值。");
            return;
        }

        var recipe = _recipes.CurrentRecipe;
        recipe.EnableSoftwareLimit = EnableSoftLimit;
        recipe.XSoftMinUm = xMin;
        recipe.XSoftMaxUm = xMax;
        recipe.YSoftMinUm = yMin;
        recipe.YSoftMaxUm = yMax;
        recipe.ZSoftMinUm = zMin;
        recipe.ZSoftMaxUm = zMax;

        _motion.ApplySoftLimitConfig(EnableSoftLimit, xMin, xMax, yMin, yMax, zMin, zMax);
        _recipes.Save(recipe);
        Log("软件软限位已应用并保存。启用=" + EnableSoftLimit + "。");
        _machineState.AppendOperationLog("软件软限位已应用。");
    }

    [RelayCommand]
    private void ResetDefault()
    {
        if (!_dialogs.Confirm("恢复默认", "确认恢复默认配方参数吗？当前页面未保存的修改会丢失。"))
            return;

        var current = _recipes.CurrentRecipe;
        var recipe = new MachineRecipe
        {
            RecipeName = "Default",
            XAxisNo = 2,
            YAxisNo = 0,
            ZAxisNo = 1,
            SlotCount = 50,
            FirstSlotXUm = 0,
            SlotPitchXUm = 5000.000,
            XDirection = 1,
            ControllerUnitsPerUm = 1,
            PztTargetPositionUm = 0.2,
            XAxisUnitsPerUm = 1,
            YAxisUnitsPerUm = 20,
            ZAxisUnitsPerUm = 1,
            YLinearScaleUnitsPerUm = 20,
            ManualSpeed = 10000,
            ManualAccel = 50000,
            ManualDecel = 50000,
            EnableSoftwareLimit = false,
            XSoftMinUm = -100000,
            XSoftMaxUm = 100000,
            YSoftMinUm = -100000,
            YSoftMaxUm = 100000,
            ZSoftMinUm = -100000,
            ZSoftMaxUm = 100000,
            HikVisionIp = current.HikVisionIp,
            HikVisionPort = current.HikVisionPort,
            HikLatestImagePath = current.HikLatestImagePath,
            InspectionDataRootFolder = current.InspectionDataRootFolder,
            CameraExposureUs = 5000,
            CameraGain = 0,
            FocusImageCount = 10,
            AutoFocusRangeUm = 5000,
            AutoFocusCoarseStepUm = 10,
            AutoFocusFineStepUm = 500,
            AutoFocusBaseZUm = 0,
            EnableYCentering = true,
            YCenterTolerancePixel = 5,
            YPixelToUm = 1,
            YCenterDirection = 1,
            YCenterMaxMoveUm = 500,
            YCenterMaxRetry = 5
        };

        LoadRecipeIntoDraft(recipe);
        SetStatus("已恢复默认", "Brush.Warning");
        Log("页面参数已恢复默认，尚未保存。");
    }

    private void LoadRecipeIntoDraft(MachineRecipe recipe)
    {
        _xAxisNo = recipe.XAxisNo;
        _yAxisNo = recipe.YAxisNo;
        _zAxisNo = recipe.ZAxisNo;
        RecipeName = recipe.RecipeName;
        SlotCount = recipe.SlotCount.ToString(CultureInfo.CurrentCulture);
        FirstSlotX = recipe.FirstSlotXUm.ToString("F3", CultureInfo.CurrentCulture);
        SlotPitchX = recipe.SlotPitchXUm.ToString("F3", CultureInfo.CurrentCulture);
        IsXDirectionNegative = recipe.XDirection < 0;
        XAxisUnitsPerUm = recipe.XAxisUnitsPerUm.ToString("F6", CultureInfo.CurrentCulture);
        YAxisUnitsPerUm = recipe.YAxisUnitsPerUm.ToString("F6", CultureInfo.CurrentCulture);
        ZAxisUnitsPerUm = recipe.ZAxisUnitsPerUm.ToString("F6", CultureInfo.CurrentCulture);
        PztTargetPositionUm = recipe.PztTargetPositionUm.ToString("F3", CultureInfo.CurrentCulture);
        ManualSpeed = recipe.ManualSpeed.ToString("F3", CultureInfo.CurrentCulture);
        ManualAccel = recipe.ManualAccel.ToString("F3", CultureInfo.CurrentCulture);
        ManualDecel = recipe.ManualDecel.ToString("F3", CultureInfo.CurrentCulture);
        EnableSoftLimit = recipe.EnableSoftwareLimit;
        XMin = recipe.XSoftMinUm.ToString("F3", CultureInfo.CurrentCulture);
        XMax = recipe.XSoftMaxUm.ToString("F3", CultureInfo.CurrentCulture);
        YMin = recipe.YSoftMinUm.ToString("F3", CultureInfo.CurrentCulture);
        YMax = recipe.YSoftMaxUm.ToString("F3", CultureInfo.CurrentCulture);
        ZMin = recipe.ZSoftMinUm.ToString("F3", CultureInfo.CurrentCulture);
        ZMax = recipe.ZSoftMaxUm.ToString("F3", CultureInfo.CurrentCulture);
        Exposure = recipe.CameraExposureUs.ToString("F3", CultureInfo.CurrentCulture);
        Gain = recipe.CameraGain.ToString("F3", CultureInfo.CurrentCulture);
        FocusImageCount = recipe.FocusImageCount.ToString(CultureInfo.CurrentCulture);
        FocusRange = recipe.AutoFocusRangeUm.ToString("F3", CultureInfo.CurrentCulture);
        CoarseStep = recipe.AutoFocusCoarseStepUm.ToString("F3", CultureInfo.CurrentCulture);
        FineStep = recipe.AutoFocusFineStepUm.ToString("F3", CultureInfo.CurrentCulture);
        AutoFocusBaseZ = recipe.AutoFocusBaseZUm.ToString("F3", CultureInfo.CurrentCulture);
        EnableYCentering = recipe.EnableYCentering;
        YCenterTolerancePixel = recipe.YCenterTolerancePixel.ToString("F3", CultureInfo.CurrentCulture);
        YPixelToUm = recipe.YPixelToUm.ToString("F6", CultureInfo.CurrentCulture);
        IsYCenterDirectionNegative = recipe.YCenterDirection < 0;
        YCenterMaxMoveUm = recipe.YCenterMaxMoveUm.ToString("F3", CultureInfo.CurrentCulture);
        YCenterMaxRetry = recipe.YCenterMaxRetry.ToString(CultureInfo.CurrentCulture);
    }

    private bool TryBuildRecipe(out MachineRecipe recipe)
    {
        recipe = new MachineRecipe();
        if (!TryGetInt(SlotCount, "针位数量", out var slotCount)) return false;
        if (!TryGetDouble(FirstSlotX, "第1根X坐标", out var firstSlotX)) return false;
        if (!TryGetDouble(SlotPitchX, "X轴针距", out var slotPitchX)) return false;
        if (!TryGetDouble(XAxisUnitsPerUm, "X轴单位/μm", out var xUnitsPerUm)) return false;
        if (!TryGetDouble(YAxisUnitsPerUm, "Y轴单位/μm", out var yUnitsPerUm)) return false;
        if (!TryGetDouble(ZAxisUnitsPerUm, "Z轴单位/μm", out var zUnitsPerUm)) return false;
        if (!TryGetDouble(PztTargetPositionUm, "压电闭环目标", out var pztTargetPositionUm)) return false;
        if (!TryGetFloat(ManualSpeed, "手动速度", out var manualSpeed)) return false;
        if (!TryGetFloat(ManualAccel, "加速度", out var manualAccel)) return false;
        if (!TryGetFloat(ManualDecel, "减速度", out var manualDecel)) return false;
        if (!TryGetDouble(XMin, "X最小软限位", out var xMin)) return false;
        if (!TryGetDouble(XMax, "X最大软限位", out var xMax)) return false;
        if (!TryGetDouble(YMin, "Y最小软限位", out var yMin)) return false;
        if (!TryGetDouble(YMax, "Y最大软限位", out var yMax)) return false;
        if (!TryGetDouble(ZMin, "Z最小软限位", out var zMin)) return false;
        if (!TryGetDouble(ZMax, "Z最大软限位", out var zMax)) return false;
        if (!TryGetDouble(Exposure, "曝光", out var exposure)) return false;
        if (!TryGetDouble(Gain, "增益", out var gain)) return false;
        if (!TryGetInt(FocusImageCount, "自动对焦采图数量", out var imageCount)) return false;
        if (!TryGetDouble(FocusRange, "自动对焦范围", out var focusRange)) return false;
        if (!TryGetDouble(CoarseStep, "粗步距", out var coarseStep)) return false;
        if (!TryGetDouble(FineStep, "精步距", out var fineStep)) return false;
        if (!TryGetDouble(AutoFocusBaseZ, "对焦基准Z", out var autoFocusBaseZ)) return false;
        if (!TryGetDouble(YCenterTolerancePixel, "Y居中允许偏差", out var yTolerance)) return false;
        if (!TryGetDouble(YPixelToUm, "Y像素换算", out var yPixelToUm)) return false;
        if (!TryGetDouble(YCenterMaxMoveUm, "Y最大修正距离", out var yMaxMove)) return false;
        if (!TryGetInt(YCenterMaxRetry, "Y最大修正次数", out var yMaxRetry)) return false;

        if (slotCount <= 0) return WarnInvalid("针位数量必须大于0。");
        if (slotPitchX <= 0) return WarnInvalid("X轴针距必须大于0。");
        if (xMin >= xMax || yMin >= yMax || zMin >= zMax) return WarnInvalid("软限位最小值必须小于最大值。");
        if (!double.IsFinite(xUnitsPerUm) || xUnitsPerUm <= 0 || !double.IsFinite(yUnitsPerUm) || yUnitsPerUm <= 0 || !double.IsFinite(zUnitsPerUm) || zUnitsPerUm <= 0)
            return WarnInvalid("单位换算必须大于0。");
        if (imageCount <= 0) return WarnInvalid("自动对焦采图数量必须大于0。");
        if (focusRange <= 0) return WarnInvalid("自动对焦范围必须大于0。");
        if (coarseStep <= 0) return WarnInvalid("粗步距必须大于0。");
        if (fineStep <= 0) return WarnInvalid("精步距必须大于0。");
        if (yPixelToUm <= 0) return WarnInvalid("Y像素换算必须大于0。");
        if (yMaxMove <= 0) return WarnInvalid("Y最大修正距离必须大于0。");
        if (yMaxRetry <= 0) return WarnInvalid("Y最大修正次数必须大于0。");

        var current = _recipes.CurrentRecipe;
        recipe = new MachineRecipe
        {
            RecipeName = string.IsNullOrWhiteSpace(RecipeName) ? "Default" : RecipeName.Trim(),
            XAxisNo = _xAxisNo,
            YAxisNo = _yAxisNo,
            ZAxisNo = _zAxisNo,
            SlotCount = slotCount,
            FirstSlotXUm = firstSlotX,
            SlotPitchXUm = slotPitchX,
            XDirection = IsXDirectionNegative ? -1 : 1,
            ControllerUnitsPerUm = 1.0,
            PztTargetPositionUm = pztTargetPositionUm,
            XAxisUnitsPerUm = xUnitsPerUm,
            YAxisUnitsPerUm = yUnitsPerUm,
            ZAxisUnitsPerUm = zUnitsPerUm,
            YLinearScaleUnitsPerUm = yUnitsPerUm,
            ManualSpeed = manualSpeed,
            ManualAccel = manualAccel,
            ManualDecel = manualDecel,
            EnableSoftwareLimit = EnableSoftLimit,
            XSoftMinUm = xMin,
            XSoftMaxUm = xMax,
            YSoftMinUm = yMin,
            YSoftMaxUm = yMax,
            ZSoftMinUm = zMin,
            ZSoftMaxUm = zMax,
            HikVisionIp = current.HikVisionIp,
            HikVisionPort = current.HikVisionPort,
            HikLatestImagePath = current.HikLatestImagePath,
            InspectionDataRootFolder = current.InspectionDataRootFolder,
            CameraExposureUs = exposure,
            CameraGain = gain,
            FocusImageCount = imageCount,
            AutoFocusRangeUm = focusRange,
            AutoFocusCoarseStepUm = coarseStep,
            AutoFocusFineStepUm = fineStep,
            AutoFocusBaseZUm = autoFocusBaseZ,
            EnableYCentering = EnableYCentering,
            YCenterTolerancePixel = yTolerance,
            YPixelToUm = yPixelToUm,
            YCenterDirection = IsYCenterDirectionNegative ? -1 : 1,
            YCenterMaxMoveUm = yMaxMove,
            YCenterMaxRetry = yMaxRetry,
            BackLightBrightness = current.BackLightBrightness,
            FrontLightBrightness = current.FrontLightBrightness
        };
        return true;
    }

    private bool TryGetInt(string text, string name, out int value)
    {
        if (int.TryParse(text.Trim(), out value)) return true;
        ShowWarning($"{name} 输入不正确。");
        return false;
    }

    private bool TryGetDouble(string text, string name, out double value)
    {
        if (double.TryParse(text.Trim(), out value)) return true;
        ShowWarning($"{name} 输入不正确。");
        return false;
    }

    private bool TryGetFloat(string text, string name, out float value)
    {
        if (float.TryParse(text.Trim(), out value)) return true;
        ShowWarning($"{name} 输入不正确。");
        return false;
    }

    private bool WarnInvalid(string message)
    {
        ShowWarning(message);
        return false;
    }

    private void ShowWarning(string message)
    {
        _dialogs.ShowWarning("提示", message);
        Log(message);
    }

    private static void ApplyRecipePageDisplayDefaults(MachineRecipe recipe) => recipe.SlotPitchXUm = 5000.000;

    private void SetStatus(string message, string brushKey)
    {
        PageStatus = message;
        StatusBrushKey = brushKey;
    }

    private void Log(string message)
    {
        if (!RuntimeLogService.ShouldShowInOperationLog("配方管理", message))
            return;
        _runtimeLogs.Append("配方管理", "配方管理", message, RuntimeLogService.InferLevel(message));
        LogMessageRequested?.Invoke(message);
    }
}
