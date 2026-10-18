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
| lifecycle | 启动失败、连续运行、停止请求、退出确认和同步时机 |
| cloud | 存档路径安全、前缀与子目录、增量目录、重上传、删除与备份、冲突处理、失败重试、回滚、中断恢复、AutoCloud 同步 |
| configuration | 启动项名称、系统过滤与选择取消、语言设置、双语资源与格式占位符、登录验证、库登记清理、备份保留、启动更新进度和调试输出 |

## 界面与 Windows：Kettle.Diagnostics

```powershell
dotnet run --project tests/Kettle.Diagnostics -c Release -- --check-ui
dotnet run --project tests/Kettle.Diagnostics -c Release -- --help
```

| 参数 | 测试内容 |
|---|---|
| --check-ui | 登录、游戏列表、详情、语言设置、连接恢复、运行控制、中英文更新拦截 |
| --check-cloud-host | 模拟云辅助进程握手、错误输出上限、初始化失败和退出超时 |
| --render-settings-en <目录> | 英文设置与登录界面、语言选择保存及控件截图 |
| --check-run-controls / --check-channel-recovery | 运行控制或通道恢复的单独检查 |
| --check-windows | Windows 控制台输入输出、ConPTY 和目录链接处理 |
| --check-startup-update / --check-update-cancel / --check-update-live | 模拟更新进程的完成、失败、取消和实时输出 |
| --check-pty-wrap <目录> | 生成合成终端换行样本 |
| --check-controls <目录> | 菜单关闭、启动选项窗口的选择与取消 |
| --check-debug-ui / --check-login-phase-ui <目录> | 调试窗口或登录阶段的控件验证 |
| --check-startup-ui / --check-startup-close <目录> | 模拟启动更新界面的进度、重试和关闭 |
| --check-game-update-ui <目录> | 灰色“需要更新”按钮及恢复可运行状态 |
| --render-library / --render-details / --render-settings / --render-prompts / --render-hybrid <目录> | 对应界面与运行状态的控件截图 |

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
