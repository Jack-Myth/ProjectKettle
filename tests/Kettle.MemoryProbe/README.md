# 内存测量

使用实际界面和演示数据，对比不同场景的内存占用，不连接 Steam 或访问真实存档。

```powershell
dotnet publish tests/Kettle.MemoryProbe -c Release -r win-x64 -o artifacts/memory-probe
& tests/Kettle.MemoryProbe/Measure.ps1
```

默认每个场景运行三次，窗口自动关闭，结果保存在 `artifacts/memory-investigation`。

| 场景 | 测量内容 |
|---|---|
| login / main | 登录页与带两个演示游戏的主界面 |
| main-empty / main-small | 隐藏列表或缩小窗口后的主界面 |
| transition | 同一进程关闭登录页后打开主界面 |
| blank-small / blank-large / unbound | 空窗口或未绑定数据的主界面，仅单独运行 |

单独运行：`artifacts/memory-probe/Kettle.MemoryProbe.exe <场景> <输出.tsv>`。

记录工作集、专用工作集、私有提交、托管内存和窗口尺寸。比较时使用相同列、缩放和采样阶段；强制回收后的结果仅用于诊断，不能单独证明是否泄漏。测试不包含真实会话、网络、封面解码和其他进程的开销。
