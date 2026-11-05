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
> 控件事件自动失效；规范正本 [spec/01–03](./spec/README.md)（v4）。
> **S2 块 v4 切片已通过**：`@if`/`@foreach` 协调器 + `R{N}`、任意 `IEnumerable<T>` item 推断、
> Keyed 复用、`UiBinding` 原语与 `EUI033`。
> **S3 绑定切片已通过（控件侧）**：`@bind-P` 新语法 + 旧 `:bind`/`:value` 弃用告警、
> item 绑定与条目释放、`EUI037`/`EUI040`/`EUI043`。
> **C1 目录派生切片已通过**：运行时六个声明特性（`[UiTag]`/`[UiContent]`/`[UiIgnore]`/`[UiParameter]`/
> `[UiSlot]` + `UiContentKind`）、`Compiler.Metadata` Roslyn 符号扫描器；派生目录 ⊇ 手写目录
> （模板属性项 6→17，差集逐条评审见 [14 §14.12.1](./design/14-control-template-contract.md)）。
> **C2 模板表面切片已通过（2026-11-02）**：分析器/生成器切到派生目录、手写目录删除（`CreateDefault()` 不存在）；
> `InputEvents` 7 个事件迁入 `Element`（模板事件 1 → 9 个事件名，任何标签可写输入/焦点处理器）、
> 补 `CheckBox.CheckedChanged`；`Metadata` 源码链接进生成器程序集；golden 快照落成
> `TestData/BuiltInSurface.golden.txt`；`@foreach` 生成代码全 `global::` 限定（D22 修复）。
> 落地记录 [14 §14.12.2](./design/14-control-template-contract.md)，台账 D22/D65/D69/D72 关闭。
> 257 个测试全绿（Headless 103 / Compiler 93 / Rendering 61）。
> **C3 转换器与使用点诊断切片已通过（2026-11-03）**：内置转换器补齐到 spec/01 §5.2 全表
> （float/decimal/long/short/byte/char/枚举/TimeSpan，编译期换算、含量程/转义校验）；
> 使用点诊断 `EUI023`（无字面量转换器，提示 `{expression}`）、`EUI025`（纯普值重复属性；
> 同名含绑定仍走 `EUI033`）、`EUI031`（`Class`/`Style`/附加属性/`<Slot>`/`<ContentSlot>` 保留语法）落地。
> 落地记录 [14 §14.12.3](./design/14-control-template-contract.md)，台账 D2/D3/D6/D14/D19 关闭。
> 281 个测试全绿（Headless 127 / Compiler 93 / Rendering 61）。
> **C4 组件标签切片已通过（2026-11-05）**：自定义组件可写入 `.ui`——`[UiParameter]` 参数表（含
> `Required`/委托参数即事件/`{P}Changed` 约定链接）、`@bind-P` 组件双向、默认内容（`Element?` 单根 /
> `IReadOnlyList<Element>` 多根）与冻结生命周期（登记 → 参数推送 → 内容 → `Mount()`）落地；
> 诊断 EUI021/027/028/040 覆盖组件声明与使用点。落地记录 [14 §14.12.4](./design/14-control-template-contract.md)，
> 台账 D17/D52/D54/D56 关闭（新增 D73 残余风险）。
> 320 个测试全绿（Headless 145 / Compiler 114 / Rendering 61）。
> **C5 Ref 与使用点诊断切片已通过（2026-11-05）**：`Ref={field}` 落地——元素/组件实例在 Build 时
> 赋给 code-behind 字段（不进 `Render()`、不重算），诊断分层：非简单标识符由分析器报 `EUI029`（语法级），
> 字段缺失/类型不符/只读/静态由生成器探针报 `EUI029`（语义级，引用 C# 诊断 CS0103/CS0029/CS0191）；
> `EUI019`/`EUI020`/`EUI021` **使用点**口径落地——引用程序集控件的元数据问题在使用点暴露（消息带完整类型名），
> 源项目控件仍落 `EUI002`/`EUI006`（声明处归 `EUI026`）。落地记录 [14 §14.12.5](./design/14-control-template-contract.md)，
> 台账新增 D74（① 使用点 ✅ / ② `EUI026` 声明处出口开放）。
> 347 个测试全绿（Headless 160 / Compiler 126 / Rendering 61）。
> **Tokenizer 切片已通过（2026-11-05）**：`.ui` 编译器内两处正则形态判定换 Roslyn 解析/深度感知扫描——
> 控件事件的委托类型（`Action` vs `Action<RoutedEventArgs>`）按表达式语法形态判定（括号/引号内的
> `=>`、嵌套 lambda、匿名方法不再误判），`@foreach` 头按 spec/01 §4.11 冻结形式扫描（集合内带引号的
> `" in "`、括号内 `in` 修饰符不再撕裂表达式；类型化条目/解构给出针对性 `EUI100` 消息）。
> 358 个测试全绿（Headless 167 / Compiler 130 / Rendering 61）。
> 具名插槽（C6）、EUI024 签名匹配、样式主题与 IDE 支持为后续工作。

## 核心理念

**模板不是"字符串拼 UI"的外壳，而是编译单元**——编译期完成类型检查、字面量转换、
更新路径分析，运行时只执行生成好的指令：无运行时 diff，无虚拟 DOM，
绑定点由编译器生成定向更新函数；状态是普通 CLR 成员，失效驱动定向重算。

## 目录结构

```
design/                          设计文档（架构 8 篇 + 计划、切片报告、控件契约）
src/
  EclipseUI.Runtime/             运行时：失效队列与所有权、StyledProperty 属性引擎、
                                 元素树、measure/arrange 布局、块协调器（v4：`Refresh`/`BlockScope`）、
                                 路由事件、InputRouter 输入/焦点、Panel.Padding/ClipToBounds、无头宿主、
                                 控件契约声明特性（`UiAttributes.cs`：`[UiTag]`/`[UiContent]`/`[UiIgnore]`/
                                 `[UiParameter]`/`[UiSlot]` + `UiContentKind`）
  EclipseUI.Text.Shaping/        HarfBuzz shaping（HarfBuzzSharp）：先 shaping 再测量，
                                 HarfBuzzTextPainter 让布局与绘制共用同一 shaping 缓存
  EclipseUI.Compiler/            .ui 编译器前端：Lexer / Parser / AST / 字面量转换与建议 / 诊断（netstandard2.0）
  EclipseUI.Compiler.Metadata/   控件契约目录（C1–C2）：声明式元数据模型 + Roslyn 符号扫描器，
                                 从符号派生模板目录（标签/属性/事件/内容/参数/插槽），不执行程序集
                                 （netstandard2.0；C2 起由分析器与生成器同源消费，源码内嵌进生成器）
  EclipseUI.Compiler.Generator/  Roslyn 增量源生成器：code-behind 发现、@foreach item 类型推断、
                                 @bind 源可写性校验、代码发射（U/R 定向更新、#line 映射回 .ui）
samples/CounterApp/              三个样例组件：Counter（计数器/条件/双向绑定）、
                                 TaskList（@foreach Key 对齐）、S1Probe（v4 失效切片）；
                                 生成的 C# 落盘在 Generated/ 供检视
samples/GalleryApp/              G2 验收样例：Counter + TaskList 真窗口交互、--selfcheck、
                                 CPU/窗口截图与帧时间统计
tests/EclipseUI.Headless.Tests/  127 个无头测试：属性引擎、布局（含 padding）、块协调、
                                 输入/焦点路由、shaping、编译器、字面量转换与使用点诊断（C3）、
                                 失效队列/所有权、端到端（含泄漏回归）
tests/EclipseUI.Compiler.Tests/  93 个测试：39 个生成器契约测试（确定性 hint、U/R 形态与 Render、
                                 块/绑定生成、@bind 源与目标诊断、集合与嵌套 item 类型推断、#line 映射）
                                 + 42 个目录扫描测试（发现规则、属性/事件/内容/参数派生、
                                 与 `StyledPropertyRegistry` 交叉一致、失效级别守卫、跨程序集标签冲突、表面快照与差集）
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

# G2 验收：Counter + TaskList 真窗口交互（事件 → 输入路由 → 失效重算 → 重绘）
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
    <CheckBox @bind-Checked={showButton}>Button visible</CheckBox>
    @if (showButton) {
        <Button OnClick={() => count++}>+1</Button>
    }
</StackPanel>
```

code-behind（同名 `.cs`，普通 CLR 成员，无继承样板）：

```csharp
public partial class Counter
{
    public int count;
    public bool showButton;
}
```

`.ui` 作为 AdditionalFile 进入编译，由增量源生成器生成 `Build()`：
结构创建指令 + 根级 `U{N}`/块内 `R{N}` 定向更新函数 + 块协调器接线。
表达式就是 C# 表达式（不重写、不补 `.Value`）；控件事件处理器末尾自动 `Invalidate()`。

## 已实现的关键语义

- **属性引擎**：`StyledProperty<T>`（默认值 / 继承 / 失效分级 Measure–Arrange–Render），
  setter 值短路无装箱；变更只标脏，布局在 `HeadlessRoot.UpdateLayout()` 帧内增量执行。
- **文本一等公民**：`<TextBlock>You pressed {count} times</TextBlock>` 编译为多个
  TextRun（经 `ITextContentHost`），各自独立定向更新；静态段不建 run 变量。
- **块协调器（v4）**：`@if` 的 setter 只保存条件与模板，`Refresh()` 首次挂载；条件翻转卸载并
  逆序释放旧 `BlockScope`，条件不变只重跑渲染动作。`@foreach` 每次重新枚举任意
  `IEnumerable<T>`（数组/`List<T>`/字典/LINQ），null 视为空；`Key` 相同 **且** item 身份相同
  才复用，不同 item 重建；条目移除/块 `Dispose` 释放作用域资源，根序列不变时不改 `Children`。
- **资源放置随上下文**：绑定与嵌套块登记到正确作用域——根级 `Track`、条件内容
  `scope.AddDisposable`、item 模板条目作用域；从机制上杜绝 toggle/增删条目时的泄漏。
- **双向绑定**：`@bind-Checked={showButton}` 生成 `UiBinding.TwoWay`；构造时源→目标，
  目标变化回写源并 `Invalidate`，相等短路防回环；源必须是可写左值，非法源/非法目标分别报
  `EUI037`/`EUI040`（编译期，含值类型 item 成员与属性返回的 struct 中间段）；旧语法
  `:bind`/`:value` 仍可用但报 `EUI043` 弃用告警；组件级 `@bind-P` 已随 C4 落地
  （`{P}Changed` 回调 + 父推送 `U{N}`/`R{N}`）。
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
  格式错=编译错误（EUI001–EUI015），`@foreach` 集合不可枚举报 EUI007，同一目标重复写入报 EUI033，
  绑定源/目标非法报 EUI037/EUI040，旧 `:bind`/`:value` 报 EUI043 弃用告警；
  表达式经 `#line` 映射，Roslyn 报错直接落在 `.ui` 上。
- **无头测试**：不开窗口断言元素树、布局结果、失效重算、块生命周期、事件冒泡、命中与焦点（设计文档 07 的落地）。
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

- ~~`@switch` 未支持；组件标签、组件参数与组件级 `@bind`（`{P}Changed` 父推送/子回调）未实现~~
  **组件标签已随 C4 落地（2026-11-05）**：参数（含 `Required`/委托事件/默认内容）与组件 `@bind` 可用；
  **`Ref={field}` 已随 C5 落地（2026-11-05）**：元素/组件实例赋给 code-behind 字段，
  `EUI029` 分层（分析器语法级 + 生成器探针语义级）；`EUI019`/`EUI020`/`EUI021` 使用点诊断
  仅对引用程序集控件生效（源项目控件落 `EUI002`/`EUI006`，声明处 `EUI026` 暂无编译期出口，D74② 开放）；
  具名插槽（`<Slot>`/`<ContentSlot>`）留 C6；`@switch` 仍未支持。
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
damage v0 让空闲帧不再提交 GPU。G3 的 C1/C2 目录派生切片、C3 转换器/使用点诊断切片、
**C4 组件标签切片与 C5 `Ref`/使用点诊断切片已全部通过**（手写目录删除，模板表面 17 属性项 /
9 个事件名；转换器补齐到 spec/01 §5.2 全表；自定义组件可写入 `.ui`；`Ref={field}` 与
EUI019–021/EUI029 使用点收敛，落地记录见
[14 §14.12.2–§14.12.5](./design/14-control-template-contract.md)）与 **tokenizer 切片**（事件委托形态与
`@foreach` 头的正则判定换 Roslyn 解析/深度感知扫描）。当前优先级是 **G2 剩余部分**：
像素 golden、retained layer、目标机 60fps 复测；G3 并行推进 C6（具名插槽）与
EUI024 签名匹配。
完整路线图见 [09 开发计划](./design/09-development-plan.md)，G1 数据见
[render-gate-2026-11](./design/reports/render-gate-2026-11.md)，G2 进度见
[11 G2 第一切片](./design/11-g2-surface-slice.md)、[12 G2 第二切片](./design/12-g2-interactive-slice.md)
与 [13 G2 第三切片](./design/13-g2-damage-v0.md)。

1. **G2 剩余**：像素 golden + retained layer / 局部重绘 + 目标机 60fps。
2. **G3 语义/编译器硬化**（与 G2 并行）：块布局透明化、~~表达式形态判定的正则替代（tokenizer）~~ ✅ 已落地（2026-11-05）、按文件增量与 `#line` 列映射、
   ~~组件标签与组件级绑定（C4）~~ ✅ C4 已落地（2026-11-05）、~~`Ref` 与剩余使用点诊断收敛（C5）~~ ✅ C5 已落地（2026-11-05）。
3. **G4 样式/控件/虚拟化**：选择器编译、亮暗主题、基础控件集、`VirtualizingPanel`。
4. **G5 IDE P0–P1**：TextMate grammar + LSP server（表达式投影进 Roslyn）+ VS Code/Rider 壳。
