# NTVI-50｜上位机源码展示

面向 50 工位针尖视觉检测的 C# / WPF 上位机。项目由本人独立开发，整合运动控制、工业相机、PZT 压电定位与视觉系统。本展示包挑选了与设备协同、反馈控制及桌面应用交互有关的实现。

## 技术栈

C# · WPF · .NET 10 · CommunityToolkit.Mvvm · Generic Host · 串口 / TCP · Modbus/TCP · JSON / JSONL

## 推荐阅读顺序

| 技术主题 | 源码 | 展示内容 |
|---|---|---|
| 50 工位流程 | [FullTrayInspectionWorkflow.cs](src/NeedleInspection.Application/Inspection/FullTrayInspectionWorkflow.cs) | 配方加载、逐工位顺序编排、取消及故障分支 |
| 批次与暂停 | [InspectionRunStateMachine.cs](src/NeedleInspection.Domain/Inspection/InspectionRunStateMachine.cs) | 运行、请求暂停、安全点暂停、恢复及工位状态 |
| 光栅尺反馈 | [Axis3StabilitySampler.cs](src/PztMotionControl.App/Services/Axis3StabilitySampler.cs)、[Axis3FeedbackConverter.cs](src/PztMotionControl.App/Hardware/Axis3FeedbackConverter.cs) | 新鲜度、序列、稳定窗口、中位数及反馈计数转换 |
| PZT 定位 | [ClosedLoopPositioner.cs](src/PztMotionControl.App/Services/ClosedLoopPositioner.cs) | 有限周期运动、位置反馈、误差修正与停止条件 |
| 设备协议 | [PiezoScrewClient.cs](src/PiezoScrewSerial/PiezoScrewClient.cs)、[PiezoScrewResponseFramer.cs](src/PiezoScrewSerial/Protocol/PiezoScrewResponseFramer.cs) | 串行请求、响应分帧、超时处理和重新连接 |
| WPF 实时图像 | [CameraFrameDispatch.cs.txt](snippets/CameraFrameDispatch.cs.txt) | 后台相机帧与 Dispatcher 交接、最新帧合并 |
| 设备连接 | [DeviceConnectionWorkflow.cs](src/NeedleInspection.Application/Devices/DeviceConnectionWorkflow.cs) | 并行连接及按设备依赖顺序断开 |
| 自动对焦 | [AutoFocusWorkflowService.cs](src/NeedleInspection.Infrastructure/Services/AutoFocusWorkflowService.cs) | 运动、逐帧采集、视觉返回最佳帧及位置回退 |
| 视觉引导居中 | [VisionCenteringService.cs](src/NeedleInspection.Infrastructure/Services/VisionCenteringService.cs) | 偏差换算、运动步长限制和重试 |
| 配方页面 | [RecipePageViewModel.cs](src/NeedleInspection.App/ViewModels/RecipePageViewModel.cs) | 属性绑定、命令、输入校验与配方应用 |
| 运行追溯 | [PztTraceLog.cs](src/PztMotionControl.App/Services/PztTraceLog.cs) | 有界队列、异步日志写入与错误提示 |
| 协议离线检查 | [Program.cs](src/PiezoScrewSerial/tests/Program.cs)、[FakeByteTransport.cs](src/PiezoScrewSerial/tests/FakeByteTransport.cs) | 使用虚拟传输检查协议交互和异常路径 |

## 工程重点

### 将设备动作组织成可读的工作流

整板检测按工位依次执行定位、等待稳定、PZT 定位、对焦和视觉检测。批次状态机将暂停请求与流程到达安全点后的实际暂停区分开；针对运动失败和单工位检测失败，工作流采用不同处理路径。

### 用位置反馈完成有限运动定位

PZT 定位先验证反馈和设备状态，再执行有限周期运动；每批运动停止后采集稳定光栅尺反馈，并根据目标误差决定后续修正。系统区分设备的命令确认、运动状态和位置反馈。

### 同时照顾设备通信与 WPF 交互

通信客户端通过互斥控制一问一答并处理响应分帧；当响应同步丢失时要求重建客户端。相机显示路径通过合并待显示帧减少 Dispatcher 消息积压。

## 关于这份代码

本包基于 NTVI-50 V0.2 源码快照 `501aa8e` 精选，保留代码结构与原文件路径。`snippets/` 中的图像帧代码是明确标注行号的节选；自动对焦文件中的本地目录已替换为展示占位符。部分上下文、设备 SDK、项目文件和运行配置未包含，因此本包用于代码阅读，**不是可直接编译或连接设备运行的完整工程**。

对焦案例展示的是上位机如何调用视觉系统获取最佳帧并回到对应位置。协议离线检查验证软件行为，真实设备精度、整机节拍与现场验收情况需结合实际测试结果说明。
