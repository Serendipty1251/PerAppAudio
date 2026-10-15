# PerAppAudio

让 Windows 上每个应用程序独立选择音频输出设备的开源工具——打破"所有声音只有一个默认输出"的系统限制。

- 状态：**P0 技术验证阶段**（2026-10-15 启动；手段A 全链路已实测打通）
- 平台：Windows 10 2004+ / 11（首版唯一平台；Linux / macOS 远期）
- 协议：MIT
- 隐私：纯本地运行，零网络请求，无遥测

## 产品本质

一个「音频走向控制引擎」：把指定应用的音频可靠地引向指定输出设备。**控制手段本身是第一优先级**，UI / 规则 / 自动化是调用层。

三种控制手段：

| 手段 | 原理 | 延迟 | 驱动 |
| --- | --- | --- | --- |
| A 进程 Loopback 重定向 | 进程级 loopback 捕获 → 原会话静音 → 渲染到目标设备 | 30–60ms | 无 |
| B 默认端点引导 | 把应用的默认输出端点改为目标设备，应用直接播放 | <10ms | 无（B2 IPolicyConfig 主推，B1 注入默认关） |
| C 虚拟声卡混音 | 多虚拟音频线 + 混音引擎分派 | <10ms | 需 EV 签名驱动（远期） |

策略调度默认「自动」：优先 B，2 秒内未生效自动回退 A——产品在任何情况下不会缺声。

## 架构

五层：调用层 / 规则引擎 / 会话管理 / **控制手段层（产品核心）** / 设备层。控制手段层以 `IRoutingBackend` 接口抽象（见 `src/RoutingEngine`），后续重采样热点与 B1 注入模块可抽为原生库 P/Invoke 加载。

## 构建

```bash
dotnet build
dotnet run --project src/App.Cli   # P0 诊断入口
```

## P0 诊断 CLI 用法

```bash
dotnet run --project src/App.Cli -- devices                              # 列出渲染设备（含 Mix 格式）
dotnet run --project src/App.Cli -- sessions [--device <ID|名称>]         # 列出音频会话
dotnet run --project src/App.Cli -- route --pid <PID> --to <ID|名称> [--ms <时长>] [--keep]
# 手段A 路由试验：把指定进程的音频改道到目标设备，输出延迟/CPU/丢帧/爆音报告
dotnet run --project src/App.Cli -- test                                  # COM 互操作分步自检
```

## P0-1 手段A 实测结果（2026-10-15，Win11 build 26200）

目标进程 440Hz 测试音（默认设备 XIBERIA K03S 播放）→ 改道至 VoiceMeeter VAIO3：

| 指标 | 实测 | 结论 |
| --- | --- | --- |
| CPU 占用 | 0.08%（16 逻辑核折算） | 达标（目标 <3%） |
| 丢帧 / 不连续（爆音） | 0 / 0 | 达标 |
| 实时率 | 100.2%（6.0s 音频 / 6.007s 墙钟） | 达标 |
| 单包处理耗时 | avg 0.018ms / max 1.876ms | 软件开销可忽略 |
| 拆除恢复 | 原会话正常恢复 | 达标 |
| 流水线缓冲 | 采集 10ms(480帧) + 渲染 10ms(1056帧) | 端到端约 20ms+ |

## 项目结构

```
src/
├── WasapiInterop/     # WASAPI P/Invoke + COM 接口声明（vtable 顺序与 SDK 头文件严格一致）
├── RoutingEngine/     # 控制手段层：IRoutingBackend + 手段A 实现（LoopbackBackend/采集/渲染）
├── SessionManager/    # 会话枚举、进程识别、音量守护
├── RuleEngine/        # 规则匹配、场景、触发器（调用层）
├── App.Wpf/           # 托盘与主界面（调用层）
└── App.Cli/           # CLI / 诊断（P0 载体）
```

## 规划文档

完整规划（需求、架构、手段对比、路线图、决策记录）见 [per-app-audio-router-plan.md](per-app-audio-router-plan.md)。

## License

MIT，详见 [LICENSE](LICENSE)。
