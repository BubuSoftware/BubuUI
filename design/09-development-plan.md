# 9. 开发计划（2026-10 基线）

> 本文是 `design/01`–`08` 的执行计划，不改变已定架构决策，只把“做什么、先做什么、何时算完成”落到可检查的里程碑。
>
> **排期假设**：1–2 名全职工程师、Windows 优先、2 周一个迭代。若增至 3–4 人且能并行拆分，整体时间可压缩约 30–40%；若只有 1 人且同时承担 IDE/平台适配，按区间上限估算。

## 9.1 当前基线

**已具备**

- 设计文档 8 篇（痛点、架构、模板引擎、运行时、布局/渲染/输入、IDE、工具链、里程碑）。
- `.ui` 前端：Lexer / Parser / AST / 内置目录 / 诊断（EUI001–EUI015）。
- Roslyn 增量源生成器：code-behind 发现、signal 发现、`@foreach` item 类型推断、代码发射、`#line` 映射。
- 运行时：`Signal<T>`、`StyledProperty<T>`、元素树、measure/arrange、路由事件、`@if`/`@foreach` 协调器、`Component`、无头宿主。
- 输入/焦点：HitTest、指针冒泡、按下捕获、Tab 遍历、空格/回车激活；`InputRouter` 无头/窗口共用。
- 文本：HarfBuzz shaping（真实字体测试），`HarfBuzzTextPainter` 让布局与绘制共用 shaping 缓存。
- 渲染：`DisplayList` 契约、Vello CPU/GPU 后端、`ElementPainter` 场景图遍历、Win32 宿主 + wgpu 上屏。
- 测试：113 个测试全绿（63 无头 + 50 渲染）。

**尚不具备**

- damage/layer、合成器、像素 golden 自动化。
- 样式/主题、虚拟化、基础控件库。
- 文本编辑（字符输入/IME）、多行断行、bidi、字体 fallback。
- LSP / TextMate / VS Code / Rider 插件。
- 热重载、DevTools、调试映射的 IDE 接入。
- 3D 视口与 wgpu 直通。
- CI、LICENSE、NuGet 打包、benchmark、AOT 冒烟。

## 9.2 总体进度评估

> 百分比是主观估算，用于排期沟通，不作为绩效指标。

| 视角 | 完成度 | 说明 |
|---|---:|---|
| 托管核心（编译器前端 + 生成器 + 运行时 + 无头测试） | **65–70%** | 主链路可运行，但有语义缺口（单向绑定、块布局、列表作用域）和工程缺口（增量、诊断覆盖、性能）。 |
| 0.1 alpha（可交付的 2D 桌面框架） | **40–45%** | 渲染与 Win32 宿主已打通到窗口像素，元素树→display list→上屏与输入路由可用；仍缺样式/主题、基础控件集、damage/layer。 |
| 完整设计愿景（含 IDE、3D、多平台、DevTools、1.0 硬化） | **10–15%** | IDE、3D、平台扩展、无障碍与稳定性尚未开始。 |

### 分领域状态

| 领域 | 状态 | 到 0.1 还差什么 |
|---|---|---|
| 模板前端 | 🟢 可用 MVP | `@switch`、附加属性、泛型标签、具名插槽、trivia/pretty-printer、Roslyn 语义分析 |
| 源生成器 | 🟢 可用 MVP | 单向绑定、语法级表达式重写、按文件增量、组件注册表、`#line` 列映射 |
| 运行时/属性/信号 | 🟢 可用基础 | 属性存储去装箱、生命周期 Unmount、样式优先级、性能基准 |
| 布局 | 🟡 部分 | Grid/Wrap/Scroll、块布局透明化、约束与裁剪、虚拟化 |
| 文本 | 🟡 单行 shaping 已接入 | 断行、bidi、fallback、emoji、glyph 缓存、display list 集成 |
| 输入/焦点 | 🟡 元素路由已通 | 字符输入/IME、滚轮/触摸、Shift+Tab、无障碍 |
| 渲染/合成器 | 🟡 scene graph + 上屏可用 | damage/layer、合成器、帧预算 |
| 样式/主题 | 🔴 未开始 | 选择器编译、资源查找、亮暗主题、控件模板 |
| 控件库 | 🟡 极简 | TextBlock/Button/CheckBox/StackPanel/ListBoxItem 之外的基础控件 |
| IDE | 🔴 未开始 | TextMate、LSP server、VS Code/Rider 壳 |
| 工具链 | 🔴 基本未开始 | 热重载、断点映射、DevTools、无头测试 SDK |
| 平台 | 🟡 Win32 宿主已可用 | macOS/Linux、多窗口、设备丢失恢复 |
| 3D | 🔴 未开始 | 视口、texture 混排、输入拾取、wgpu 直通 |
| 工程化 | 🟡 早期 | CI、LICENSE、打包、benchmark、AOT、发布流程 |

## 9.3 范围定义

### 0.1 alpha（第一个外部可用版本）

必须包含：

- Windows 桌面窗口，能显示真实像素；
- `.ui` 模板 + code-behind + signals 的完整 2D 链路；
- `@if` / `@foreach`（含 Key 复用）语义正确；
- StackPanel/TextBlock/Button/CheckBox/ListBox/TextBox 等最小控件集；
- 基础样式：颜色、字号、间距、圆角、简单主题切换；
- 无头测试 + 像素探针；
- 生成器诊断（未知标签/属性/字面量格式/表达式错误）在 IDE 中可见。

不包含：LSP 补全、热重载、3D、多平台、完整无障碍、DevTools。

### 1.0

在 0.1 基础上增加：

- VS Code + Rider 的补全/悬停/跳转/重命名；
- 热重载与 `#line` 调试映射；
- 3D 视口与 2D/3D 同帧混排；
- macOS/Linux 平台；
- IME、无障碍、DPI、本地化/RTL；
- API 稳定性承诺、性能预算、发布与兼容策略。

## 9.4 版本路线图

| 版本 | 目标窗口 | 内容 | 出口标准 |
|---|---|---|---|
| **G1 渲染 gate** | 2026-11（2–3 周） | Vello 最小像素闭环 + 功能/性能数据 | ✅ Go（有条件） |
| **0.1 alpha** | 2027 Q2–Q3 | 2D 渲染、Win32 宿主、样式、基础控件、虚拟化起步 | `CounterApp`/`TaskList` 真窗口可交互；像素测试稳定 |
| **0.2 alpha** | 2027 Q4 | IDE 诊断 + 补全（VS Code 先行）、热重载原型 | VS Code 中 `.ui` 可补全/报错；改 UI 不丢状态 |
| **0.5 beta** | 2028 Q1–Q2 | 3D 视口、macOS/Linux、DevTools、无障碍起步 | 两个平台跑通；3D 与 2D 同帧 |
| **1.0** | 2028 Q3–Q4 | API 冻结、IME、性能/稳定性、文档与发布 | 外部项目可无痛升级；性能预算达标 |

> 这是 1–2 人团队的“现实区间”。如果只有 1 人且 IDE/平台同时推进，按每个阶段上限再加 20–30%。

## 9.5 阶段计划

### G1：渲染 Gate（2–3 周，最高优先级）

> **状态：已完成（2026-10-28），结论 Go（有条件）。** 结果见 [render-gate-2026-11.md](./reports/render-gate-2026-11.md)。
> 关键数据：GPU 200 元素 1280×800 = 6.36 ms/帧（含 readback）；功能矩阵 8/9；CPU 路径有约 60 ms 固定开销，仅用于 CI。

**目标**：回答“Vello 能不能作为唯一渲染后端”。

**范围**

- 定义最小 `DisplayList` / `DrawCommand` / `IRenderBackend` 契约。
- 用 `vello_cpu` 在无窗口环境渲染 PNG。
- 接入 HarfBuzz glyph run，验证文本渲染。
- 逐项验证 dash、阴影、渐变、clip/blend、图像缩放、彩色 emoji/fallback。
- 测 layout → display list → FFI/序列化 → 光栅化的分段耗时。
- 最小 3D texture → Vello image 混排（stretch）。

**交付物**

- `src/EclipseUI.Rendering` 契约层。
- `src/EclipseUI.Rendering.Vello` CPU 后端（GPU 视时间）。
- `samples/RenderSpike` 一帧输出 PNG。
- `tests/EclipseUI.Rendering.Tests` 像素探针 + 确定性测试。
- `design/reports/render-gate-2026-11.md`：功能矩阵、性能数据、go/no-go。

**出口标准**

- 功能矩阵 ≥ 7/9 通过，失败项有明确降级方案。
- 文本渲染宽度与布局测量误差 ≤ 1px/100px。
- CPU 输出确定性可复现。
- 单帧耗时基线可比较；若 CPU 预处理 > 8ms（1280×800、约 200 元素）标红。
- 给出 Go / Conditional Go / No-Go 结论；No-Go 必须触发 ADR 复议。

### G2：2D 渲染垂直切片（8–10 周）

**目标**：`.ui` → 元素树 → 布局 → scene graph → display list → 窗口像素。

**进度**（2026-10-28）：前两切片已完成——Win32 宿主 + wgpu surface 上屏 + resize 冒烟
（[11 G2 第一切片](./11-g2-surface-slice.md)）；元素树→display list、输入路由、文本 shaping 接入、
Counter/TaskList 真窗口可交互（[12 G2 第二切片](./12-g2-interactive-slice.md)）。
剩余：damage/layer、像素 golden、目标机 60fps 复测。

| 子项 | 状态 |
|---|---|
| Vello GPU 后端与 surface/swapchain 集成 | ✅ Dx12 上屏，截图与 CPU 输出一致 |
| Win32 窗口宿主（窗口、resize、DPI、鼠标/键盘桥接） | ✅ 事件 → InputRouter → 元素树 |
| Scene graph / paint 遍历 / display list 构建 | ✅ `ElementPainter` 全量遍历 |
| 文本整段 shaping 接入布局与绘制 | ✅ `ITextPainter` + `ITextMeasurer` 共用 shaping 缓存 |
| damage/layer v0 | ⏳ 全量重绘 |
| 像素探针与 golden 测试 | ⏳ 截图工具已就绪（`tools/capture-window.ps1`） |

**范围**

- Scene graph / paint 遍历 / display list 构建。
- Vello GPU 后端与 surface/swapchain 集成。
- Win32 窗口宿主：窗口、resize、DPI、鼠标/键盘桥接。
- 文本整段 shaping 接入布局与绘制。
- damage/layer v0：先全量重绘，再按脏区域优化。
- 像素探针与 golden 测试。

**出口标准**

- `CounterApp`、`TaskList` 在真实窗口可交互。—— ✅ GalleryApp 自检 6 步全过（[12](./12-g2-interactive-slice.md)）。
- resize/DPI 变化不崩、不闪。—— ✅ 860×600 ↔ 1000×760 自检 + 第一切片 4 档尺寸冒烟。
- 1280×800 简单场景稳定 60fps（目标机实测）。—— ⏳ 本机稳态帧成本 4.49 ms（p95 5.95），受 29 Hz 虚拟显示限制待目标机复测。
- `dotnet test` 含渲染测试且跨机器稳定（容差/探针策略）。—— ✅ 113 个测试（63 无头 + 50 渲染），GPU 用例无设备自动跳过。

**已知约束**（第一切片测得）：测试机显示器 29 Hz，`Fifo` 下 33.3ms/帧是正确行为；
60fps 结论必须在目标机（真实显示器）上用 `--no-vsync` 的 `Immediate` 模式复测。

### G3：语义与编译器硬化（6–8 周，可与 G2 并行）

**范围**

- 控制流块布局透明化（`@if`/`@foreach` 不再作为 StackPanel 参与布局）。
- 单向绑定 `Prop={expr}`。
- `ForEachBlock.Dispose` 泄漏修复；item 模板内嵌套块的 Key 复用更新。
- 语法级表达式重写（Roslyn SyntaxRewriter，替代正则）。
- 组件注册表：从 Compilation 收集元素/属性/事件/内容模型。
- 按文件增量生成；`#line` 列映射；诊断覆盖 EUI005/008/009/011/012/014/015。
- 属性存储去装箱、Signal 写入分配优化、benchmark 基线。

**出口标准**

- 列表间距/条件布局有回归测试。
- 组件注册表能发现样例项目与一个测试用第三方程序集。
- 生成器在大型项目上增量时间可测，避免全量重跑。

### G4：样式、控件与虚拟化（8–10 周）

**范围**

- 选择器编译（AOT 友好）、样式优先级、资源查找、亮暗主题。
- 控件集：ListBox、TextBox、ScrollViewer、Grid、WrapPanel、Slider 等。
- `VirtualizingPanel` 与 item 模板的容器复用。
- 键盘/焦点/IME 接口预留。

**出口标准**

- 主题切换 demo；样式变更只重排/重绘受影响子树。
- 1000+ 条目列表滚动流畅；容器复用有测试。

### G5：IDE P0–P1（12–16 周）

**范围**

- TextMate grammar + snippets。
- LSP server：诊断双通道（生成器 + 实时）、补全、悬停、跳转。
- 表达式投影：`{}` → 合成 C# → Roslyn → span 映射回 `.ui`。
- VS Code 扩展先行；Rider 通过 lsp4ij 接入。
- 重命名跨文件联动（P2 起步）。

**出口标准**

- VS Code/Rider 同日同版发布。
- 补全按目标类型过滤，附加属性按 owner 过滤，事件显示委托签名。
- 诊断位置与生成器一致。

### G6：工具链（8–10 周）

**范围**

- 热重载：模板重编译 + Key 对齐 + 状态保留。
- `#line` 断点映射接入 DAP。
- DevTools：元素树检查器、signals 依赖图、帧剖析。
- 无头测试 SDK 与 CI 模板。

**出口标准**

- 改 `.ui` 不丢 code-behind 状态。
- 断点可直接下在 `{}` 表达式上。
- CI 可跑无头 + 像素测试。

### G7：3D 与平台扩展（10–14 周）

**范围**

- 3D 视口组件 + offscreen texture → Vello image 混排。
- wgpu 直通协议、输入转发、CPU/GPU picking 两档。
- macOS（Metal）/Linux（Vulkan）宿主。
- 跨端渲染正确性与性能回归。

**出口标准**

- 2D/3D 同帧、同一 device/queue 单次 submit。
- 至少两个平台跑通 0.1 场景。

### G8：1.0 硬化（10–12 周）

**范围**

- 无障碍（UIA/屏幕阅读器）、IME 完整链路、DPI/多显示器。
- 本地化/RTL、剪贴板/拖放、多窗口/对话框。
- 性能预算、内存/启动、AOT/裁剪、API 稳定性与兼容策略。
- 文档、示例、迁移指南、发布流程。

**出口标准**

- 性能预算达标；无已知 P0/P1 缺陷。
- 外部项目可在不修改模板语义的情况下升级。

## 9.6 关键路径与并行

```
G1 渲染 gate
  └─ G2 2D 垂直切片 ── G4 样式/控件/虚拟化 ── G8 1.0 硬化
        │                      │
        │                      └─ G5 IDE P0–P1 ── G6 工具链
        │
        └─ G7 3D + 平台扩展

G3 语义/编译器硬化：从 G1 结束后并行，最迟在 G4 前完成关键项
```

- **关键路径**：G1 → G2 → G4 → G8。
- **G3 必须并行**：它修的是会污染 demo 的语义问题，不能等渲染做完再补。
- **G5 依赖 G3 的组件注册表**：没有符号元数据，LSP 补全只能靠硬编码。
- **G7 依赖 G2 的帧循环/display list**：不要在渲染契约稳定前做 3D。

## 9.7 下一任务详细计划：G1 渲染 Gate

> 可执行工作单（任务看板、验收勾选、指标表）见 [10 G1 渲染 Gate 执行清单](./10-g1-render-gate.md)。

### 9.7.1 目标与非目标

**目标**

1. 证明或证伪 Vello 路线：能不能画对、画快、跑在 Windows。
2. 建立最小渲染契约，后续 scene graph/合成器都围绕它演进。
3. 得到可复现的像素测试与性能基线。
4. 形成 go/no-go 决策报告。

**非目标（本阶段不做）**

- 完整 scene graph、动画、damage/layer 优化；
- 3D 引擎、材质/场景图；
- 样式系统、控件库、LSP；
- 最终 display list 二进制格式（先验证绘制正确性，再谈跨语言编码）；
- 热重载、DevTools。

### 9.7.2 交付物清单

| ID | 交付物 | 位置 |
|---|---|---|
| D1 | 渲染契约 | `src/EclipseUI.Rendering/` |
| D2 | Vello CPU 后端 | `src/EclipseUI.Rendering.Vello/` |
| D3 | 一帧 PNG CLI | `samples/RenderSpike/` |
| D4 | 像素/确定性/性能测试 | `tests/EclipseUI.Rendering.Tests/` |
| D5 | 功能矩阵 + 性能数据 + 结论 | `design/reports/render-gate-2026-11.md` |
| D6 | 基线提交 + spike 分支 | git |

### 9.7.3 工作分解（WBS）

| ID | 任务 | 工期 | 依赖 | 产出/验收 |
|---|---|---|---:|---|
| G1-01 | 提交当前基线，创建 `spike/vello-render` 分支 | 0.5d | — | 53 测试仍绿；分支可回滚 |
| G1-02 | 建 `spikes/vello` 控制台，引用 VelloSharp 或最小 C ABI shim；确认原生库加载 | 1d | G1-01 | 256×256 红色矩形 PNG |
| G1-03 | 定义 `DisplayList`/`DrawCommand`/`IRenderBackend`/`Color` | 0.5d | — | 契约通过评审，无 UI 依赖 |
| G1-04 | CPU 后端：clear、fill rect、clip、transform | 1d | G1-02/03 | 像素探针通过 |
| G1-05 | 圆角矩形、描边、虚线、椭圆/路径 | 1d | G1-04 | 图元 PNG |
| G1-06 | 接入 `HarfBuzzTextShaper`：glyph run → 绘制 | 1.5d | G1-04 | 文本 PNG；宽度与布局一致 |
| G1-07 | 功能矩阵：阴影、线性/径向/锥形渐变、blend、图像缩放、emoji/fallback | 1.5d | G1-05/06 | 每项 pass/fail + 截图 |
| G1-08 | 性能 harness：100/1k/10k 命令、200 元素场景 | 0.5d | G1-04 | 分段耗时表 |
| G1-09 | 像素探针 + 确定性测试 + CI 友好输出 | 1d | G1-04/06 | `dotnet test` 稳定 |
| G1-10 | GPU 后端 / surface 呈现（时间不够则 CPU readback） | 1.5d | G1-02/04 | 窗口/离屏呈现一帧 |
| G1-11 | 3D texture → Vello image 最小混排（stretch） | 1d | G1-10 | 同帧 PNG |
| G1-12 | 写报告、go/no-go、后续 backlog | 1d | 全部 | 决策文档 |

**关键路径**：G1-01 → G1-02 → G1-04 → G1-06 → G1-09 → G1-12。  
**可并行**：G1-03 与 G1-02；G1-07 与 G1-08；G1-11 在 G1-10 之后。

### 9.7.4 契约草案

```csharp
namespace EclipseUI.Rendering;

public readonly record struct Color(byte R, byte G, byte B, byte A);

public abstract record DrawCommand;
public sealed record FillRect(Rect Rect, Color Color) : DrawCommand;
public sealed record FillRoundedRect(Rect Rect, double Radius, Color Color) : DrawCommand;
public sealed record StrokeRect(Rect Rect, Color Color, double Width) : DrawCommand;
public sealed record FillPath(PathGeometry Path, Color Color) : DrawCommand;
public sealed record DrawGlyphs(ShapedBuffer Glyphs, Point Origin, Color Color) : DrawCommand;
public sealed record DrawImage(ImageHandle Image, Rect Dest) : DrawCommand;
public sealed record PushClip(Rect Rect) : DrawCommand;
public sealed record PopClip : DrawCommand;
public sealed record PushLayer(double Opacity, Transform Transform) : DrawCommand;
public sealed record PopLayer : DrawCommand;

public sealed class DisplayList
{
    public required Size Size { get; init; }
    public List<DrawCommand> Commands { get; } = new();
}

public interface IRenderBackend : IDisposable
{
    void Render(DisplayList list, RenderTarget target);
}
```

约束：

- 契约层只依赖 `EclipseUI.Runtime` 的几何/文本类型，不依赖 VelloSharp。
- 先支持 `RenderTarget.CpuBuffer`（RGBA），GPU surface 作为第二种 target 后加。
- glyph run 直接复用 `ShapedBuffer`，避免 spike 阶段再造文本数据结构。

### 9.7.5 测试与度量

**像素探针（必须）**

- 中心点颜色精确匹配；
- 圆角外/内探针；
- clip 外透明、clip 内正确；
- 文本区域非空 + 包围盒宽度与 `TextMeasurer` 误差 ≤ 1px/100px；
- 同一输入两次 CPU 渲染哈希一致。

**性能表（必须记录，不强制失败）**

| 场景 | 命令数/glyph | layout | display list build | 序列化/FFI | 光栅化 | 总帧 |
|---|---:|---:|---:|---:|---:|---:|
| 空场景 | 0 | | | | | |
| 200 元素 | ~200 | | | | | |
| 1k 文本 | ~1k glyph | | | | | |
| 10k 命令 | 10k | | | | | |

**功能矩阵（必须）**

| 项 | 结果 | 备注 |
|---|---|---|
| dash 描边 | pass/fail | |
| 阴影/模糊 | pass/fail | |
| 线性/径向/锥形渐变 | pass/fail | |
| clip 路径 + 复杂 blend | pass/fail | |
| 图像缩放质量 | pass/fail | |
| 彩色 emoji | pass/fail | |
| 字体 fallback | pass/fail | |
| 2D/3D 同帧混排 | pass/fail | |
| FFI 帧开销 | 数值 | |

### 9.7.6 验收标准

- **AC-1**：`samples/RenderSpike` 一条命令输出 `out/frame.png`。
- **AC-2**：`dotnet test` 全绿，含 ≥ 5 个渲染测试。
- **AC-3**：功能矩阵 ≥ 7/9 通过；失败项写明降级方案。
- **AC-4**：CPU 渲染确定性可复现；跨机器用探针/容差而非全图哈希。
- **AC-5**：性能基线完整；若 1280×800/200 元素 CPU 预处理 > 8ms，标红并分析。
- **AC-6**：报告给出 Go / Conditional Go / No-Go 及依据。
- **AC-7**：spike 代码不污染生产 API；契约层可保留，后端可替换。

### 9.7.7 风险与应对

| 风险 | 概率 | 影响 | 应对 |
|---|---|---|---|
| VelloSharp 原生库/API 不可用 | 中 | 高 | 1 天时间盒；失败则 vendor `vello_ffi` 或自建最小 C shim，再跑同一矩阵 |
| vello_cpu 输出与 GPU 不一致 | 中 | 中 | CPU 仅用于 CI/兜底；像素容差 + 探针；GPU 单独基线 |
| 文本 glyph 渲染质量差 | 中 | 高 | 对比 HarfBuzz 度量；必要时先用 path outline 渲染，glyph 缓存后补 |
| 阴影/滤镜/渐变能力缺口 | 高 | 中 | 框架层降级（近似/分层实现）或明确不支持；记录到 ADR |
| 没有可用的 GPU surface 集成 | 中 | 中 | spike 用 CPU readback 呈现；GPU 推迟到 G2 |
| 范围蔓延到 scene graph/合成器 | 高 | 中 | 冻结非目标；所有优化进 backlog，不在 G1 做 |
| 像素测试跨机器不稳定 | 高 | 低 | 探针/容差 + CPU 确定性哈希；不提交全图 golden |

### 9.7.8 Go / No-Go 判定

- **Go**：≥ 7/9 功能通过；文本质量可接受；目标场景总帧 ≤ 16.7ms（或 CPU 预处理 ≤ 8ms）。→ 进入 G2。
- **Conditional Go**：核心通过但 FFI/绑定有风险。→ 先 vendor/shim，1 周复测，再进 G2。
- **No-Go**：文本/clip/blend 质量不可接受，或优化后帧时间仍 > 33ms。→ 触发 ADR：评估 SkiaSharp 兜底或明确功能降级，暂停 G2。

### 9.7.9 G1 完成后立即做什么

**若 Go**

1. 冻结 `DisplayList` v0 契约，进入 G2。
2. 先做 scene graph + paint 遍历 + Win32 宿主，不急着做 damage/layer。
3. 并行启动 G3 的块布局透明化、单向绑定、`ForEachBlock.Dispose`。
4. 用 `CounterApp`/`TaskList` 作为 G2 的验收 demo。

**若 No-Go**

1. 用同一套契约和矩阵做 1–2 周 SkiaSharp/自研光栅 fallback spike。
2. 更新 `design/08` 的渲染后端决策记录，明确推翻条件。
3. 不在结论出来前继续投入 Vello 相关基础设施。

## 9.8 并行修复清单（G1–G3 期间）

| 任务 | 工期 | 验收 |
|---|---:|---|
| 控制流块布局透明化 | 2–3d | `TaskList` item 间距 = 根 `Gap`；`@if` 多根元素按父容器布局 |
| `ForEachBlock.Dispose` 释放 item 作用域 | 0.5d | 组件 Dispose 后 `Signal.SubscriberCount == 0` |
| 单向绑定 `Prop={expr}` | 1–2d | signal 变化后属性跟随；`:bind` 行为不变 |
| 语法级表达式重写 | 3–5d | 字符串字面量/同名局部变量不被误改；回归测试 |
| 组件注册表（Roslyn 符号收集） | 1–2w | 样例项目 + 测试第三方程序集可被发现 |
| CI + LICENSE + 打包骨架 | 1–2d | Windows/Linux 矩阵跑 `dotnet test`；NuGet 可本地打包 |

## 9.9 资源与节奏

- **1 人**：严格串行，G1 → G2 → G3 → G4；IDE/平台按季度目标取舍。优先保“诊断 + 渲染”，砍补全档位。
- **2 人**：一人渲染（G1/G2），一人编译器/运行时（G3）；G4 会合。
- **3–4 人**：增加 IDE 与平台/3D 并行；但共享契约（DisplayList、组件注册表、span 映射）必须先冻结，避免接口漂移。
- **节奏**：每周 demo（可运行产物，不是 PPT）；每两周里程碑检查；每个架构变更写 ADR。
- **Definition of Done**：代码 + 测试 + 文档/状态更新 + 性能备注；无已知 P0/P1 缺陷。

## 9.10 变更管理

- `design/01`–`08` 是架构决策，不轻易改；实现范围变化写入本计划或 ADR。
- README 的“状态/边界/下一步”每个里程碑更新一次，避免文档与代码漂移。
- 每个 No-Go / 架构推翻都必须记录：触发条件、证据、替代方案、影响范围。
