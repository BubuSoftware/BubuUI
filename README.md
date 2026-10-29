# EclipseUI

模板引擎优先的自绘 UI 框架。设计文档见 [design/](./design/README.md)，执行计划见 [开发计划](./design/09-development-plan.md) 与 [G1 渲染 Gate 执行清单](./design/10-g1-render-gate.md)。
`.ui` 语法与生成代码契约的规范正本见 [spec/](./spec/README.md)（[语法](./spec/01-template-syntax.md)、[生成代码](./spec/02-generated-code-contract.md)）。

> 状态：M1（托管部分 + HarfBuzz shaping spike）+ M2 编译器 MVP + M3 无头垂直切片 + M4 输入与焦点（无头形态）已落地；
> **G1 渲染 Gate 已出结论：Go（有条件）**——Vello 后端在 Windows GPU 上 200 元素 1280×800 ≈ 6.4 ms/帧，
> 功能矩阵 8/9，CPU/GPU 像素一致。报告见 [render-gate-2026-11](./design/reports/render-gate-2026-11.md)。
> **G2 第一切片已通过**：Win32 窗口 + wgpu(Dx12) surface 上屏，resize 冒烟通过，见
> [11 G2 第一切片](./design/11-g2-surface-slice.md)。
> **G2 第二切片已通过**：元素树→display list、输入路由、文本 shaping 接入布局与绘制；
> CounterApp/TaskList 在真实窗口可交互，1000×760/25 元素稳态帧成本 ≈ 4.4 ms（p95 5.8 ms），见
> [12 G2 第二切片](./design/12-g2-interactive-slice.md)。
> **G2 第三切片已通过**：元素级 damage（`RenderVersion` + 快照 diff）与空闲跳帧——
> 空闲 400 tick 仅首帧上屏，交互帧按脏区提交；跟踪开销 ≈ 0.1 µs/元素，见
> [13 G2 第三切片](./design/13-g2-damage-v0.md)。
> **S1 失效重算切片已通过**：普通 CLR 状态 + `Invalidate`/`Render`，根级属性/文本重算、
> 控件事件自动失效；规范正本 [spec/01–03](./spec/README.md)（v4），142 个测试全绿
> （Headless 77 / Compiler 4 / Rendering 61）。块与双向绑定仍是 v3 过渡路径（S2–S5）。
> 样式主题、IDE 支持为后续里程碑。

## 核心理念

**模板不是"字符串拼 UI"的外壳，而是编译单元**——编译期完成类型检查、字面量转换、
更新路径分析，运行时只执行生成好的指令：无运行时 diff，无虚拟 DOM，
绑定点由编译器生成定向更新函数；状态是普通 CLR 成员，失效驱动定向重算。

## 目录结构

```
design/                          设计文档（架构 8 篇 + 计划、切片报告、控件契约）
src/
  EclipseUI.Runtime/             运行时：失效队列与所有权（S1）、StyledProperty 属性引擎、
                                 元素树、measure/arrange 布局、块协调器（v3 订阅，S2 迁移）、
                                 路由事件、InputRouter 输入/焦点、Panel.Padding/ClipToBounds、无头宿主
  EclipseUI.Text.Shaping/        HarfBuzz shaping（HarfBuzzSharp）：先 shaping 再测量，
                                 HarfBuzzTextPainter 让布局与绘制共用同一 shaping 缓存
  EclipseUI.Compiler/            .ui 编译器前端：Lexer / Parser / AST / 内置目录 / 诊断（netstandard2.0）
  EclipseUI.Compiler.Generator/  Roslyn 增量源生成器：code-behind 发现、@foreach item 类型推断、
                                 代码发射（#line 映射回 .ui，诊断与断点共用）
samples/CounterApp/              三个样例组件：Counter（计数器/条件/双向绑定）、
                                 TaskList（@foreach Key 对齐）、S1Probe（v4 失效切片）；
                                 生成的 C# 落盘在 Generated/ 供检视
samples/GalleryApp/              G2 验收样例：Counter + TaskList 真窗口交互、--selfcheck、
                                 CPU/窗口截图与帧时间统计
tests/EclipseUI.Headless.Tests/  77 个无头测试：属性引擎、布局（含 padding）、块协调、
                                 输入/焦点路由、shaping、编译器、失效队列/所有权、端到端（含泄漏回归）
tests/EclipseUI.Compiler.Tests/  4 个生成器契约测试：确定性 hint、根级字段提升与 Render、
                                 事件失效包装、#line 映射（Roslyn GeneratorDriver）
tests/EclipseUI.Rendering.Tests/ 61 个渲染测试：图元/作用域/文本/功能矩阵/确定性/CPU-GPU 交叉一致、
                                 场景图绘制语义、damage 脏区检测
src/EclipseUI.Rendering/         渲染契约：DisplayList、DrawCommand、Brush、Transform、PngEncoder、
                                 SceneGraph/ElementPainter（元素树→display list）、FrameDamage/DamageTracker、
                                 IRenderBackend、ISurfacePresenter（上屏目标）
src/EclipseUI.Rendering.Vello/   Vello 后端：VelloSceneBuilder（CPU/GPU 共用）、CPU 离屏、wgpu surface
src/EclipseUI.Platform.Windows/  Win32 窗口宿主：消息循环、DPI、resize、鼠标/键盘事件、
                                 WindowHost（布局+绘制+damage 跳帧上屏循环）
samples/RenderSpike/             G1 像素管线 spike：PNG 输出 + 性能 bench
samples/WindowSpike/             G2 上屏 spike：动画场景 + 帧时间分段 + resize 自检
tools/capture-window.ps1         截取真实窗口客户区 PNG，用于像素核对
```

## 快速开始

```bash
dotnet build EclipseUI.slnx
dotnet test  EclipseUI.slnx

# G1：离屏 PNG 与性能表
dotnet run --project samples/RenderSpike -c Release
dotnet run --project samples/RenderSpike -c Release -- bench

# G2：真实窗口（Esc/关闭按钮退出；--frames N 自动退出）
dotnet run --project samples/WindowSpike -c Release -- --frames 300
dotnet run --project samples/WindowSpike -c Release -- --selfcheck   # resize 冒烟

# G2 验收：Counter + TaskList 真窗口交互（消息 → 输入路由 → 信号 → 重绘）
dotnet run --project samples/GalleryApp -c Release
dotnet run --project samples/GalleryApp -c Release -- --selfcheck --no-vsync   # 6 步自检 + damage 覆盖率
dotnet run --project samples/GalleryApp -c Release -- --no-vsync --frames 400  # 空闲跳帧：rendered=1
dotnet run --project samples/GalleryApp -c Release -- --always-render --no-vsync --max-fps 60 --frames 1200  # 帧成本
dotnet run --project samples/GalleryApp -c Release -- --cpu --shot out/gallery-cpu.png
```

模板（`samples/CounterApp/Counter.ui`）：

```xml
<StackPanel Gap="8">
    <TextBlock FontSize="30">You pressed {count} times</TextBlock>
    <CheckBox Checked:bind={showButton}>Button visible</CheckBox>
    @if (showButton) {
        <Button OnClick={() => count.value++}>+1</Button>
    }
</StackPanel>
```

code-behind（同名 `.cs`，无继承样板）：

```csharp
public partial class Counter
{
    public readonly Signal<int> count = new(0);
    public readonly Signal<bool> showButton = new(false);
}
```

`.ui` 作为 AdditionalFile 进入编译，由增量源生成器生成 `Build()`：
结构创建指令 + 每个绑定点的定向更新函数 + signal 订阅接线。
表达式中裸信号引用自动补 `.Value`（`{count}` → `count.Value`）。

## 已实现的关键语义

- **属性引擎**：`StyledProperty<T>`（默认值 / 继承 / 失效分级 Measure–Arrange–Render），
  setter 值短路无装箱；变更只标脏，布局在 `HeadlessRoot.UpdateLayout()` 帧内增量执行。
- **文本一等公民**：`<TextBlock>You pressed {count} times</TextBlock>` 编译为多个
  TextRun，各自独立定向更新。
- **块协调器**：`@if` 挂载/卸载内容并随内容释放订阅（测试含泄漏探测；条件初始为真时
  SetContent 晚到也会正确挂载）；`@foreach` 按 `Key={...}` 对齐新旧条目，复用实例并按新
  item 重跑更新函数——O(变动数)，移除条目时连带释放其作用域内订阅（`BlockScope.Disposables`）。
- **订阅放置随上下文**：生成器把双向绑定/嵌套块的订阅挂到正确的作用域——根级 `Track`、
  条件内容挂内容订阅表（卸载即释放）、item 模板挂 scope 订阅表（条目移除即释放），
  从机制上杜绝 toggle/增删条目时的订阅泄漏。
- **双向绑定**：`Checked:bind={showButton}` 生成 `Bind.TwoWay`，值短路防回环。
- **输入与焦点**：`InputRouter` 统一 hit test、按下捕获、指针移动/释放与键盘路由；
  无头宿主（`HeadlessRoot`）与 Win32 窗口宿主共用同一条路径。点击保持最深层命中、
  键盘焦点交给最近可聚焦祖先；`FocusManager` 负责 Tab 环形遍历与 Got/LostFocus，
  空格/回车激活（键盘可交互 demo）。按下即 Win32 `SetCapture`，拖出窗口释放不会丢状态。
- **场景图绘制（G2 第二切片）**：`ElementPainter` 遍历元素树，按 `FinalRect` 累加根坐标，
  输出卡片/按钮/勾选框/文本/焦点环/裁剪指令；DPI 作为一次根变换。
  块协调器视觉透明（布局透明留给 G3）。详见 [12 G2 第二切片](./design/12-g2-interactive-slice.md)。
- **文本 shaping**：`ITextShaper` 契约 + `HarfBuzzTextShaper`（HarfBuzzSharp），
  RTL 簇序、比例字宽、行盒高度均有真实字体（arial.ttf）测试；`HarfBuzzTextPainter`
  同时实现 `ITextPainter` 与 `ITextMeasurer`，布局与绘制共用同一个 shaping 缓存。
- **诊断即生命线**：未知标签/属性给最近似名建议；字面量按目标类型编译期转换，
  格式错=编译错误（EUI001–EUI015）；表达式经 `#line` 映射，Roslyn 报错直接落在 `.ui` 上。
- **无头测试**：不开窗口断言元素树、布局结果、signal 行为、事件冒泡、命中与焦点（设计文档 07 的落地）。
- **GPU 上屏（G2）**：`DisplayList` → `VelloSurfaceBackend` → wgpu(Dx12) surface → 窗口像素；
  `WindowHost` 把 Measure/Arrange → `ElementPainter` → present 串成帧循环，
  纯 Win32 宿主（无 WinForms/WPF），`WM_SIZE`/`WM_DPICHANGED` 触发 `surface.Configure`。
  踩过的坑与数据见 [11 G2 第一切片](./design/11-g2-surface-slice.md)。
- **damage v0（G2 第三切片）**：`Element.RenderVersion` 记录一切可能改变像素的变更，
  `DamageTracker` 对比上一帧 `(绝对矩形, 版本)` 快照产出合并后的 `FrameDamage`；
  无脏区的 tick 跳过 present（`WM_PAINT`/resize/DPI 变化强制整帧）。
  空闲 400 tick 仅上屏 1 帧；跟踪开销 ≈ 0.1 µs/元素。真正的局部重绘需要 retained layer，
  尚未实现。详见 [13 G2 第三切片](./design/13-g2-damage-v0.md)。

## 当前边界（已知限制）

- `@switch` 未支持；item 模板内引用 signal / 双向绑定未支持（EUI014）。
- 文本：无头测试默认用确定性 naive 测量；窗口样例接入 HarfBuzz。多行断行、bidi 混排、字体 fallback 未实现。
- 输入：无头注入与 Win32 鼠标/键盘已共用 `InputRouter` 并路由进元素树；字符输入（`WM_CHAR`）、
  IME、Shift+Tab 未接（暂无文本编辑控件）。
- 生成代码不入库（内存生成）；`Generated/` 落盘仅作检视。
- 渲染：`DisplayList` → Vello CPU（PNG/CI）与 GPU surface（真实窗口）两条路径已通；
  scene graph 与元素级 damage（空闲跳帧）已落地（G2 第二/三切片）；
  **retained layer、合成器、真正的局部重绘、样式系统仍未实现**；
  主题是 `ElementTheme` 临时数据，文本主/次色暂按字号阈值区分。
- 字体 fallback 未实现（缺字显示 `.notdef`）；CPU 光栅化每次约 60 ms 固定开销，仅用于 CI/离线导出。
- 3D：**不提供 3D 引擎**。规划为 WebGPU 视口控件——应用自带渲染器，框架负责布局、
  裁剪、DPI、输入与纹理合成（见 [05 布局、渲染与输入](./design/05-layout-rendering-input.md) 与 G7）。
- 测试机显示器为 29 Hz，`Fifo` 下 33.3ms/帧属正常；60fps 结论需在目标机用 `--no-vsync` 复测。

## 下一步（按开发计划）

G1 已出结论（Go），G2 前三切片已通过：`CounterApp` / `TaskList` 已在真实窗口可交互，
damage v0 让空闲帧不再提交 GPU。当前优先级是 **G2 剩余部分**：像素 golden、retained layer、
目标机 60fps 复测。
完整路线图见 [09 开发计划](./design/09-development-plan.md)，G1 数据见
[render-gate-2026-11](./design/reports/render-gate-2026-11.md)，G2 进度见
[11 G2 第一切片](./design/11-g2-surface-slice.md)、[12 G2 第二切片](./design/12-g2-interactive-slice.md)
与 [13 G2 第三切片](./design/13-g2-damage-v0.md)。

1. **G2 剩余**：像素 golden + retained layer / 局部重绘 + 目标机 60fps。
2. **G3 语义/编译器硬化**（与 G2 并行）：块布局透明化、单向绑定、列表作用域、语法级表达式重写、组件注册表。
3. **G4 样式/控件/虚拟化**：选择器编译、亮暗主题、基础控件集、`VirtualizingPanel`。
4. **G5 IDE P0–P1**：TextMate grammar + LSP server（表达式投影进 Roslyn）+ VS Code/Rider 壳。
