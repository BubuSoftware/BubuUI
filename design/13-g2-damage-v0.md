# 13. G2 第三切片：damage v0（元素级脏区 + 空闲跳帧）

> 状态：**已执行，通过** · 时间盒：1–2 天 · 关联计划：[09 开发计划](./09-development-plan.md#g2-2d-渲染垂直切片810-周)
> 前置：[12 G2 第二切片](./12-g2-interactive-slice.md) · 后续：G2-14 像素 golden、retained layer / 局部重绘
>
> 本文回答一个问题：**帧循环能不能知道“这一帧变了多少”，并在没有变化时完全不提交 GPU？**

## 13.1 目标与非目标

### 目标

1. 元素级失效追踪：属性变更、按下态、焦点、子树增删、布局矩形变化都能进入脏区。
2. 每帧输出 `FrameDamage`：根坐标脏矩形、重叠合并、裁剪到画面、覆盖率可计算。
3. 脏区为空的 tick 跳过 present；首帧 / resize / DPI / 遮挡恢复走整帧。
4. 宿主暴露 `ticks / rendered / skipped / damage` 可观测数据；样例打印覆盖率。
5. 量化“跟踪本身”的 CPU 开销，确认不值得为 25 元素场景做额外优化。

### 非目标

- **真正的局部重绘**：当前 `VelloSurfaceBackend.Render` 每次 acquire 新 swapchain texture
  并以清屏色开始渲染；只画脏区会把其余区域擦掉。局部重绘需要保留纹理或合成器，见 DG-4。
- retained layer / 分层合成 / 动画调度。
- 行内文本的精确脏区（v0 以元素外接矩形 + 焦点环外扩为单位）。
- GPU 侧 query、多窗口共享脏区、跨帧脏区累积（当前每帧独立判定）。

## 13.2 设计

### DG-1：`RenderVersion` 是所有“像素可能变化”的统一入口

`Element` 增加单调递增的 `RenderVersion` 与 `InvalidateRender()`。以下路径必须 bump：

| 来源 | 位置 | 说明 |
|---|---|---|
| StyledProperty 变更 | `Element.SetValue` | 值短路之后；无论失效级别是 Measure/Arrange/Render |
| 继承属性变更 | `NotifyInheritedChange` | 逐后代 bump（并保留原有 Measure 失效） |
| 控件按下态 | `Button` / `CheckBox` | `IsPressed` 是普通字段，需显式 `InvalidateRender()` |
| 焦点变化 | `FocusManager.Focus/ClearFocus` | 旧、新元素都 bump（焦点环 + 键盘态） |
| 子树增删 | `ElementCollection.Add/Remove/Clear` | owner bump；新增/移除元素由快照 diff 兜底 |
| TextRun 属性变更 | `TextBlock.AddRun` 订阅 `run.PropertyChanged` | run 文字画在 block 自身段里（段键 = block.RenderVersion）：run 变化连带 bump block（D113） |

`Arrange` 级属性（如 `Padding`）即使不改变自身像素，也会移动子元素——快照矩形 diff 兜底，
不依赖控件自觉。

**D113 回归（2026-11-12）**：上表曾漏「父元素自绘读取子元素属性」这一来源——`PaintTextBlock`
把全部 run 的文字画进 TextBlock 自身段，但 `run.Text` 变更只 bump run 的 RenderVersion，
block 段键不变 → 复用旧文字段：模型已更新、像素陈旧。画廊表现为点 +1 数字不变；
TextBlock 被拉伸到整行宽，悬停文字后空白区命中 block → 伪类态变 → 段失配重画才显示新值
（「鼠标移过去才变」的真实机制）。CheckBox 勾选走自身属性/结构增删，不受影响。
「retained == 全量」金标准两边同复用旧段抓不到（同 D97 盲区）；
`RetainedPaintTests.RunTextChanged_RepaintsBlockSegment_WithNewText` 用记录型绘制器锁定。

### DG-2：painter 做快照 diff，不做 dirty 传播

`DamageTracker` 每帧记录 `(绝对矩形, RenderVersion)`：

- 首帧 / 尺寸变化 / DPI 变化 / `ResetDamage()` → `IsFull`；
- 新挂载元素 → 加入其矩形；
- 版本或矩形变化 → 同时加入旧矩形与新矩形（擦除 + 重画）；
- 上一帧存在、本帧消失 → 加入旧矩形；
- 焦点元素矩形按 `FocusRingWidth` 外扩（焦点环画在元素外）；
- 全部矩形裁剪到 surface 范围，避免负坐标/越界让覆盖率失真。

`FrameDamage.Add` 对重叠（含相邻）矩形做合并；`Area` / `Bounds` / `Coverage(surface)` 直接可读。
这是 O(n)/帧的实现，但不要求控件上报几何变化，正确性由“快照对比”保证——漏报只可能来自
“状态变了但既没 bump 版本、也没改矩形”的路径，当前所有绘制输入都受 DG-1 覆盖。

### DG-3：宿主跳过的是 present，不是构建

`WindowHost` 每个 tick 仍然 `Measure + Arrange + BuildFrame`（否则无从得知有没有变化），
但只在 `damage 非空`（或 `AlwaysRender=true`）时调用 `presenter.Render`。
无脏区时 tick 休眠 `IdleSleepMilliseconds`（默认 4 ms），避免空转占满 CPU。

- `WM_SIZE` / `WM_DPICHANGED` → `surface.Configure` + `ResetDamage()` → 下一帧整帧；
- `WM_PAINT`（窗口被遮挡后恢复）→ `ResetDamage()`，避免 DWM 拿旧内容；
- `FrameTicked` 事件携带 `Presented` 与 `FrameDamage`，样例据此统计。

### DG-4：为什么 v0 不做局部重绘

`VelloSurfaceBackend.Render` 的执行模型是：acquire 下一张 surface texture → 以清屏色
`(0,0,0,255)` 开始 render pass → 整幅重画 → present。交换链纹理内容是“上一张的后备缓冲”，
不是“当前屏幕内容”，直接在其上只画脏区会得到被清空的画面。

要做真正的局部重绘，至少需要以下之一：

1. **保留纹理**：渲染到持久纹理，仅将脏区更新后整幅 copy 到 swapchain（省的是光栅化，不是带宽）；
2. **合成器 + layer**：每个脏区/图层独立纹理，present 时合成（Vello 场景仍需整幅构建）；
3. **Vello 增量场景**：保留 scene，按 dirty 重建部分编码（受 VelloSharp API 限制）。

v0 的收益路径是“**空闲零提交** + 为上述方案提供输入契约（脏区坐标与覆盖率）**”，
不做“只画脏区”的假优化——视觉上正确比数字好看重要。

### DG-5：覆盖率口径

`Coverage` = 合并后脏矩形面积 / 画面面积，包含脏矩形内的未变像素，只用于比较不同交互的
“影响范围”，不代表实际光栅化工作量。整帧重绘恒为 1。

## 13.3 实现清单

| 文件 | 变更 |
|---|---|
| `src/EclipseUI.Runtime/Element.cs` | `RenderVersion` / `InvalidateRender()`；SetValue / 继承传播 / 子集合挂接 |
| `src/EclipseUI.Runtime/FocusManager.cs` | 焦点切换 bump 旧/新元素 |
| `src/EclipseUI.Runtime/Controls.cs` | 按下/释放 bump |
| `src/EclipseUI.Rendering/SceneGraph/FrameDamage.cs` | 新增：`FrameDamage` + `DamageTracker` |
| `src/EclipseUI.Rendering/SceneGraph/ElementPainter.cs` | `BuildFrame` / `PaintedFrame` / `ResetDamage`；按元素 visit |
| `src/EclipseUI.Platform.Windows/WindowHost.cs` | 跳帧 present、`AlwaysRender`、`IdleSleepMilliseconds`、`FrameTicked`、统计属性 |
| `samples/GalleryApp/Program.cs` | `--always-render`；damage 覆盖率与 ticks/rendered/skipped 汇总 |
| `tests/EclipseUI.Rendering.Tests/DamageTests.cs` | 新增 11 个用例 |

## 13.4 验证

### 回归测试

`dotnet test`：**124 个测试全绿**（63 无头 + 61 渲染；新增 11 个 damage 用例）。

damage 用例覆盖：首帧整帧 / 不变帧为空、复选框切换、文本变长（旧+新矩形）、
布局位移（Padding 改变）、元素移除、焦点切换（旧/新焦点环区域）、指针按下、
尺寸与 DPI 变化整帧、`ResetDamage`、`Build` 清除历史、重叠矩形合并与覆盖率。

### 真实窗口自检（`--selfcheck --no-vsync`）

```
selfcheck: damage 1 rect(s), 46.89%
selfcheck: ok checkbox toggles showButton
selfcheck: ok conditional +1 button mounted
selfcheck: damage 3 rect(s), 8.21%
selfcheck: ok +1 click increments count
selfcheck: damage 3 rect(s), 34.04%
selfcheck: ok add task appends item
selfcheck: ok surface follows resize
selfcheck: ok surface restored after resize
ticks=150 rendered=6 skipped=144 measured=0 frame=n/a
damage: full=3 partial=3 coverage(avg/max)=29.71% avg, 46.89% max
selfcheck: PASS
```

- 150 个 tick 只提交 6 帧：首帧整帧、3 次交互局部帧、2 次 resize 整帧；
- 复选框 toggle（挂载 `+1` 按钮 + 焦点环 + 下方内容位移）覆盖率 46.89%，
  计数 +1 的文本更新 8.21%，追加任务 34.04%；
- `full=3` = 首帧 + 两次 resize；`partial=3` = 三次交互。

### 空闲跳帧（`--no-vsync --frames 400`，无输入）

```
ticks=400 rendered=1 skipped=399
damage: full=1 partial=0 coverage(avg/max)=n/a
```

稳态空闲时只有首帧上屏，其余 399 个 tick 全部跳过。

### 帧成本（Release，`Immediate`，`--always-render` 强制每帧上屏，60fps 上限）

| 尺寸 | 帧时间 avg | p95 | 说明 |
|---|---:|---:|---|
| 1000×760 | 4.74–5.03 ms | 6.34–6.96 ms | 当日 5 次运行 |
| 1280×800 | 4.88–5.06 ms | 6.75–7.01 ms | 3 次运行 |

与第二切片（1000×760 4.42–4.64 / p95 5.76–6.00）相比高约 0.2–0.6 ms，
属于同日机器负载 / `Immediate` acquire 波动范围；下表的跟踪开销说明它不是原因。

### 跟踪开销（离屏微基准，22 / 202 / 1002 元素树，5 轮取最小）

| 元素数 | `Build` | `BuildFrame` | 跟踪开销 |
|---:|---:|---:|---:|
| 22 | 14–20 µs | 15–29 µs | ≈ 0–12 µs（噪声级） |
| 202 | 51–65 µs | 67–87 µs | 10–22 µs |
| 1002 | 258–273 µs | 356–381 µs | 96–121 µs |

跟踪约为 0.1 µs/元素；Gallery（25 元素）约 2–5 µs，占 4.7 ms 帧成本的 0.1%。
初版每帧新建字典，1002 元素时开销 172 µs；改为复用并清空两个字典后降到 ≤121 µs。

## 13.5 风险与后续

| 风险 | 触发信号 | 应对 |
|---|---|---|
| 漏报：状态变了但没 bump/没改矩形 | 画面停在旧内容，需交互才刷新 | DG-1 覆盖所有绘制输入；动画/样式落地时必须调用 `InvalidateRender()`；G2-14 像素探针兜底 |
| 大面积脏区导致跳帧收益有限 | 大量动画/文本滚动 | 下一步 retained layer / 局部重绘；引入动画调度后再评估 |
| swapchain 内容假设 | 最小化/遮挡恢复后画面异常 | `WM_PAINT`/resize 强制整帧；第一切片的 Configure 重试仍有效 |
| 覆盖率被误读为工作量 | 文档/报表比较 | 明确口径（DG-5），性能只认帧时间 |

后续：

1. **G2-14 像素 golden**：窗口截图/CPU 渲染 → 容差比较，纳入 CI（无 GPU 跳过）。
2. **retained layer / 局部重绘**：保留纹理或合成器，让脏区真正减少光栅化与带宽。
3. **动画调度**：按动画时间线驱动 `InvalidateRender`，避免逐帧整帧。
4. **目标机 60fps 复测**：本机 29 Hz 虚拟显示只能证明帧成本，不能证明呈现节奏。

## 13.6 日志

### 2026-10-28

- 完成：`RenderVersion` 失效模型、`FrameDamage` / `DamageTracker` 快照 diff、
  `ElementPainter.BuildFrame`、`WindowHost` 空闲跳帧与 `AlwaysRender`、
  GalleryApp damage 统计、11 个 damage 测试。
- 数据：124 测试全绿；selfcheck PASS（150 tick 提交 6 帧）；空闲 400 tick 提交 1 帧；
  `--always-render` 稳态 1000×760 4.74–5.03 ms（p95 6.34–6.96），
  1280×800 4.88–5.06 ms（p95 6.75–7.01）；跟踪开销 ≈ 0.1 µs/元素。
- 阻塞：无。
- 日志/产物：`out/g2-13-selfcheck.log`、`out/g2-13-idle.log`、`out/g2-13-perf-1000.log`、`out/g2-13-perf-1280.log`。
- 下一步：G2-14 像素 golden；retained layer；目标机 60fps 复测。
