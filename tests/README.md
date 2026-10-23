# 测试

需要 .NET 10 SDK；从项目根目录运行。测试是独立控制台程序，不能用 `dotnet test` 执行。默认使用模拟会话和临时文件，不登录真实 Steam、不操作真实存档。

## 核心回归：Kettle.Checks

```powershell
dotnet run --project tests/Kettle.Checks -c Release
# 只运行一组：
dotnet run --project tests/Kettle.Checks -c Release -- --suite output
```

| 分组 | 测试内容 |
|---|---|
| output | 命令边界、分块输出、光标控制符与异常回显、异步通知、通道恢复、游戏更新状态解析 |
| cloud | 存档路径安全、前缀与子目录、增量目录、启动前只读与冲突、退出后上传与按运行快照删除、本地缺失恢复、删除批次中断恢复、双边缺失拦截、失败备份保留、失败重试、回滚、AutoCloud 同步 |
| configuration | 启动项名称、系统过滤、语言设置、双语资源与格式占位符、登录验证、库登记清理、备份保留、启动更新进度和调试输出 |

## 界面与 Windows：Kettle.Diagnostics

```powershell
dotnet run --project tests/Kettle.Diagnostics -c Release -- --check-ui
dotnet run --project tests/Kettle.Diagnostics -c Release -- --help
```

| 参数 | 测试内容 |
|---|---|
| --check-ui | 登录、游戏列表、详情、语言设置、连接恢复、运行控制、中英文更新拦截 |
| --check-cloud-host | 实际工作进程的备份、保留清理和中英文错误返回 |
| --render-settings-en <目录> | 英文设置与登录界面、语言选择保存及控件截图 |
| --check-channel-recovery | 通道恢复的单独检查 |
| --check-windows | Windows 控制台输入输出、ConPTY 和目录链接处理 |
| --check-startup-update / --check-update-cancel / --check-update-live | 模拟更新进程的完成、失败、取消和实时输出 |
| --check-pty-wrap <目录> | 生成合成终端换行样本 |
| --check-controls <目录> | 菜单关闭、启动选项窗口的选择与取消 |
| --check-debug-ui / --check-login-phase-ui <目录> | 调试窗口或登录阶段的控件验证 |
| --check-startup-ui / --check-startup-close <目录> | 模拟启动更新界面的进度、重试和关闭 |
| --check-game-update-ui <目录> | 灰色“需要更新”按钮及恢复可运行状态 |
| --render-library / --render-details / --render-settings / --render-prompts <目录> | 对应界面与运行状态的控件截图 |

带窗口或截图的检查需要可交互 Windows 桌面。更新相关测试只使用模拟数据，不下载或更新游戏。

发布后的 `cloud-host/Kettle.CloudHost.exe --self-test` 检查 x86 AOT、JSON 和原生 DLL 加载，不初始化 Steam、不操作存档。

## 发布验证：Kettle.AotChecks

```powershell
dotnet publish tests/Kettle.AotChecks -c Release -r win-x64 -o artifacts/Kettle-AotChecks
& artifacts/Kettle-AotChecks/Kettle.AotChecks.exe
```

检查原生发布后的双语资源加载、设置与恢复记录读写、备份清单、终端宿主输入输出。必须先发布再运行。

- `--verify-failure-reporting`：故意失败，验证错误输出和退出码 1。

## 内存测量：Kettle.MemoryProbe

比较登录页、主界面、窗口尺寸和页面切换的内存占用。它是手动测量工具，不是通过/失败测试。场景和运行方法见 [内存测量说明](Kettle.MemoryProbe/README.md)。

## Debug 崩溃诊断

`dotnet run --project tests/Kettle.Diagnostics -c Debug -p:UsedAvaloniaProducts= -- --check-debug-trace` 检查日志写入、异常详情和重启轮转；使用 `-c Release` 检查额外诊断未启用。

Debug 构建自动写入 `data/diagnostics/kettle-debug-latest.log`，包含登录切换、同步阶段和线程。v2 仅使用普通文件写入，不订阅全局异常事件，不采样 GC，不逐条强制磁盘刷新。已有 catch 处捕获的异常另写 `kettle-errors-latest.log`，不代表进程崩溃。重启保留 `previous`，阶段日志超过约 4 MiB 时保留一个 `overflow`。Release 不包含这些诊断调用。CloudHost 的既有日志仍保留，Debug 另外保留上一轮。Release 仅保留空的条件方法声明，文件写入实现和静态初始化也通过预处理剔除。

## CoreSession 运行流程

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
./tests/Kettle.CoreSessionChecks/run.ps1
```

需要 Visual Studio C++ 工具链。脚本构建原生助手、模拟 SteamCMD 和工作进程，检查正常退出、停止后等待全部 PID、启动失败、冲突选择及主界面恢复。不连接真实 Steam。

`Kettle.CloudFixtures` 直接编译当前 CloudHost 存档源码供离线测试使用，只引用 Shared；不再保留旧 GameLifecycle、GameRunMonitor 和运行日志实现。存档回归直接调用准备/结束同步接口；游戏运行和恢复由上述原生集成测试负责。

## 数量与维护范围

输出的 Assertions 是断言数，不是独立测试场景数。双语资源检查逐项验证资源存在及格式参数，贡献了大量断言，执行成本很低；保留这些检查，不通过减少枚举项来压低数字。核心回归会分别报告每组断言数和耗时。

已移除旧托管运行流程、混合轮询监控及旧恢复日志的测试与夹具，也移除了未接入被测界面的模拟计数器断言。继续保留存档缺失、冲突、删除失败恢复、路径安全、SteamCMD 分块输出，以及真实 CoreSession 流程等回归。界面截图、原生发布和内存测量按需要单独运行，不并入日常离线回归。
