namespace NeedleInspection.UseCases.Devices;

/// <summary>
/// Coordinates the existing Connect All / Disconnect All rules without
/// changing the device-specific command implementations.
/// </summary>
public sealed class DeviceConnectionWorkflow
{
    public async Task<DeviceConnectionWorkflowResult> ConnectAllAsync(IDeviceConnectionGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var settingsWarning = gateway.SaveCommunicationSettings();

        var operations = await Task.WhenAll(
            gateway.ConnectMotionControllerAsync(),
            gateway.ConnectCameraAsync(),
            gateway.ConnectPiezoScrewAsync());

        var status = gateway.ReadConnectionSnapshot(validateCameraConnection: true);
        var message = $"运动卡：{operations[0].Message}\n相机：{operations[1].Message}\n压电螺钉：{operations[2].Message}";
        if (!string.IsNullOrWhiteSpace(settingsWarning))
            message += $"\n{settingsWarning}";

        return new(message, status, ShouldWarn: !status.AllConnected);
    }

    public async Task<DeviceConnectionWorkflowResult> DisconnectAllAsync(IDeviceConnectionGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (gateway.ReadConnectionSnapshot().PiezoScrewConnected)
        {
            var piezoResult = await gateway.DisconnectPiezoScrewAsync();
            if (!piezoResult.Success)
            {
                return new(
                    piezoResult.Message,
                    gateway.ReadConnectionSnapshot(),
                    ShouldWarn: true,
                    PiezoDisconnectPreventedOtherDisconnects: true);
            }
        }

        var cameraTask = gateway.DisconnectCameraAsync();
        var motionTask = gateway.DisconnectMotionControllerAsync();
        await Task.WhenAll(cameraTask, motionTask);

        var motionResult = await motionTask;
        var status = gateway.ReadConnectionSnapshot();
        var message = $"相机：{await cameraTask}\n运动卡：{motionResult.Message}\n压电螺钉：{(status.PiezoScrewConnected ? "仍连接" : "已断开")}";
        return new(
            message,
            status,
            ShouldWarn: !motionResult.Success || status.CameraConnected || status.PiezoScrewConnected);
    }
}
