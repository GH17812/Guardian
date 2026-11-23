# Phase 0 验证清单（技术假设核对）

对照《产品需求与研发总纲》第六部分。完成一项勾一项，结论填入 docs/技术兼容性报告.md。

## 假设 1：物理显示器关闭时，远程桌面捕获是否正常

- [ ] RDP：关闭显示器后远程画面正常
- [ ] Sunshine/Moonlight：正常
- [ ] Parsec：正常
- [ ] 其他方案（ToDesk/向日葵等）：正常
- [ ] 恢复后显示拓扑（分辨率/多屏）与开启前一致

实现：`Guardian.Windows.Display.DisplayPrivacyController`（SC_MONITORPOWER 跨会话广播 + 周期重申）。
风险点：个别捕获方案依赖物理显示器电源状态；驱动级 HDR/多屏组合。

## 假设 2：不同显示器、GPU、串流软件组合的行为

- [ ] NVIDIA / AMD / Intel 核显各至少一台
- [ ] HDMI / DP / eDP 连接各至少一种
- [ ] 多显示器（2+）组合
- [ ] 高刷（144Hz+）/ HDR 组合

## 假设 3：Windows 防休眠 API

- [ ] `SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED)` 在目标机有效
- [ ] 请求释放后 `powercfg /requests` 无残留
- [ ] 服务进程崩溃后请求自动失效（fail-open）
- [ ] 显示器电源与系统电源可分别管理（ES_DISPLAY_REQUIRED 开关）
- [ ] Modern Standby 设备专项

实现：`Guardian.Windows.Power.PowerRequestController`（专用线程持有请求 + 60s 重申请）。

## 假设 4：USB、蓝牙键盘鼠标的输入识别

- [ ] Raw Input 枚举能区分键盘/鼠标/HID 类型
- [ ] USB 与蓝牙设备均被枚举
- [ ] 低层钩子对两类设备的输入均生效

实现：`Guardian.Windows.Input.InputCapabilityDetector`（GetRawInputDeviceList/Info）。

## 假设 5：本地输入与远程注入输入能否可靠区分

- [ ] SendInput 注入（模拟远程）带 INJECTED 标记，可放行
- [ ] 物理输入无标记，可拦截
- [ ] RDP 输入的标记行为实测（**关键风险项**）
- [ ] 其他串流方案输入的标记行为实测

实现：`Guardian.Windows.Input.InputIsolationController`（WH_KEYBOARD_LL/WH_MOUSE_LL + INJECTED 过滤 + 紧急组合键）。
结论不通过 → 输入隔离保持禁用，不承诺能力。

## 假设 6：服务崩溃时的恢复路径

- [ ] 服务崩溃后：输入隔离失效（钩子随进程移除）
- [ ] 服务崩溃后：电源请求失效（线程退出即释放）
- [ ] 服务崩溃后：显示器电源恢复路径（SCM 自动重启 → 启动 fail-open 恢复打开显示器）
- [ ] SCM 崩溃自动重启配置生效（sc failure 配置）
- [ ] 客户端崩溃：心跳超时 → 按设置自动恢复

实现：`ProtectionEngine.StartupRestore` / `RecoveryCoordinator` / `GuardianHost`。

## 产出物

- [ ] 技术验证程序（本仓库：Guardian.App + Guardian.Service 的对应功能 + 集成测试）
- [ ] 测试记录（xUnit 测试结果）
- [ ] 兼容性矩阵（docs/技术兼容性报告.md 按机填写）
- [ ] 风险报告（docs/技术兼容性报告.md 第 5 节结论 + README 已知限制）
