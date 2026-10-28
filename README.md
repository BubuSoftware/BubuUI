# EclipseUI

模板引擎优先的自绘 UI 框架。设计文档见 [design/](./design/README.md)，执行计划见 [开发计划](./design/09-development-plan.md) 与 [G1 渲染 Gate 执行清单](./design/10-g1-render-gate.md)。

> 状态：M1（托管部分 + HarfBuzz shaping spike）+ M2 编译器 MVP + M3 无头垂直切片 + M4 输入与焦点（无头形态）已落地，`dotnet test` 全绿。
> 渲染后端（Vello/FFI）、平台窗口宿主、样式主题、IDE 支持为后续里程碑；当前优先级是 **G1 渲染 Gate**（未开始）。

## 核心理念

**模板不是"字符串拼 UI"的外壳，而是编译单元**——编译期完成类型检查、字面量转换、
更新路径分析，运行时只执行生成好的指令：无运行时 diff，无虚拟 DOM，
绑定点由编译器生成定向更新函数，状态原语为显式 signals。

## 目录结构

```
design/                          设计文档（8 篇，定稿）
src/
  EclipseUI.Runtime/             运行时：signals、StyledProperty 属性引擎、元素树、
                                 measure/arrange 布局、块协调器、路由事件、输入/焦点、
                                 Component、无头宿主（hit test + 指针/键盘注入）
  EclipseUI.Text.Shaping/        HarfBuzz shaping（HarfBuzzSharp）：先 shaping 再测量，
                                 glyph run 供布局与未来 display list 共用（M1 spike）
  EclipseUI.Compiler/            .ui 编译器前端：Lexer / Parser / AST / 内置目录 / 诊断（netstandard2.0）
  EclipseUI.Compiler.Generator/  Roslyn 增量源生成器：code-behind 发现、@foreach item 类型推断、
                                 代码发射（#line 映射回 .ui，诊断与断点共用）
samples/CounterApp/              两个样例组件：Counter（计数器/条件/双向绑定）、
                                 TaskList（@foreach Key 对齐）；生成的 C# 落盘在 Generated/ 供检视
tests/EclipseUI.Headless.Tests/  53 个无头测试：属性引擎、signals、布局、块协调、输入/焦点、
                                 shaping、编译器、端到端（含泄漏回归）
```

## 快速开始

```bash
dotnet build EclipseUI.slnx
dotnet test  tests/EclipseUI.Headless.Tests/EclipseUI.Headless.Tests.csproj
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
- **输入与焦点（M4 无头形态）**：`HitTest` 树遍历（子元素局部坐标约定，同 WPF/Avalonia）、
  指针事件经命中目标冒泡 + **按下捕获语义**（拖出后释放不丢状态、不误触其他控件）、
  `FocusManager` Tab 环形遍历 + Got/LostFocus 路由事件、空格/回车激活（键盘可交互 demo）。
- **文本 shaping（M1 spike）**：`ITextShaper` 契约 + `HarfBuzzTextShaper`（HarfBuzzSharp），
  RTL 簇序、比例字宽、行盒高度均有真实字体（arial.ttf）测试；可整体替换
  `TextMeasurer.Current` 接入布局——布局测量与绘制共用同一 shaping 结果。
- **诊断即生命线**：未知标签/属性给最近似名建议；字面量按目标类型编译期转换，
  格式错=编译错误（EUI001–EUI015）；表达式经 `#line` 映射，Roslyn 报错直接落在 `.ui` 上。
- **无头测试**：不开窗口断言元素树、布局结果、signal 行为、事件冒泡、命中与焦点（设计文档 07 的落地）。

## 当前边界（已知限制）

- `@switch` 未支持；item 模板内引用 signal / 双向绑定未支持（EUI014）。
- 文本测量默认仍为确定性 naive 实现（测试数值稳定）；shaping 版可插入。
- 输入为无头注入形态：IME、真实平台指针/键盘桥接待平台宿主（M4 后半）。
- 生成代码不入库（内存生成）；`Generated/` 落盘仅作检视。
- 渲染管线、平台窗口宿主、样式主题、LSP 未开始——见设计文档里程碑。

## 下一步（按开发计划）

当前优先级是 **G1 渲染 Gate**：先用 2–3 周验证 Vello 最小像素闭环，再进入 2D 垂直切片。
完整路线图见 [09 开发计划](./design/09-development-plan.md)，当前任务看板见
[10 G1 执行清单](./design/10-g1-render-gate.md)。

1. **G1 渲染 Gate**：`DisplayList` 契约 + `vello_cpu` PNG + 文本 glyph + 功能/性能矩阵 + go/no-go。
2. **G2 2D 垂直切片**：scene graph + Win32 宿主 + 文本接入 + 像素测试。
3. **G3 语义/编译器硬化**（与 G2 并行）：块布局透明化、单向绑定、列表作用域、语法级表达式重写、组件注册表。
4. **G4 样式/控件/虚拟化**：选择器编译、亮暗主题、基础控件集、`VirtualizingPanel`。
5. **G5 IDE P0–P1**：TextMate grammar + LSP server（表达式投影进 Roslyn）+ VS Code/Rider 壳。
