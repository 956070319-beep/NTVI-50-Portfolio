using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeedleInspection.Domain.Inspection;
using NeedleInspection.Infrastructure.Services.Motion;
using NeedleInspection.Infrastructure.Services.Integration;
using NeedleInspection.UseCases.Inspection;
using NeedleInspection.UseCases.Devices;
using NeedleInspection.App.Views.Pages;
using NeedleInspection.App.ViewModels;

using NeedleInspection.Infrastructure.Services;

namespace NeedleInspection.App.Services;

internal static class AppHost
{
    public static IHost Build(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((context, services) =>
            {
                var dataRoot = context.Configuration["NeedleInspection:DataRoot"];
                services.AddSingleton(new AppDataPaths(
                    string.IsNullOrWhiteSpace(dataRoot) ? AppContext.BaseDirectory : dataRoot));
                services.AddSingleton<MotionConnectionLogService>();
                services.AddSingleton<RuntimeLogService>();
                services.AddSingleton<DeviceCommunicationSettingsService>();
                services.AddSingleton<ZMotionService>(provider =>
                {
                    var connectionLog = provider.GetRequiredService<MotionConnectionLogService>();
                    var machineState = provider.GetRequiredService<MachineStateService>();
                    return new ZMotionService(
                        connectionLog,
                        machineState,
                        motionCommandsEnabled: () => !machineState.IsEmergency);
                });
                services.AddSingleton<MachineStateService>();
                services.AddSingleton<MachineButtonMonitorService>();
                services.AddSingleton<RecipeService>();
                services.AddSingleton<AxisHomingModeSettingsService>();
                services.AddSingleton<InspectionResultService>();
                services.AddSingleton<InspectionBatchAuditService>();
                services.AddSingleton<IInspectionBatchAuditStore>(provider =>
                    provider.GetRequiredService<InspectionBatchAuditService>());
                services.AddSingleton<AlarmService>();
                services.AddSingleton<InspectionFileService>();
                services.AddSingleton<HikVisionModbusService>();
                services.AddSingleton<VisionCenteringService>();
                services.AddSingleton<FocusCaptureService>();
                services.AddSingleton<HikCameraSdkService>();
                services.AddSingleton<CameraConnectionService>();
                services.AddSingleton<AutoInspectionProcessService>();
                services.AddSingleton<VisionVmService>();
                services.AddSingleton<AutoFocusWorkflowService>();
                services.AddSingleton<LightControllerService>();
                services.AddSingleton<InspectionRunStateMachine>();
                services.AddSingleton<TrayInspectionGateway>();
                services.AddSingleton<ITrayInspectionGateway>(provider =>
                    provider.GetRequiredService<TrayInspectionGateway>());
                services.AddSingleton<IAutomaticInspectionReadinessGateway>(provider =>
                    provider.GetRequiredService<TrayInspectionGateway>());
                services.AddSingleton<FullTrayInspectionWorkflow>();
                services.AddSingleton<AutomaticInspectionReadiness>();
                services.AddSingleton<DeviceConnectionWorkflow>();
                services.AddSingleton<SimulatedMotionService>();
                services.AddSingleton<IMotionService>(provider =>
                    provider.GetRequiredService<SimulatedMotionService>());
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<AutoRunPageViewModel>();
                services.AddSingleton<ResultPageViewModel>();
                services.AddSingleton<AlarmPageViewModel>();
                services.AddSingleton<RecipePageViewModel>();
                services.AddSingleton<ManualPageViewModel>();
                services.AddSingleton<IUserDialogService, WpfUserDialogService>();
                services.AddSingleton<EmergencyResetService>();
                services.AddSingleton<AutoRunPage>();
                services.AddSingleton<ManualPage>();
                services.AddSingleton<CameraFocusPage>();
                services.AddSingleton<RecipePage>();
                services.AddSingleton<ResultPage>();
                services.AddSingleton<AlarmPage>();
                services.AddSingleton<PztIntegrationPage>();
                services.AddSingleton<IPztPositioningGateway>(provider =>
                    provider.GetRequiredService<PztIntegrationPage>());
            })
            .Build();
    }
}
