using System.Diagnostics;
using NeedleInspection.Domain.Inspection;

namespace NeedleInspection.UseCases.Inspection;

/// <summary>
/// Runs the existing one-board inspection sequence. Device details and result
/// storage are supplied by the infrastructure gateway; this class owns order,
/// checkpoints, and the batch/station state transitions.
/// </summary>
public sealed class FullTrayInspectionWorkflow(
    ITrayInspectionGateway gateway,
    InspectionRunStateMachine runState,
    IInspectionBatchAuditStore auditStore)
{
    public async Task<InspectionResultSummary> RunAsync(
        Action<string> log,
        Action<TrayInspectionProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(reportProgress);

        var timer = Stopwatch.StartNew();
        var recipe = gateway.LoadAndApplyRecipe();
        runState.Start(recipe.SlotCount);
        var traceability = gateway.GetBatchTraceability();
        AppendAudit("Started", null, null);

        try
        {
            log($"自动检测开始：共{recipe.SlotCount}个物料，X轴间距{recipe.SlotPitchXUm:F3} μm。");
            log($"第1个物料X基准位置：{recipe.FirstSlotXUm:F3} μm。");
            log($"X轴固定间距：{recipe.SlotPitchXUm:F3} μm。");
            log($"X方向：{recipe.XDirection}。");

            for (var slot = 1; slot <= recipe.SlotCount; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfEmergencyAborted();
                await WaitForRecoverableStopAsync(slot, recipe.SlotCount, timer, log, reportProgress, cancellationToken);

                runState.BeginSlot(slot);
                ReportStep(slot, recipe.SlotCount, "X轴移动到当前物料位", InspectionSlotState.Positioning);

                var targetX = gateway.GetSlotXPositionUm(slot);
                log($"第 {slot} 个物料目标X位置：{targetX:F3} μm。");

                var moveResult = await gateway.MoveToSlotAsync(slot, cancellationToken);
                log(moveResult.Message);
                if (!moveResult.Success)
                {
                    runState.FailCurrentSlot(InspectionSlotState.MotionFailed, moveResult.Message);
                    throw new InvalidOperationException(moveResult.Message);
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfEmergencyAborted();
                await WaitForRecoverableStopAsync(slot, recipe.SlotCount, timer, log, reportProgress, cancellationToken);

                ReportStep(slot, recipe.SlotCount, "等待物料稳定", InspectionSlotState.WaitingForStability);
                await Task.Delay(500, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfEmergencyAborted();

                ReportStep(slot, recipe.SlotCount, "压电螺钉闭环定位", InspectionSlotState.Positioning);
                log($"第 {slot} 个物料压电闭环目标：{recipe.PztTargetPositionUm:+0.000;-0.000} μm（相对软件零点）。");
                var pztResult = await gateway.MovePztToTargetAsync(
                    slot,
                    recipe.PztTargetPositionUm,
                    log,
                    cancellationToken);
                log(pztResult.Message);
                if (!pztResult.Success)
                {
                    gateway.MarkPztPositionFailed(slot, pztResult.Message);
                    runState.FailCurrentSlot(InspectionSlotState.MotionFailed, pztResult.Message);
                    throw new InvalidOperationException($"第 {slot} 个物料压电闭环定位失败，整板流程已停止：{pztResult.Message}");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfEmergencyAborted();
                await WaitForRecoverableStopAsync(slot, recipe.SlotCount, timer, log, reportProgress, cancellationToken);

                ReportStep(slot, recipe.SlotCount, "Z轴自动对焦", InspectionSlotState.Focusing);
                var focus = await gateway.RunAutofocusAsync(slot, log, cancellationToken);
                ThrowIfEmergencyAborted();
                if (!focus.Success)
                {
                    log($"第 {slot} 个物料自动对焦失败：{focus.Message}");
                    gateway.MarkFocusFailed(slot, $"自动对焦失败：{focus.Message}");
                    runState.FailCurrentSlot(InspectionSlotState.FocusFailed, focus.Message);
                    reportProgress(new(slot, recipe.SlotCount, "自动对焦失败，继续下一针", timer.Elapsed, RefreshSelectedTrayResult: true));
                    continue;
                }

                log($"第 {slot} 个物料自动对焦完成：最佳编号={focus.BestIndex}，最佳Z={focus.BestZUm:F3} μm。");
                log($"第 {slot} 个物料最清晰图片：{focus.BestImagePath}");

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfEmergencyAborted();
                await WaitForRecoverableStopAsync(slot, recipe.SlotCount, timer, log, reportProgress, cancellationToken);

                ReportStep(slot, recipe.SlotCount, "视觉D检测", InspectionSlotState.Inspecting);
                try
                {
                    await gateway.InspectSlotAsync(slot, focus, log, cancellationToken);
                    ThrowIfEmergencyAborted();
                    runState.CompleteCurrentSlot();
                    reportProgress(new(slot, recipe.SlotCount, "视觉D检测", timer.Elapsed, RefreshCurrentResult: true));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log($"第 {slot} 个物料视觉D检测失败：{ex.Message}");
                    gateway.MarkVisionFailed(slot, $"自动对焦完成，但视觉D检测失败：{ex.Message}");
                    runState.FailCurrentSlot(InspectionSlotState.VisionFailed, ex.Message);
                    reportProgress(new(slot, recipe.SlotCount, "视觉D检测失败，继续下一针", timer.Elapsed, RefreshSelectedTrayResult: true));
                }

                reportProgress(new(slot, recipe.SlotCount, "当前物料流程完成", timer.Elapsed));
            }

            timer.Stop();
            var summary = gateway.Summarize(recipe.SlotCount);
            runState.Complete(summary.AllSlotsAccountedFor);
            reportProgress(new(recipe.SlotCount, recipe.SlotCount, "整板检测完成", timer.Elapsed));
            log($"整板结果：已检测 {summary.InspectedCount}，空位跳过 {summary.SkippedCount}，失败 {summary.FailedCount}，未完成 {summary.IncompleteCount}。");
            AppendAudit("Completed", summary, null);
            return summary;
        }
        catch (OperationCanceledException exception)
        {
            timer.Stop();
            var snapshot = runState.Snapshot;
            var eventName = snapshot.State == InspectionBatchState.EmergencyAborted ? "EmergencyAborted" : "Cancelled";
            AppendAudit(eventName, null, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            timer.Stop();
            runState.FailRun();
            AppendAudit("Faulted", null, exception.Message);
            throw;
        }

        void AppendAudit(string eventName, InspectionResultSummary? summary, string? message)
        {
            var snapshot = runState.Snapshot;
            try
            {
                auditStore.Append(new InspectionBatchAuditEntry(
                    SchemaVersion: 1,
                    Event: eventName,
                    BatchId: traceability.BatchId,
                    SlotCount: recipe.SlotCount,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    State: snapshot.State.ToString(),
                    CurrentSlot: snapshot.CurrentSlot,
                    RecipeName: traceability.RecipeName,
                    RecipePath: traceability.RecipePath,
                    RecipeSha256: traceability.RecipeSha256,
                    CameraModel: traceability.CameraModel,
                    CameraSerialNumber: traceability.CameraSerialNumber,
                    SoftwareVersion: traceability.SoftwareVersion,
                    InspectedCount: summary?.InspectedCount,
                    SkippedCount: summary?.SkippedCount,
                    FailedCount: summary?.FailedCount,
                    IncompleteCount: summary?.IncompleteCount,
                    Message: message));
            }
            catch (Exception exception)
            {
                log($"批次追溯日志写入失败：{exception.Message}");
            }
        }

        void ReportStep(int slotNumber, int totalSlots, string step, InspectionSlotState state)
        {
            runState.SetCurrentSlotState(state);
            reportProgress(new(slotNumber, totalSlots, step, timer.Elapsed));
            log($"第 {slotNumber} 个物料：{step}");
        }
    }

    private async Task WaitForRecoverableStopAsync(
        int slotNumber,
        int totalSlots,
        Stopwatch timer,
        Action<string> log,
        Action<TrayInspectionProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        var reachedSafePoint = runState.ReachSafePoint();
        var currentState = runState.Snapshot.State;
        if (!reachedSafePoint && currentState != InspectionBatchState.PausedAtSafePoint)
            return;

        if (reachedSafePoint)
        {
            reportProgress(new(slotNumber, totalSlots, "检测已停止（可恢复）", timer.Elapsed, SafeStopReached: true));
            log("自动检测已在安全点停止，可恢复。");
        }

        while (runState.Snapshot.State == InspectionBatchState.PausedAtSafePoint)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfEmergencyAborted();
            await Task.Delay(100, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfEmergencyAborted();
    }

    private void ThrowIfEmergencyAborted()
    {
        if (runState.Snapshot.State == InspectionBatchState.EmergencyAborted)
            throw new OperationCanceledException("急停已触发，当前自动检测已终止。");
    }
}
