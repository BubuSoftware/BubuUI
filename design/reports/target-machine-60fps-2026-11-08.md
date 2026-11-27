# 60fps 复测报告（2026-11-08，刷新率已调至 60Hz）

> 按 `design/reports/target-machine-60fps-template.md` 口径回填。本次为**同一台机器**（Intel UHD 630 +
> GameViewer 虚拟显示链路）把刷新率调到 60Hz 后的复测；不再是 29Hz。数据全部来自实跑，
> 日志在 `out/g2-60hz-*.log`（`out/` 已被 `.gitignore` 忽略，磁盘保留）。

## 1. 环境

| 字段 | 值 |
|---|---|
| 复测日期 | 2026-11-08 22:25 – 22:45 (GMT+8) |
| 分支 / commit | `main` @ `dd02381`（工作区另有 3 处文档失步修正未提交，与本测量无关） |
| 机器名 / CPU | BUBU / Genuine Intel(R) CPU 0000 @ 2.40GHz（12 逻辑核） |
| 操作系统 | Microsoft Windows NT 10.0.26200.0（Windows 11） |
| .NET SDK | .NET 10.0.12 |
| 显卡 / 驱动 | Intel(R) UHD Graphics 630（渲染路径）；GameViewer Virtual Display Adapter（虚拟链路适配器） |
| **CurrentRefreshRate** | **Intel UHD 630 = 60 Hz** @ 1920×1080；GameViewer 虚拟适配器自报 144 Hz（非渲染路径） |
| **≥ 60Hz？** | 是（按 Intel 适配器的 60Hz 计；注意 `bench-60fps.ps1` 取首个 >0 的适配器会采到 144，需人工甄别） |
| PresentMode | Fifo（vsync）/ Immediate（`--no-vsync`），wgpu = Dx12，Bgra8Unorm |

## 2. 复测命令（全部实跑，`--no-build` 复用同一 Release 构建）

```powershell
dotnet build BubuUI.slnx -c Release                                   # 0 错误（2 个既有 CS8604 警告）
dotnet test  tests/BubuUI.Rendering.Tests -c Release                  # ② 65/65 通过
$env:RENDERSPIKE_ITERATIONS = 20
dotnet run --project samples/RenderSpike  -c Release -- bench            # ③ 离屏 bench
# ④ GalleryApp 两态
dotnet run --project samples/GalleryApp   -c Release -- --always-render --frames 600            # vsync
dotnet run --project samples/GalleryApp   -c Release -- --always-render --no-vsync --max-fps 60 --frames 1200
# ⑤ WindowSpike 四态（限速 on/off × vsync on/off）
dotnet run --project samples/WindowSpike  -c Release -- --frames 1200 --no-vsync --max-fps 60
dotnet run --project samples/WindowSpike  -c Release -- --frames 1200 --max-fps 60
dotnet run --project samples/WindowSpike  -c Release -- --frames 1200 --no-vsync
dotnet run --project samples/WindowSpike  -c Release -- --frames 600 --max-fps 10000            # 等效关掉限速器
```

> 说明：`--max-fps 10000` 时剩余时间恒 <1ms，`Thread.Sleep` 分支不触发，等效「无限速 + Fifo」，
> 这是 CLI 下唯一能测纯 Fifo 自由跑的方式（WindowSpike/WindowHost 在 vsync 模式下强制叠加 60fps 软限速）。

## 3. 结果

### 3.1 按模板判定表

| 指标 | 实测 | 来源 | 判定 |
|---|---|---|---|
| CPU 像素 golden / 渲染测试 | 65/65 通过 | Rendering.Tests | ✅ 必须通过 |
| RenderSpike GPU 200 元素 1280×800 | 14.90 ms（含 readback） | bench 表 | 参考（与 G1 报告 6.4ms 同口径差异为本次含回读路径） |
| RenderSpike 像素一致性 | 5 场景全部「一致」 | bench 表 | ✅ |
| 帧平均 avg（GalleryApp，vsync） | **6.94 ms** | `frame=`（不含限速器） | ✅ ≤16.7 |
| 帧 p95（GalleryApp，vsync） | **10.02 ms** | `p95=` | ✅ ≤16.7 |
| 帧平均 avg（GalleryApp，Immediate） | **4.73 ms** | `frame=` | ✅ ≤16.7 |
| 帧 p95（GalleryApp，Immediate） | **7.01 ms** | `p95=` | ✅ ≤16.7 |
| ticks / rendered / skipped | 600/600/0 与 1200/1200/0 | 汇总行 | 参考 |
| **墙钟 fps（软限速 60，任意 vsync）** | **33.1–33.4** | WindowSpike `fps=` | ❌ 未达 60 |
| **墙钟 fps（Fifo，无软限速）** | **49.9** | WindowSpike `fps=` | ❌ 未达 60 |
| 墙钟 fps（Immediate，无软限速） | 91.5 | WindowSpike `fps=` | 渲染上限参考 |

### 3.2 WindowSpike 分段与墙钟（四态对照）

| 运行形态 | 墙钟 fps | frame avg | acquire | render | present tail |
|---|---:|---:|---:|---:|---:|
| Immediate + 限速 60 | 33.1 | 30.17 ms | 5.57 ms | 5.34 ms | 0.14 ms |
| Fifo + 限速 60 | 33.4 | 29.96 ms | 4.97 ms | 4.77 ms | 0.13 ms |
| Immediate + 无限速 | 91.5 | 10.93 ms | 10.66 ms | 6.15 ms | 0.22 ms |
| Fifo + 无限速 | **49.9** | 20.04 ms | **19.77 ms（vblank 等待）** | 8.00 ms | 0.20 ms |

## 4. 结论

- [x] **帧成本 PASS**：渲染管线工作成本 avg 4.73–6.94ms / p95 7.01–10.02ms，全部 ≤16.7ms 预算，
  余量 2–3 倍。60fps 所需的渲染能力充分具备。
- [x] **60Hz 已生效**：纯 Fifo（无限速）下 acquire 呈 vblank 等待，主间隔 16.7ms、偶发 33.4ms
  （对比 29Hz 时代固定 33.3ms/帧）；`frame avg 20.04 ≈ 0.8×16.7 + 0.2×33.4`，即约 80% 帧命中
  一个 vblank、20% 错过跳到下一个。
- [ ] **墙钟「稳定 60fps」FAIL（新发现，两个宿主层问题，非渲染瓶颈）**：
  1. **软限速器 `Thread.Sleep` 粒度超调**——`WindowHost`/`WindowSpike` 在 vsync 模式下强制叠加
     `max-fps=60` 软限速；工作 ~10ms 后 `Sleep(6)` 实际睡 ~15.6ms（Windows 默认定时器粒度），
     tick 落到 ~30ms → **无论 vsync 开关，墙钟只有 ~33fps**。29Hz 时代该缺陷被刷新率掩盖。
     修复方向：`timeBeginPeriod(1)` / `CreateWaitableTimerEx(HIGH_RESOLUTION)`，或 vsync 模式下
     去掉软限速、交由 Fifo 阻塞自然锁帧。
  2. **Fifo 自由跑帧节奏抖动**——无软限速时 acquire 等待 16.7/33.4ms 双峰（80/20），有效 ~50fps。
     与 WindowSpike 既有注释「Fifo 下自由跑每次都会错过一个 vsync」一致；wgpu 默认
     `desired_maximum_frame_latency = 2`（后端未显式配置）。修复方向：显式 latency=1 或
     对齐 vblank 的提交时机（需确认 VelloSharp 0.5.0-alpha.3 暴露面）。

### 4.1 修复后复测（同日晚，问题 1 已修复、问题 2 上游阻塞）

代码修复（详见 §7）：`Win32Window` 存续期 `timeBeginPeriod(1)` + 软限速器提前 1ms 唤醒自旋。

| 运行形态（修复后） | 墙钟 fps | frame avg | 判读 |
|---|---:|---:|---|
| GalleryApp `Immediate` + 限速 60（1200 帧） | **~60**（ticks=1200 按节奏走完） | 6.02 ms / p95 8.61 | ✅ 代码侧节奏修准，帧成本不变 |
| WindowSpike `Immediate` + 限速 60（1200 帧） | **59.7** | 16.74 ms | ✅ 同上 |
| WindowSpike `Fifo` + 限速 60（修复后首测） | **59.7** | 16.75 ms / p95 16.68 | ✅ 合成节奏配合时代码可达 60 |
| WindowSpike `Fifo` + 限速 60（同代码复测 ×3） | 41.4 / 32.0 / **54.7** | 24.1 / 31.2 / 18.3 ms | ⚠️ 本机虚拟链 DWM 合成节奏在 32–60Hz 间波动，上屏消费速率由环境主导 |
| GalleryApp `Fifo` + 限速 60（600 帧） | ≈31–49（随合成波动） | 9.17–20.96 ms | ⚠️ 同上 |

- **判定**：问题 1（软限速）已修复并验证——`Immediate` 模式墙钟 59.7–59.8fps 稳定、
  `Fifo` 在合成节奏配合时 59.7fps；问题 2 的确定性修复（frame latency=1）被上游阻塞（§7.3）。
  本机虚拟链合成节奏波动使「稳定 60fps」的**上屏侧**结论仍不可下，需物理目标机。
- 按「先忽略 IsVsync == false 的问题、文档说明即可」的口径：`ISurfacePresenter.IsVsync`
  仅作诊断与未来 pacing 决策入口；`IsVsync == false`（Immediate 等）沿用软限速，
  不做 DWM 合成对齐，属刻意取舍。

## 5. 偏差记录

- 测量期间窗口前台、无遮挡、无人工交互；默认电源计划；后台为常规开发会话负载。
- 显示器为 GameViewer 虚拟显示链路（非物理面板）：Intel 适配器报告 60Hz，实际合成/串流路径
  的有效节奏以 Fifo acquire 等待实测为准（16.7ms 主频已出现）。
- `bench-60fps.ps1` 的刷新率自动采集取首个 >0 的适配器，本机会采到 GameViewer 的 144Hz 而非
  Intel 的 60Hz；本报告以人工查询结果为准。
- GalleryApp 的 `frame=` 不含软限速器耗时（计时点在限速前）；WindowSpike 的 `frame=` 含限速器
  （`interval.Restart()` 在限速后）。两表口径不同，对照时注意。

## 6. 关联待办

1. ~~WindowHost 帧节奏修复（问题 4.1）~~ ✅ 已修复（§7.1–7.2），`Immediate` 墙钟 59.7–59.8fps 验证。
2. ~~Vello surface 显式配置 frame latency（问题 4.2）~~ ⛔ 上游阻塞（§7.3）：VelloSharp 原生
   `vello_wgpu_surface_configure` 硬编码 `desired_maximum_frame_latency: 2`，C ABI 结构体无该字段；
   需上游（workgroupengineering/VelloSharp `ffi/vello_ffi`）暴露后才能做确定性 Fifo 锁帧。
3. 物理目标机复测：本机虚拟链 DWM 合成节奏波动（32–60Hz）主导 Fifo 上屏侧，稳定 60fps 结论
   需 ≥60Hz 物理显示器；复跑本报告 §2 命令，目标：`Immediate` 墙钟 fps ≥ 59 且 p95 ≤ 16.7ms，
   `Fifo` 上屏节奏用 `DwmGetCompositionTimingInfo` 或外拍核对。
4. `bench-60fps.ps1` 刷新率采集改为多适配器列出 + 取非虚拟适配器。
5. （已否决备档）DwmFlush 合成钟 pacing：见 §7.4，本机合成节奏抖动下反而更差，物理面板可再评估。

## 7. 帧节奏修复记录（2026-11-08 晚）

### 7.1 定时器分辨率（问题 4.1 根因）

`Win32Window` 构造/析构以引用计数调用 `timeBeginPeriod(1)`/`timeEndPeriod(1)`
（`src/BubuUI.Platform.Windows/Win32Native.cs` + `Win32Window.cs`）：`Thread.Sleep` 的实际粒度
等于系统定时器分辨率（默认 ~15.6ms），软限速器 `Sleep(6)` 实际睡一个量子，tick 落 ~30ms。

### 7.2 软限速器收敛（提前唤醒 + 自旋）

`WindowHost.Run` / `samples/WindowSpike` 的限速分支改为 `Sleep(remaining - 1.0)`（剩余 >2ms 时）
+ 末段自旋补足：1ms 分辨率下把 pacing 收敛到亚毫秒。`GalleryApp`/`WindowSpike` 两样例
`Immediate` 模式墙钟 59.7–59.8fps 稳定，`frame=` 帧成本不变（4.73–6.94ms）。

### 7.3 frame latency=1 上游阻塞（问题 4.2）

托管侧 `VelloSharp.WgpuSurfaceConfiguration`（0.5.0-alpha.3）无 latency 字段；溯源
`ffi/vello_ffi/src/lib.rs`（workgroupengineering/VelloSharp）确认原生
`vello_wgpu_surface_configure` 硬编码 `desired_maximum_frame_latency: 2`，C ABI 结构体
（usage/format/width/height/present_mode/alpha_mode/view_formats）不含该字段——托管层补字段、
P/Invoke 直调两条路都不通。确定性 Fifo 逐 vblank 锁帧需上游先暴露配置。

### 7.4 DwmFlush 合成钟 pacing 实验（已回退）

实现过「vsync 且 maxFps≥60 时帧首 `DwmFlush` 等合成、去掉软限速」的方案：
WindowSpike `Fifo` 实测 **43.5fps**（frame=23.01ms，acquire 阻塞 12.94ms）——本机虚拟链的
DWM 合成节奏抖动大，合成钟本身不可靠，把节奏锚上去反而引入反馈恶化。同代码回退后复测还发现
`Fifo` 墙钟随合成波动在 32–54.7fps 间漂移（§4.1），进一步佐证上屏侧瓶颈在环境。
结论：代码回退（`FramePacing`/`DwmFlush` P/Invoke 已删），实验数据留档；物理面板上可再评估。

### 7.5 契约补充

`ISurfacePresenter.IsVsync`（`Fifo`/`AutoVsync` 为 true）随本次新增到
`src/BubuUI.Rendering/ISurfacePresenter.cs`（`VelloSurfaceBackend` 返回实际配置的呈现模式）。
按决策口径：`IsVsync == false` 沿用软限速、不做 DWM 对齐，文档说明即可，不做专门处理。

## 8. 2026-11-09 复测（retained 显示列表 D93 后；机器已更换）

> `tools/bench-60fps.ps1 -RunWindowMeasure -IncludeWindowSpike -Frames 600 -MaxFps 60`
> 全步骤实跑（1m54s，日志 `out/bench-60fps-20261109-044321.log`）。与 §1–§7 **不是同一台机器**：
> 机器 DESKTOP-SFG7EQV，GPU 为 **NVIDIA GeForce RTX 2070**（渲染路径）+ GameViewer 虚拟适配器（0Hz），
> 物理适配器自报 **59Hz**（< 60 判定线，见 §8.3 环境提示）。本次同时验证 D93 retained 段缓存
> 落地后的构建/渲染链路与 `bench-60fps.ps1` 的刷新率采集改进（§6.4 完成：逐适配器列出 +
> 识别虚拟链路 + 优先物理适配器——本跑正确选中 RTX 2070 而非虚拟适配器）。

### 8.1 结果

| 指标 | 实测 | 判定 |
|---|---|---|
| Release 构建 | 0 警告 0 错误（24.8s） | ✅ |
| CPU 渲染测试（Release） | 84/84 通过 | ✅ 必须通过 |
| RenderSpike GPU 200 元素 1280×800 | **1.91 ms**（含 readback）；10k 命令 8.37 ms | 参考（RTX 2070，优于 G1 报告 Intel 630 的 6.4ms——机器差异，不与历史直接对比） |
| RenderSpike 像素一致性 | 5 场景全部「一致」 | ✅ |
| **GalleryCheck 帧成本（Immediate + 限速 60）** | **avg 3.00 ms / p95 3.43 ms**（600 帧，76 元素，damage 全 1 局部 599） | ✅ ≤16.7ms 预算 |
| **WindowSpike Immediate + 限速 60 墙钟** | **60.0 fps**（frame=16.68 含限速等待；stages acquire 2.91 / render 2.64 / present 0.24 ms） | ✅ 节奏修复在 D93 后保持（§4.1 口径 ≥59） |
| Fifo 自由跑 | 未复采（§7.3 上游 latency=2 阻塞，本机结论不因换机改变） | ⛔ 上游 |

### 8.2 判读

- **帧成本 PASS 且余量进一步拉大**：GalleryCheck avg 3.00ms / p95 3.43ms（预算 16.7ms 的 ~18%）；
  D93 retained 段缓存生效后构建层开销趋零（damage 局部 599 帧、覆盖率 0.00% 的静止画面全部复用段）。
- **Immediate 墙钟 60.0fps**：软限速修复（§7.1–7.2）在 retained 后保持有效。
- **上屏侧结论不变**：59Hz 面板 + 虚拟链路仍不满足「物理 ≥60Hz 目标机」前置；Fifo 确定性锁帧
  仍被 VelloSharp `desired_maximum_frame_latency=2` 阻塞（§7.3）。

### 8.3 环境提示（脚本输出）

`CurrentRefreshRate=59Hz < 60Hz` → 帧时间不能作为 60fps 达标依据，本次窗口测量结论记「不适用」；
帧成本判定（PASS）只反映渲染管线工作成本。两级判定口径见 §4。

## 9. 物理目标机复测指引（60fps 最终结论的唯一剩余前置）

1. **前置**：≥60Hz **物理**显示器（笔记本内屏通常 60/90/120/144Hz 即可）；独显/核显驱动正常；
   关闭可变刷新率（G-Sync/FreeSync）与 HDR（减少干扰项）；电源计划「高性能」；窗口保持前台无遮挡。
2. **步骤**（仓库根目录）：
   ```powershell
   dotnet build BubuUI.slnx -c Release
   powershell -File tools/bench-60fps.ps1 -RunWindowMeasure -IncludeWindowSpike -Frames 1200 -MaxFps 60
   ```
   脚本自动：列出适配器并优先取物理适配器刷新率 → Release 构建 → CPU 渲染测试 →
   RenderSpike GPU bench（像素一致性）→ GalleryCheck 窗口帧成本（Immediate）→
   WindowSpike Immediate 分段与墙钟 → 16.7ms 预算判定，全部落 `out/bench-60fps-*.log`。
3. **补充采集（Fifo 侧）**：
   ```powershell
   dotnet run --project sandbox/WindowSpike -c Release -- --frames 1200 --max-fps 60      # Fifo + 限速 60
   dotnet run --project sandbox/WindowSpike -c Release -- --frames 1200 --max-fps 10000   # Fifo 无软限速（acquire 观察 vblank）
   ```
   若 Fifo 墙钟仍波动：以 `DwmGetCompositionTimingInfo`（rateRefresh/rateCompose）或
   手机慢动作拍屏（240fps 拍 2 秒数帧）核对上屏节奏，区分「渲染侧」与「合成侧」。
4. **判定（两级口径，全部满足即给「稳定 60fps」结论）**：
   - 帧成本：GalleryCheck `frame=` avg 与 p95 ≤ 16.7ms（渲染管线能力）；
   - 墙钟：WindowSpike `Immediate + 限速 60` fps ≥ 59（pacing 能力）；
   - 上屏：Fifo + 限速 60 的墙钟 fps ≥ 59 且 acquire 无 33.4ms 双峰（vblank 逐帧命中；
     若仅此项受上游 latency=2 阻塞，在报告记录并在上游暴露配置后复测）。
5. **回填**：复制 `design/reports/target-machine-60fps-template.md` 为
   `target-machine-60fps-<日期>.md`，按模板口径填 §1 环境 / §2 命令 / §3 结果 / §4 结论；
   在 README 状态块与 design/09 台账登记结论行。
