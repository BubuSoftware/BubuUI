# 12. G2 第二切片：场景图 + 输入路由 + 可交互 Gallery

> 状态：**已执行，通过** · 时间盒：1 周（G2 第 2 周）· 关联计划：[09 开发计划](./09-development-plan.md#g22d-渲染垂直切片810-周)
> 前置：[11 G2 第一切片](./11-g2-surface-slice.md) · 后续：damage v0（已完成，见 [13](./13-g2-damage-v0.md)）、像素 golden
>
> 本文回答 G2 的核心问题：**元素树能不能直接画到窗口，并且 CounterApp / TaskList 在真实窗口里可交互？**

## 12.1 目标与非目标

### 目标

1. 元素树 → `DisplayList` 的场景图遍历（替换第一切片的手写 display list）。
2. 指针/键盘从 Win32 消息路由进元素树；无头宿主与窗口宿主共用同一条路径。
3. 文本 shaping 同时服务布局测量与绘制，进距一致。
4. `CounterApp` / `TaskList` 在真实 Win32 窗口中可交互（G2 出口标准之一）。
5. 程序化自检：用真实 `WM_LBUTTONDOWN/UP` 驱动复选框、条件按钮、列表追加与 resize。

### 非目标

- 样式/主题系统：`ElementTheme` 是临时数据对象，G4 由选择器结果替换；
- damage/layer、合成器、像素 golden（后续切片）；
- 字符输入 / IME / 文本编辑控件；
- 多行断行、bidi 混排、字体 fallback；
- 块协调器的**布局**透明化（G3，本切片只做视觉透明）；
- 图像、渐变等超出实色/描边的绘制能力。

## 12.2 任务看板

状态：`TODO` / `DOING` / `DONE` / `BLOCKED` / `CUT`

| ID | 任务 | 工期 | 状态 | 产出 |
|---|---|---:|---|---|
| G2-10 | scene graph → display list | 1.5d | DONE | `src/BubuUI.Rendering/SceneGraph/ElementPainter.cs` |
| G2-11 | 输入路由：窗口事件 → 元素树 / 焦点 | 1d | DONE | `InputRouter`、`WindowHost`、Win32 指针捕获 |
| G2-12 | 文本 shaping 接入布局与绘制 | 1d | DONE | `ITextPainter` / `HarfBuzzTextPainter` / `ITextMeasurer` |
| G2-15 | CounterApp + TaskList 真窗口可交互 | 1d | DONE | `samples/GalleryApp`（`--selfcheck` PASS） |
| G2-13 | damage/layer v0 | 2d | DONE | 元素级脏区 + 空闲跳帧，见 [13 G2 第三切片](./13-g2-damage-v0.md)；layer/局部重绘未做 |
| G2-14 | 像素探针 / golden | 1d | TODO | 截图容差比较纳入 CI |

**关键路径**：G2-10 → G2-12 → G2-15。
**可并行**：G2-11 与 G2-10；G2-14 与 G2-13。

## 12.3 结构

```
.bui → 元素树 → Measure/Arrange → ElementPainter ─┬→ VelloCpuBackend → RenderTarget → PNG / 探针
                                                └→ VelloSurfaceBackend → wgpu surface → 窗口像素
                                                          ↑
                          WindowHost（消息循环 + 帧循环）← Win32Window（鼠标 / 键盘 / resize / DPI）
                                                          ↑
                          InputRouter（HeadlessRoot 与窗口宿主共用：hit test → 捕获 → 路由 → 焦点）
```

### 分层与依赖

| 层 | 程序集 | 新增内容 | 说明 |
|---|---|---|---|
| 运行时 | `BubuUI.Runtime` | `InputRouter`、`Panel.Padding` / `ClipToBounds`、`IsVisualTransparent`、`IsPressed` | 不感知渲染后端 |
| 渲染契约 | `BubuUI.Rendering` | `SceneGraph/ElementPainter`、`ElementTheme`、`ITextPainter` / `MeasuringTextPainter` | 读元素树建 display list，不依赖 Vello / HarfBuzz |
| 文本 | `BubuUI.Text.Shaping` | `HarfBuzzTextPainter`、`ITextMeasurer` 实现、`LineMetrics`、线程安全 shaping | 布局与绘制共用一个 shaping 缓存 |
| 平台 | `BubuUI.Platform.Windows` | `WindowHost`、`Close`、`PostPointerClick`、指针捕获 | 纯 P/Invoke |
| 样例 | `samples/GalleryApp` | Counter + TaskList + 6 步 selfcheck | G2 验收 harness |

### 绘制顺序与坐标系

- 每个元素的 `FinalRect` 位于**父元素局部坐标**；遍历时累加偏移，作为 `DisplayList` 的根坐标。
- 背景/卡片/控件 chrome 由 `ElementPainter` 统一绘制，控件本身不产生绘制指令。
- 文本按 `TextRun.FinalRect` 定位，颜色暂时按字号阈值区分主/次文本。
- DPI 缩放作为**一次根变换**（`PushTransform`），`DisplayList.Size` 保持逻辑单位；
  设备像素尺寸由 surface / render target 决定。

## 12.4 关键决策与发现

### SG-1：场景图放在渲染契约层，而不是运行时

`ElementPainter` 只读运行时树（`FinalRect`、`Children`、`IsPressed`、`IsFocusable`），
输出 `DisplayList`。好处是运行时不需要引用渲染契约之外的任何后端；代价是 painter 必须按
控件类型 `switch` 绘制 chrome——这是样式系统落地前的过渡形态。

### SG-2：块协调器先做“视觉透明”

`ConditionalBlock` / `ForEachBlock` 仍是 `StackPanelBase`，参与布局（G3 才做布局透明），
但通过 `Element.IsVisualTransparent` 跳过自身背景/边框。否则 `@if` / `@foreach` 会被画成卡片。

### SG-3：布局与绘制共用同一个 shaping 缓存

`HarfBuzzTextPainter` 同时实现 `ITextPainter` 与 `ITextMeasurer`：

- 布局：`TextRun.MeasureCore` → `TextMeasurer.Current.Measure` → painter 缓存；
- 绘制：painter 从同一缓存取 `ShapedBuffer` → `DrawGlyphs`。

这样“布局算出的宽”和“画出的字形进距”是同一份数据；无字体机器回落
`MeasuringTextPainter`（只测量不绘制），布局仍可用。缓存上限 512 条，超出整体清空。

### SG-4：点击焦点规则与指针捕获

- 命中保持“最深层元素”，键盘焦点交给**最近可聚焦祖先**（与平台控件习惯一致）。
- 按下即 `SetCapture`，释放时 `ReleaseCapture`；拖出窗口后释放仍归按下元素，不会“卡在按下态”。
- 无头与窗口共用 `InputRouter`，测试里点的和窗口里点的是同一条路径。
- `Tab` 由焦点系统拦截做遍历，其余按键从焦点元素（或根）路由。

### SG-5：窗口帧循环由宿主驱动

`WindowHost.Run` 每轮：`PumpMessages`（非阻塞）→ 跳过最小化 → `Measure/Arrange` →
`ElementPainter.Build` → `presenter.Render` → 可选限帧（sleep + spin 补足）。
resize 走 `WM_SIZE → presenter.Resize`，不等下一帧重建 painter。
`MaxFramesPerSecond` 用于把“渲染成本”和“vsync 等待”分开测量。

### SG-6：自检用真实消息路径，而不是直接调 API

`GalleryApp --selfcheck` 通过 `PostPointerClick` 投递真实 `WM_LBUTTONDOWN/UP`，
覆盖 **消息 → InputRouter → 信号 → 元素树重建 → 下一帧绘制** 全链路；
resize 同样用 `SetClientSize` 走 `WM_SIZE`。直接调 `InputRouter` 会绕开平台桥接，测不到回归。

### SG-7：`Padding` / `ClipToBounds` 先落在 Panel 上

布局与裁剪需要这两个基础属性，但它们最终属于样式系统。当前以 `StyledProperty` 落在
`Panel`（`Padding` 触发 Measure 失效并钳制非负），`ListBoxItem` 默认 8px；
G4 引入选择器后再迁移/覆盖。

## 12.5 验证

### 自检（真实窗口，`--selfcheck`）

```
surface: 1000x760 px, vsync=False, max-fps=0
selfcheck: ok checkbox toggles showButton
selfcheck: ok conditional +1 button mounted
selfcheck: ok +1 click increments count
selfcheck: ok add task appends item
selfcheck: ok surface follows resize
selfcheck: ok surface restored after resize
state: counter=1 showButton=True tasks=4 buttonClicks=2
selfcheck: PASS
```

覆盖：复选框双向绑定 → `@if` 挂载 → 按钮点击改计数 → `@foreach` 追加条目 →
resize 到 860×600 → 恢复 1000×760，每步都校验 surface 尺寸与客户区设备像素一致。

### 上屏正确性

- `tools/capture-window.ps1` 截取真实窗口客户区 → `out/gallery-screen.png`；
- 同一元素树走 `VelloCpuBackend` → `out/gallery-cpu.png`（30 条命令，hash `0EB9C9A18E24952E`）；
- 人工比对：卡片圆角/边框、按钮、勾选框、焦点环、文本位置与颜色一致。
  （焦点环只在 CPU 图中出现，因为截图时窗口未持有焦点，属预期差异。）

### 回归测试

`dotnet test`：**113 个测试全绿**（63 无头 + 50 渲染，新增 10 个布局/输入测试 + 14 个场景图测试）。
场景图测试覆盖：surface 填充与栈平衡、按钮/勾选框 chrome、按下态、焦点环、裁剪、
DPI 根变换、块视觉透明、Panel padding、HarfBuzz glyph run、测量/绘制度量一致。

### 帧成本（Release，`Immediate`，60fps 上限）

场景：Gallery 默认状态，25 个元素，约 28–30 条命令。帧时间 =
`Measure + Arrange + ElementPainter.Build + presenter.Render(submit)`，不含 GPU 异步完成。

| 尺寸 | 帧时间 avg | p95 | 说明 |
|---|---:|---:|---|
| 1000×760 | 4.42–4.64 ms | 5.76–6.00 ms | 两次独立运行 |
| 1280×800 | 4.49 ms | 5.95 ms | G2 出口尺寸 |
| 1000×760 selfcheck | 13.59 ms | 14.71 ms | 含 2 次 surface 重配与 resize 抖动，不代表稳态 |

60fps 预算 16.7 ms，1280×800 稳态成本约 4.5 ms，余量充足；但本机为 29 Hz 虚拟显示，
**60fps 结论仍需目标机复测**。`--no-vsync` 下 `Immediate` 的 acquire 波动（±2×）属已知现象。

## 12.6 G2 出口标准核对

| 出口标准 | 结果 | 说明 |
|---|---|---|
| `CounterApp`、`TaskList` 在真实窗口可交互 | ✅ | GalleryApp 自检 6 步全过，真实鼠标消息驱动 |
| resize / DPI 变化不崩、不闪 | ✅ | 860×600 ↔ 1000×760 自检；第一切片另测 4 档尺寸 |
| 1280×800 简单场景稳定 60fps | ⏳ 目标机 | 本机稳态帧成本 4.49 ms（p95 5.95 ms），受 29 Hz 显示限制不能下 60fps 结论 |
| `dotnet test` 含渲染测试且跨机器稳定 | ✅ | 113 个测试；GPU 用例无设备时自动跳过 |

## 12.7 风险与后续

| 风险 | 触发信号 | 应对 |
|---|---|---|
| 全量重绘随元素增长线性变慢 | 元素数 × 命令数上涨，帧成本同步上涨 | G2-13 已做元素级脏区与空闲跳帧；局部重绘/图层待 retained layer |
| 主题是硬编码数据 | 新增控件要改 painter + theme | G4 样式系统，painter 改为读解析后的样式 |
| 文本只有单行 LTR | 多行/混排文本溢出或错位 | 断行 + bidi + fallback 排入 G2/G3 |
| 字符输入未接 | 无文本编辑控件 | `WM_CHAR` → 文本输入路由，随 TextBox 一起做 |
| 截图与 golden 未自动化 | 视觉回归靠人工 | G2-14 像素探针 + 容差比较 |
| 目标机 60fps 未验证 | 本机虚拟显示 29 Hz | 目标机 `--no-vsync` 复测并记录分段耗时 |

后续任务（G2 剩余）：

1. ~~**G2-13 damage/layer v0**：全量重绘 + 脏矩形统计，再按脏区裁剪；量化元素数-帧成本曲线。~~
   已完成元素级脏区 + 空闲跳帧，见 [13](./13-g2-damage-v0.md)；局部重绘/图层待 retained layer。
2. **G2-14 像素探针与 golden**：窗口截图/CPU 渲染 → 容差比较，纳入 CI（无 GPU 跳过）。
3. **文本整段能力**：多行断行、字体 fallback、glyph 缓存统计。
4. **输入补全**：`WM_CHAR` 文本输入、Shift+Tab、鼠标悬停/光标形状。
5. **G3 并行项**：块布局透明化、状态与反应式 S1–S3（[spec/03](../spec/03-reactivity-and-binding.md)：`Invalidate`/`Render`/`P={expr}`/块 `Refresh`/item 重建/`@bind-P`）、列表作用域泄漏修复。

## 12.8 日志

### 2026-10-28

- 完成：`ElementPainter`（元素树遍历、控件 chrome、焦点环、裁剪、DPI 根变换）、
  `InputRouter`（无头/窗口共用）、`WindowHost`（帧循环/输入/resize/限帧）、
  `HarfBuzzTextPainter`（布局与绘制共用 shaping 缓存）、`Panel.Padding` / `ClipToBounds`、
  `GalleryApp`（Counter + TaskList + 6 步自检）。
- 数据：113 测试全绿；自检 PASS；1000×760 稳态 4.42–4.64 ms（p95 5.76–6.00），
  1280×800 4.49 ms（p95 5.95）；CPU 输出 30 条命令。
- 阻塞：无。
- 截图/产物：`out/gallery-screen.png`（真实窗口）、`out/gallery-cpu.png`（CPU 对照）。
- 明日：G2-13 damage/layer v0；G2-14 像素探针；目标机 60fps 复测。
