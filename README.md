# PerAppAudio

让 Windows 上每个应用程序独立选择音频输出设备的开源工具——打破"所有声音只有一个默认输出"的系统限制。

- 状态：**P1 控制闭环已实测验证**（2026-10-16；协调器 + 持久路由表 + 看门狗上线）
- 仓库：https://github.com/Serendipty1251/PerAppAudio
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
git clone https://github.com/Serendipty1251/PerAppAudio.git
cd PerAppAudio
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

## P0-2 手段B2 实测结果（2026-10-15，Win11 build 26200）

机制：WinRT 内部类 `Windows.Media.Internal.AudioPolicyConfig` 激活工厂 `IAudioPolicyConfigFactory`（EarTrumpet / Windows 现代声音设置同款，需 Win10 21H2+）。

| 项 | 结果 |
| --- | --- |
| 接口绑定 | 成功 |
| SetPersistedDefaultAudioEndpoint | **成功**（注册表实锤：`HKCU\...\DefaultEndpoint\{hash}_0` 双角色写入正确设备） |
| Get / Clear | 成功过（读回设备 ID / 恢复默认） |
| 运行中进程实时迁移 | **不支持**——路由持久化每程序，只在应用创建新音频会话时生效 |
| API 稳定性 | 间歇性 E_INVALIDARG（成功后同进程后续调用可能失败）→ 产品侧重试 + 回退设计 |

**产品结论**：B2 管「未来」（应用启动即按路由走），A 管「现在」（正在播放的应用实时改道）——A+B 组合拳。

## P1 控制闭环实测结果（2026-10-16，Win11 build 26200）

新增组件：`RoutingCoordinator`（策略调度，Auto = B2+A 双管齐下）、`RouteStore`（持久路由表 `%APPDATA%\PerAppAudio\routes.json`，按进程路径绑定）、`EndpointBackend` 重试封装、CLI `ctl add/remove/list/watch`。

| 验收项 | 标准 | 实测 | 结论 |
| --- | --- | --- | --- |
| 单应用改道成功率 | ≥99% | B2 set（播放中进程）15/15；A 链路 PoC-1 0 丢帧 + 本日多次 ✅ | 达标 |
| B2 失败保障 | 2s 内不缺声 | Auto 并行双管齐下（实测 B2❌A✅ 场景音频不中断） | 达标 |
| 进程退出自动拆除 | <2s | 自然退出 2s / 短命进程 1s（看门狗日志实锤） | 达标 |
| 重启后路由保持 | 持久 | B2 注册表：新进程零干预直走目标设备（RMS 0.259 实测）；routes.json 重载 | 达标 |
| 新进程自动应用 | — | ctl watch 2–4s 内发现并应用（B2+A 30ms） | 达标 |

**P0-2 未解现象破案（E_INVALIDARG 根因）**：`SetPersistedDefaultAudioEndpoint` 仅在**目标进程已注册音频会话**时可用——正在播放 / 播放过 / 系统声音宿主（explorer）全部成功（15/15 + 3/3）；**从未发声的活进程与已退出进程**一律 E_INVALIDARG（含重试）。产品语义自洽：UI 只显示有会话的应用，用户可选的目标 set 必成功；「预路由未启动应用」由 RouteStore + watch 轮询在会话出现后补设。重试设计仍吸收多调用方并发竞争与启动注册窗口（实测 629/657ms 恢复案例）。

## 项目结构

```
src/
├── WasapiInterop/     # WASAPI P/Invoke + COM 接口声明（vtable 顺序与 SDK 头文件严格一致）
│                      # + IAudioPolicyConfigFactory（手段B2，未公开接口）
├── RoutingEngine/     # 控制手段层：IRoutingBackend + 手段A(LoopbackBackend) + 手段B(EndpointBackend)
│                      # + RoutingCoordinator（P1 策略调度：Auto 双管齐下 + 看门狗）
├── SessionManager/    # 会话枚举、进程识别、音量守护
├── RuleEngine/        # 规则匹配、场景、触发器 + RouteStore（P1 持久路由表）
├── App.Wpf/           # 托盘与主界面（调用层）
└── App.Cli/           # CLI / 诊断（P0 载体 + P1 ctl 控制命令）
```

## 规划文档

完整规划（需求、架构、手段对比、路线图、决策记录）见 [per-app-audio-router-plan.md](per-app-audio-router-plan.md)。

## License

MIT，详见 [LICENSE](LICENSE)。
