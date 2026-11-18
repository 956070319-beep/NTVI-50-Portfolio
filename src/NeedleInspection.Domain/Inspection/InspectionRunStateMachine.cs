namespace NeedleInspection.Domain.Inspection;

public enum InspectionBatchState
{
    Idle,
    Running,
    PauseRequested,
    PausedAtSafePoint,
    EmergencyAborted,
    Completed,
    CompletedWithIssues,
    Faulted
}

public enum InspectionSlotState
{
    Pending,
    Positioning,
    WaitingForStability,
    Focusing,
    FocusFailed,
    Inspecting,
    VisionFailed,
    MotionFailed,
    Completed
}

public sealed record InspectionSlotSnapshot(
    int SlotNumber,
    InspectionSlotState State,
    string? FailureMessage);

public sealed record InspectionRunSnapshot(
    InspectionBatchState State,
    int TotalSlots,
    int CurrentSlot,
    IReadOnlyList<InspectionSlotSnapshot> Slots)
{
    public bool IsActive => State is InspectionBatchState.Running
        or InspectionBatchState.PauseRequested
        or InspectionBatchState.PausedAtSafePoint;

    public bool CanRequestPause => State == InspectionBatchState.Running;

    public bool CanResume => State == InspectionBatchState.PausedAtSafePoint;
}

/// <summary>
/// Runtime state for one tray inspection. It owns only business state and has
/// no WPF, device SDK, or persistence dependency.
/// </summary>
public sealed class InspectionRunStateMachine
{
    private readonly object _sync = new();
    private List<InspectionSlotSnapshot> _slots = [];
    private InspectionBatchState _state = InspectionBatchState.Idle;
    private int _currentSlot;

    public InspectionRunSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshot();
        }
    }

    public void Start(int slotCount)
    {
        if (slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "工位数量必须大于 0。");

        lock (_sync)
        {
            if (IsActive(_state))
                throw new InvalidOperationException("当前批次仍在运行，不能启动新批次。");

            _slots = Enumerable.Range(1, slotCount)
                .Select(slot => new InspectionSlotSnapshot(slot, InspectionSlotState.Pending, null))
                .ToList();
            _currentSlot = 0;
            _state = InspectionBatchState.Running;
        }
    }

    public void BeginSlot(int slotNumber)
    {
        lock (_sync)
        {
            if (_state != InspectionBatchState.Running)
                throw new InvalidOperationException("批次没有处于运行状态，不能进入新工位。");
            if (slotNumber != _currentSlot + 1 || slotNumber > _slots.Count)
                throw new InvalidOperationException($"工位顺序无效：当前 {_currentSlot}，请求 {slotNumber}。");

            _currentSlot = slotNumber;
            SetSlotState(slotNumber, InspectionSlotState.Positioning, null);
        }
    }

    public void SetCurrentSlotState(InspectionSlotState state)
    {
        lock (_sync)
        {
            if (!CanUpdateCurrentAction())
                return;
            if (_currentSlot <= 0)
                throw new InvalidOperationException("当前没有正在处理的工位。");

            SetSlotState(_currentSlot, state, null);
        }
    }

    public void FailCurrentSlot(InspectionSlotState failureState, string message)
    {
        if (failureState is not (InspectionSlotState.FocusFailed
            or InspectionSlotState.VisionFailed
            or InspectionSlotState.MotionFailed))
            throw new ArgumentOutOfRangeException(nameof(failureState), "这不是工位失败状态。");

        lock (_sync)
        {
            if (!CanUpdateCurrentAction())
                return;
            if (_currentSlot <= 0)
                throw new InvalidOperationException("当前没有正在处理的工位。");

            SetSlotState(_currentSlot, failureState, message);
        }
    }

    public void CompleteCurrentSlot()
    {
        lock (_sync)
        {
            if (!CanUpdateCurrentAction())
                return;
            if (_currentSlot <= 0)
                throw new InvalidOperationException("当前没有正在处理的工位。");

            SetSlotState(_currentSlot, InspectionSlotState.Completed, null);
        }
    }

    public bool RequestPause()
    {
        lock (_sync)
        {
            if (_state != InspectionBatchState.Running)
                return false;

            _state = InspectionBatchState.PauseRequested;
            return true;
        }
    }

    public bool ReachSafePoint()
    {
        lock (_sync)
        {
            if (_state == InspectionBatchState.PauseRequested)
            {
                _state = InspectionBatchState.PausedAtSafePoint;
                return true;
            }

            return false;
        }
    }

    public bool Resume()
    {
        lock (_sync)
        {
            if (_state != InspectionBatchState.PausedAtSafePoint)
                return false;

            _state = InspectionBatchState.Running;
            return true;
        }
    }

    public void AbortForEmergency()
    {
        lock (_sync)
        {
            if (_state is InspectionBatchState.Completed
                or InspectionBatchState.CompletedWithIssues
                or InspectionBatchState.Faulted)
                return;

            _state = InspectionBatchState.EmergencyAborted;
        }
    }

    public void Complete(bool allSlotsAccountedFor)
    {
        lock (_sync)
        {
            if (_state is not (InspectionBatchState.Running or InspectionBatchState.PauseRequested))
                throw new InvalidOperationException("只有正在运行的批次可以正常结束。");

            _state = allSlotsAccountedFor
                ? InspectionBatchState.Completed
                : InspectionBatchState.CompletedWithIssues;
        }
    }

    public void FailRun()
    {
        lock (_sync)
        {
            if (IsActive(_state))
                _state = InspectionBatchState.Faulted;
        }
    }

    public void ResetToStandby()
    {
        lock (_sync)
        {
            _slots = [];
            _currentSlot = 0;
            _state = InspectionBatchState.Idle;
        }
    }

    private InspectionRunSnapshot CreateSnapshot() =>
        new(_state, _slots.Count, _currentSlot, _slots.ToArray());

    private void SetSlotState(int slotNumber, InspectionSlotState state, string? message)
    {
        var index = slotNumber - 1;
        _slots[index] = _slots[index] with { State = state, FailureMessage = message };
    }

    private bool CanUpdateCurrentAction() =>
        _state is InspectionBatchState.Running or InspectionBatchState.PauseRequested;

    private static bool IsActive(InspectionBatchState state) =>
        state is InspectionBatchState.Running
            or InspectionBatchState.PauseRequested
            or InspectionBatchState.PausedAtSafePoint;
}
