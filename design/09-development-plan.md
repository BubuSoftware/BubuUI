# 9. 开发计划（2026-10 基线）

> 本文是 `design/01`–`16` 的执行计划，不改变已定架构决策，只把“做什么、先做什么、何时算完成”落到可检查的里程碑。
> 状态与反应式的实现级规范见 [spec/03](../spec/03-reactivity-and-binding.md)，其 S1–S5 分阶段分别落在本文 G3（S1–S3）、G4（S4）与 G8（S5）。
> 模板语法与生成形态分别以 [spec/01](../spec/01-template-syntax.md)、[spec/02](../spec/02-generated-code-contract.md) 为准；v4 差距台账见 design/03 §3.5.4。
> **S6「语法对齐」（K23，2026-11-08 ✅ 已实现）**：插在 G3 之后、G4 之前；
> **S6 是本文切片编号，不是 spec/03 反应式阶段 S1–S5 的延续**，也不改变反应式语义。
> 目标语法见 spec/01 §1.5，偏差见 spec/02 §12 D85–D88，阶段 P0–P6 见 design/16。
>
> **排期假设**：1–2 名全职工程师、Windows 优先、2 周一个迭代。若增至 3–4 人且能并行拆分，整体时间可压缩约 30–40%；若只有 1 人且同时承担 IDE/平台适配，按区间上限估算。

## 9.1 当前基线

> **当前实施：** [21 · 根类型统一与彻底清理](./21-root-type-unification.md)（K35/K36，2026-11-13）。统一UiObject机制已替换旧组件层；最新验证以21为准。下文早期里程碑与历史类名仅为实施台账，不是当前API规范。

**已具备**

- 设计文档 01–15（痛点、架构、模板引擎、运行时、布局/渲染/输入、IDE、工具链、里程碑、
  各 G 阶段切片记录、控件契约、状态与反应式）。
- `.ui` 前端：Lexer / Parser / AST / 字面量转换与建议 / 诊断。已实现的出口：EUI001–EUI013/EUI015、
  EUI016/017/018/019/020/021/022/023/025/026/027/028/029/030/031、EUI033/037/040/043/045、EUI100。
  `EclipseUI.Compiler.Metadata` 的目录构建另产出 EUI017/018/019/021/026/028/030 的**元数据问题**
  （`UiMetadataProblem`，不是 `.ui` 上的诊断）；✅ 2026-11-08（D74 ②/D81）：源项目问题在 C# 声明处报出、
  标签使用点分流 EUI016/017/018/030，引用程序集仍只在使用点暴露。
  EUI024（委托签名匹配）已落地（✅ 2026-11-08/D82，`EventHandlerSignatureTests` 16 例），G3 七个小切片至此全部收敛；口径见台账 D82。
- Roslyn 增量源生成器：code-behind 发现、模板解析与动态点生成、`@foreach` item 类型推断、代码发射、`#line` 映射。
- 运行时：`Signal<T>`（遗留原语，不参与模板反应式）、`StyledProperty<T>`、元素树、measure/arrange、路由事件、`@if`/`@foreach` 协调器、`Component`、无头宿主；**D114（2026-11-12）**：单线程 `UiSynchronizationContext` + `Component.InvalidateSubtree` + `WindowHost.Run` 帧循环排空，让 `async void` 续体回 UI 线程并自动失效（[design/15 §15.14](./15-state-and-reactivity.md)）。
- 输入/焦点：HitTest、指针冒泡、按下捕获、Tab 遍历、空格/回车激活；`InputRouter` 无头/窗口共用。
- 文本：HarfBuzz shaping（真实字体测试），`HarfBuzzTextPainter` 让布局与绘制共用 shaping 缓存。
- 渲染：`DisplayList` 契约、Vello CPU/GPU 后端、`ElementPainter` 场景图遍历、Win32 宿主 + wgpu 上屏、
  元素级 damage 与空闲跳帧。
- 控件/组件标签契约（C1–C6 ✅）：目录从 Roslyn 符号派生、事件迁入 `Element`、字面量转换器、
  组件标签（参数/委托事件/必填/`{P}Changed`/默认内容/生命周期）、`Ref={field}`、具名插槽
  `<Slot>` + `<ContentSlot>`（见 [14 §14.12](./14-control-template-contract.md)）。
- 反应式 S1–S3 ✅：`Invalidate`/`Render`/`U{N}`/`R{N}`、块 `Refresh` 与 Key 复用、`UiBinding` 双向
  （控件侧与组件侧）；旧语法 `:bind`/`:value` 已按 K27 移除并报 `EUI046`（`EUI043` 停用）。
- 测试：**643 个用例（Headless 327 / Compiler 241 / Rendering 75），全绿**——既有像素 golden 红
  （VelloSharp AA 舍入差 1 导致的整图 hash 漂移）已随 D91 基线重生成清零；
  编译器测试用快照钉住当前模板表面（9 标签 / 38 属性项 / 74 事件项 / 附加属性 2），样例 `.ui` 已用 `@bind-P` 新语法。

**尚不具备**

- retained layer / 真正的局部重绘（GPU 侧与脏区光栅化）。
- 样式/主题、TextBox / 完整 ListBox、文本编辑（字符输入/IME）、多行断行、bidi、字体 fallback。
- LSP / TextMate / VS Code / Rider 插件。
- 热重载、DevTools、调试映射的 IDE 接入。
- WebGPU 视口控件（不做 3D 引擎）。
- CI、LICENSE、NuGet 打包、benchmark、~~AOT 冒烟~~（✅ D95：full trimming 试点通过；AOT 运行待上游修复）。
- 组件作者面的真实样例：`samples/` 里没有任何 `[UiParameter]`/`[UiContent]`/`[UiSlot]` 用法，
  C4–C6 的组件契约只在测试夹具中验证过，尚未经真实 App 检验。

## 9.2 总体进度评估

> 百分比是主观估算，用于排期沟通，不作为绩效指标。
> **2026-11-05 按 S1–S3 / C1–C6 落地重估**；上一版 65–70% / 42–47% / 10–15% 写在 S/C 切片之前。

| 视角 | 完成度 | 说明 |
|---|---:|---|
| 托管核心（编译器前端 + 生成器 + 运行时 + 无头测试） | **85–90%** | 主链路与组件契约已通（S1–S3、C1–C6、G3 七个小切片含 EUI024 签名匹配 ✅ D82、`#line` 列映射、`EUI045` 冲突诊断、D74 ② 诊断出口，497 例全绿）；S6 语法对齐 ✅（K21–K29 / D85–D88：`@using`、`@code` 单文件、`else`/`@switch`、`@* *@`，样例与约 94 例 golden 补 `@using` 一次性迁移）；S4/S5 已收敛（D83 去装箱/Dispose 加固、D94 分配预算门槛、D95 AOT/裁剪试点、D96 死循环上限测试与最终审计；CI 单列未做），G2 四项收敛三项（D91 探针 DSL / D92 交叉一致性门禁 / D93 retained 构建层）。剩 G2 目标机 60fps 结论、上游 scissor 局部重绘、正式性能吞吐基准表与性能基准。 |
| 0.1 alpha（可交付的 2D 桌面框架） | **60–65%** | 渲染与 Win32 宿主已打通到窗口像素，元素树→display list→上屏、输入路由与 damage v0 可用（G1 门「有条件 Go」，GPU 200 元素 @1280×800 实测 6.36 ms/帧）；Grid/WrapPanel/ScrollViewer/列表虚拟化已落地（2026-11-09），retained 构建层 ✅ D93；仍缺样式/主题、TextBox/ListBox、文本编辑（IME）、脏区光栅化（上游 scissor API）。 |
| 完整设计愿景（含 IDE、3D、多平台、DevTools、1.0 硬化） | **15–20%** | IDE、3D、平台扩展、无障碍与稳定性尚未开始；编译器/渲染/控件线的完成度是 2026-11-09 重估的上调来源。 |

> **2026-11-08（K23）**：S6 语法对齐已实现（D85–D88 关闭，用例 497 → 548），上述托管核心口径已含
> 本次收敛；百分比维持 85–90% / 55–60% / 12–17% 不变，避免估算通胀（上调留待 S4/S5、G2 余项收敛时重估）。
>
> **2026-11-09（S5 收敛重估）**：S4/S5 出口按 spec/03 §13.1 核对完成（D94 预算门槛 / D95 AOT/裁剪试点 /
> D96 死循环上限测试与最终审计），G2 四项收敛三项（D91/D92/D93），控件面新增 Grid/WrapPanel/
> ScrollViewer/列表虚拟化/Unmount/附加属性（用例 548 → 657）。重估：托管核心 **90%**
> （剩 CI、正式性能吞吐基准表）、0.1 alpha **60–65%**（样式/主题、TextBox/IME、脏区光栅化未开始）、
> 完整愿景 **15–20%**（编译器/渲染/控件份额上调；IDE/3D/多平台未动）。

### 分领域状态

| 领域 | 状态 | 到 0.1 还差什么 |
|---|---|---|
| 模板前端 | 🟢 可用 MVP（v2） | **S6 语法对齐已实现（K23 / D85–D88 ✅ 2026-11-08）**：`@using`、`@code` 单文件、`else`/`@switch`、`@* *@`；其余待办：trivia/pretty-printer、表达式语义分析深化（✅ 最小闭环 EUI049 已落地 2026-11-09，全量流分析留后续）；附加属性 ✅ G4 首批已落地（2026-11-09）；泛型标签暂缓（K19，不排期） |
| 源生成器 | 🟢 可用 MVP | 组件作者面样例；EUI024 签名匹配 ✅ 2026-11-08/D82（按文件增量 ✅ 2026-11-08、`#line` 列映射 ✅ 2026-11-08/D79、`EUI045` ✅ 2026-11-08/D57、诊断出口 ✅ 2026-11-08/D74 ② 均已落地） |
| 运行时/属性 | 🟢 可用基础 | ~~属性存储去装箱~~ ✅ D83、~~生命周期 Unmount~~ ✅ K33/D89、~~失效死循环上限~~ ✅ D96（100 次 Render fail-fast）；剩样式优先级、正式性能基准表 |
| 布局 | 🟡 部分 | WrapPanel / Grid / ScrollViewer / 列表虚拟化 v1 已有实现；2026-11-13 核对发现统一测量约束及滚动测量方向仍需修正，含 Grid 按轨道重测与排列尺寸约束；目标与验收见 [design/05](./05-layout-rendering-input.md#布局)，尚未实施（块布局透明化 ✅ 2026-11-05，D77） |
| 文本 | 🟡 单行 shaping + **TextBlock 换行（Wrap 贪心断行，✅ 2026-11-13，D116）**已接入 | bidi、fallback、emoji、glyph 缓存、display list 集成（断行随 HarfBuzz 管线重修——naive 词界/字符切分是 v1 口径） |
| 输入/焦点 | 🟡 元素路由已通 | 字符输入/IME、滚轮/触摸、Shift+Tab、无障碍 |
| 渲染/合成器 | 🟡 scene graph + 上屏 + damage v0 + retained 构建层（✅ D93） | 脏区光栅化（上游 scissor API）、合成器、帧预算门槛 |
| 样式/主题 | 🟡 令牌 + Class 样式 + 主题资源 + 伪类变体 + `:disabled` + **样式表编译器（E1：.eustyle/@style → 生成 Define 调用）**已落（2026-11-09，D98–D104），E2/E4/E5 补齐（D105–D107，2026-11-09/10），**样式属性自定义化（E6：`[UiStyleProperty]` 并集目录 + enum/string 值 + EUI058/059）**已落（2026-11-12，D115） | 资源字典完整形态（S-C 后续）、后代/子代选择器组合、控件模板（S-D）、P2 复用 `[UiLiteralParser]` 的复杂类型样式值、P3 多值语法与 Owner 适用性诊断（design/19） |
| 控件库 | 🟡 极简 | TextBlock/Button/CheckBox/StackPanel/ListBoxItem 之外的基础控件 |
| IDE | 🔴 未开始 | TextMate、LSP server、VS Code/Rider 壳 |
| 工具链 | 🔴 基本未开始 | 热重载、断点映射、DevTools、无头测试 SDK |
| 平台 | 🟡 Win32 宿主已可用 | macOS/Linux、多窗口、设备丢失恢复 |
| GPU/3D | 🔴 未开始 | WebGPU 视口控件、同 device 纹理混排、输入转发；不做 3D 引擎/拾取 |
| 工程化 | 🟡 早期 | CI、LICENSE、打包、发布流程（AOT/裁剪试点 ✅ D95 full trimming 0 警告；分配预算门槛 ✅ D94；AppBuilder + 依赖注入启动形态 ✅ D101） |

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

不包含：LSP 补全、热重载、3D/WebGPU 控件、多平台、完整无障碍、DevTools。

### 1.0

在 0.1 基础上增加：

- VS Code + Rider 的补全/悬停/跳转/重命名；
- 热重载与 `#line` 调试映射；
- WebGPU 视口控件与 2D/3D 同帧混排；
- macOS/Linux 平台；
- IME、无障碍、DPI、本地化/RTL；
- API 稳定性承诺、性能预算、发布与兼容策略。

## 9.4 版本路线图

| 版本 | 目标窗口 | 内容 | 出口标准 |
|---|---|---|---|
| **G1 渲染 gate** | 2026-11（2–3 周） | Vello 最小像素闭环 + 功能/性能数据 | ✅ Go（有条件） |
| **0.1 alpha** | 2027 Q2–Q3 | 2D 渲染、Win32 宿主、样式、基础控件、虚拟化起步 | `CounterApp`/`TaskList` 真窗口可交互；像素测试稳定 |
| **0.2 alpha** | 2027 Q4 | IDE 诊断 + 补全（VS Code 先行）、热重载原型 | VS Code 中 `.ui` 可补全/报错；改 UI 不丢状态 |
| **0.5 beta** | 2028 Q1–Q2 | WebGPU 控件、macOS/Linux、DevTools、无障碍起步 | 两个平台跑通；WebGPU 内容与 2D 同帧 |
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

**进度**（2026-10-28）：前三切片已完成——Win32 宿主 + wgpu surface 上屏 + resize 冒烟
（[11 G2 第一切片](./11-g2-surface-slice.md)）；元素树→display list、输入路由、文本 shaping 接入、
Counter/TaskList 真窗口可交互（[12 G2 第二切片](./12-g2-interactive-slice.md)）；
元素级脏区检测、空闲跳帧与覆盖率统计（[13 G2 第三切片](./13-g2-damage-v0.md)）。
CPU 侧部分收敛（2026-11-08/D84）：CPU 像素 golden（`PixelGoldenTests` 4 例 + `PixelGolden.baseline.txt`）、retained 合成原型（`RetainedFrameCompositor.cs`，只 blit 脏区、不减少光栅化）、`tools/bench-60fps.ps1` + 目标机报告模板。剩余：GPU retained layer / 真正局部重绘（需 `IRenderBackend.Render` 脏区/scissor 重载，VelloSharp 0.5.0-alpha.3 无公开 API）、物理目标机稳定 60fps 结论（见 [60fps 复测报告](./reports/target-machine-60fps-2026-11-08.md)）、像素探针 DSL、CPU/GPU 交叉一致性门禁。
帧节奏修复（2026-11-08 晚）：`Win32Window` 存续期 `timeBeginPeriod(1)`（引用计数）+ `WindowHost`/`WindowSpike` 软限速器提前 1ms 唤醒自旋——修复 `Thread.Sleep` 在默认 ~15.6ms 定时器量子下整量子超调导致的墙钟 ~33fps；修复后 `Immediate` 模式墙钟 59.7–59.8fps 稳定（两样例），`Fifo` 上屏节奏受本机虚拟显示链 DWM 合成波动（32–60Hz）限制。`DwmFlush` 合成钟 pacing 实验过（43.5fps，更差）已回退；确定性修复需 VelloSharp 暴露 frame latency（原生硬编码 2），上游阻塞。

| 子项 | 状态 |
|---|---|
| Vello GPU 后端与 surface/swapchain 集成 | ✅ Dx12 上屏，截图与 CPU 输出一致 |
| Win32 窗口宿主（窗口、resize、DPI、鼠标/键盘桥接） | ✅ 事件 → InputRouter → 元素树 |
| Scene graph / paint 遍历 / display list 构建 | ✅ `ElementPainter` 全量遍历 |
| 文本整段 shaping 接入布局与绘制 | ✅ `ITextPainter` + `ITextMeasurer` 共用 shaping 缓存 |
| damage/layer v0 | ✅ `RenderVersion` + 快照 diff + 空闲跳帧；CPU 侧 retained 合成原型 ✅ D84（只 blit 脏区、不减少光栅化），GPU retained layer/真正局部重绘仍待后续（[13](./13-g2-damage-v0.md)） |
| 像素探针与 golden 测试 | 🟡 CPU 侧已落地（`PixelGoldenTests` + 文本基线，2026-11-08/D84）；缺探针 DSL 与窗口/CPU-GPU 交叉门禁（`tools/capture-window.ps1` 已就绪） |

**范围**

- Scene graph / paint 遍历 / display list 构建。
- Vello GPU 后端与 surface/swapchain 集成。
- Win32 窗口宿主：窗口、resize、DPI、鼠标/键盘桥接。
- 文本整段 shaping 接入布局与绘制。
- damage/layer v0：元素级脏区 + 空闲跳帧（已完成）；retained layer / 局部重绘排入后续切片。
- 像素探针与 golden 测试。

**出口标准**

- `CounterApp`、`TaskList` 在真实窗口可交互。—— ✅ GalleryApp 自检 6 步全过（[12](./12-g2-interactive-slice.md)）。
- resize/DPI 变化不崩、不闪。—— ✅ 860×600 ↔ 1000×760 自检 + 第一切片 4 档尺寸冒烟。
- 1280×800 简单场景稳定 60fps（目标机实测）。—— ⏳ 帧成本余量充足（2026-11-09 复测：GalleryCheck
  avg 3.00ms / p95 3.43ms @ RTX 2070，retained 后构建层开销趋零）；`Immediate` 墙钟 60.0fps
  （软限速修复 + D93 保持）；`Fifo` 上屏节奏受虚拟链合成波动与上游 `frame latency=2`（§7.3）
  限制，稳定 60fps 结论待物理目标机复测——**操作指引已就绪**（报告 §9，bench 脚本已支持
  多适配器刷新率甄别）（[报告](./reports/target-machine-60fps-2026-11-08.md)）。
- `dotnet test` 含渲染测试且跨机器稳定（容差/探针策略）。—— ✅ 657 个测试（332 无头 + 241 编译器 + 84 渲染），GPU 用例无设备自动跳过。

**已知约束**（2026-11-09 更新）：测量机器已更换为 RTX 2070（物理适配器 59Hz 自报）+ GameViewer 虚拟适配器；`bench-60fps.ps1` 已改为逐适配器列出并优先物理适配器（虚拟链自报值不代表渲染路径有效节奏）。`Immediate` 模式墙钟 60.0fps（代码侧节奏已验证、D93 retained 后保持），`Fifo` 上屏受合成波动与上游 `desired_maximum_frame_latency=2`（VelloSharp 硬编码，C ABI 无字段）双重阻塞。稳定 60fps 结论必须在物理目标机（真实显示器）复测——**操作指引见报告 §9**，两级判定口径（帧成本/墙钟/上屏）已在列。

### G3：语义与编译器硬化（6–8 周，可与 G2 并行）

**范围**

- 控制流块布局透明化（`@if`/`@foreach` 不再作为 StackPanel 参与布局）。
  —— ✅ **块布局透明切片（2026-11-05，G3 小片）**：新增 `Element.IsLayoutTransparent`（块协调器覆盖为 true），
  `StackPanelBase` 对透明节点取零内边距 + **最近非透明布局祖先**的 `Gap`，零尺寸透明块退出父容器的间距计数
  但仍 arrange 到零矩形（避免 `FinalRect` 过期被命中/脏区读到）。实现保留块为被父容器定位的节点，
  不把条目摊平进父容器子序列——摊平会让块 `FinalRect` 与子元素坐标系脱钩，绘制偏移/`PointFromRoot`/命中/脏区
  四条路径都要改；保留节点则一行不动且排布等价（口径与理由登记 **D77**，规范正本 spec/01 §4.10/§4.11）。
  实测：条目间距 19.6 → 27.6（根 `Gap=8`，原先块内 `Gap=0` 把间距吃掉），空块两侧 35.6 → 27.6（原先双份 Gap）。
  测试：`BlockLayoutTests` 10 例（含嵌套块、空块、块自身 `Gap`/`Padding` 惰性、`ListBoxItem` 祖先胜出、
  命中测试穿块坐标链、条件翻转重算）+ `TaskListEndToEndTests` 2 例（真实样例 `Gap="4"`）；
  **421 例全绿（158/202/61）**，golden 模板表面不变（新属性无 `{Name}Property` 字段，不进目录）。
  D16 关闭。G4 的 Grid/WrapPanel 需各自的透明排布口径，届时另记。
  样例目视验收：GalleryApp `--cpu` 与窗口 `--selfcheck` 双路径均见 TaskList 条目按根 `Gap="4"` 落位
  （条目顶 y≈351/416/481/546，pitch 65 = 条目高 61 + 4），窗口自检 PASS（`tasks=4`）。
  顺带修样例排序缺陷：`BuildGallery` 原先 Mount 后才填 `tasks` 并 `Invalidate()`，该失效入队到 taskList
  自己的根队列、被 `GalleryRoot` 接管后永远排不到（HEAD 基线截图同样是空 Tasks 卡，**非本切片回归**）；
  改为 Mount 前填数据（spec/03 §2.3「首次 `Build()` 会读取当前状态」），初始即渲染 3 条目。
  接管时已入队失效的迁移口径原为 spec/03 §2.7 空白，随下方 D78 切片补写并收敛。
- 所有权接管的失效迁移。
  —— ✅ **D78 切片（2026-11-07，G3 小片）**：`Adopt` 按入队顺序 drain 子组件原根队列并重入新根
  （按待渲染标记合并），接管前的 `Invalidate()` 不再静默丢弃；`Depth` 改所有权链实时计算——接管改变链长后
  不残留旧深度，否则 §2.4「同深度按入队顺序」可能把孙排在父前。契约补写 spec/03 §2.4/§2.7；
  `ComponentOwnershipTests` 5 例（接管前失效、孙随子迁移、父先于子、幂等不重复、与新根失效合并）。
- 状态与反应式 S1–S3（[spec/03](../spec/03-reactivity-and-binding.md)）：`Component.Invalidate`/`ProcessInvalidations`/`Render`、
  `U{N}`/`R{N}` 生成、根级 `P={expr}`/文本重算、块 v4（`BlockScope.RenderActions` 与显式 `Refresh`）、
  `@bind-P` 双向与 `UiBinding`（控件侧 ✅ S3、组件侧 ✅ 随 C4）、item 身份重建与 `ForEachBlock.Dispose` 泄漏修复；
  诊断 EUI007/EUI010/EUI033/EUI037/EUI040/EUI043。组件 `@bind`/`{P}Changed` 已随 C4 收敛（D54/D56 关闭）。
  S4（所有权/深度排序、异步/后台失效、`ShouldRender`；参数推送与相等保护已随 C4 落地，D52）随 G4 推进，S5 收口归 G8。
- 语法级表达式重写（Roslyn SyntaxRewriter，替代正则）。
  —— ✅ **Tokenizer 切片（2026-11-05，G3 小片）**：v4 无表达式重写需求（D20 取消信号重写），
  该项收敛为两处形态判定的正则替代。`Emitter.EventHandlerType` 换 `SyntaxFactory.ParseExpression`
  （括号/引号内的 `=>`、嵌套 lambda、匿名方法、`new Action(...)` 顶层嵌套不再误判）；
  `Parser.ParseForeachInto` 的正则换深度/字符串感知扫描（spec/01 §4.11 冻结形态）——
  集合内带引号的 `" in "`、括号内 `in` 修饰符不再撕裂集合，类型化条目/解构给出针对性 EUI100 消息。
  `UiCatalogFactory` 的特殊类型显示名规整正则保留（非表达式语义）。测试：Headless `TokenizerTests` 7 例 +
  Generator 事件形态 4 例；**358 例全绿（167/130/61）**。
- 组件注册表：从 Compilation 收集元素/属性/事件/内容模型（契约、数据模型与阶段 C1–C6 见
  [14 控件与模板契约](./14-control-template-contract.md)）。
  **进度（2026-11-05）：C1 ✅ / C2 ✅ / C3 ✅ / C4 ✅ / C5 ✅ / C6 ✅** —— `Runtime/UiAttributes.cs` 五个声明特性
  （`[UiTag]` 已于 K32 退役，标签恒为类型短名）+ 内置控件标注、
  `Compiler.Metadata` 目录模型与 Roslyn 符号扫描器、42 例一致性/快照测试全部落地；
  派生目录 **⊇** 手写目录（属性项 6→17，差集 12 项逐条评审见 [14 §14.12.1](./14-control-template-contract.md)）。
  **C2 切换完成**：分析器与生成器改读派生目录、手写目录删除；`InputEvents` 7 个事件迁入 `Element`
  （模板事件表面 1 → 9 个事件名）并补 `CheckBox.CheckedChanged`；`@foreach` 生成代码全 `global::` 限定；
  golden 快照落成文件。落地记录见 [14 §14.12.2](./14-control-template-contract.md)，台账 D22/D65/D69/D72 关闭。
  提前落到 C1 的三项（标注、`ClipToBounds`→`Render`、`[UiIgnore]+[UiParameter]` 报 EUI028）与
  扫描器对 §14.10.2 伪码的一处 API 偏离都记进台账（D63/D65/D67/D71/D72）。
- 控件契约落地：目录从符号派生（C1–C2，含**输入事件字段从 `InputEvents` 迁入 `Element`**，
  模板事件表面 1 → 8、属性项 6 → 17）、转换器与 EUI016–EUI026 + EUI031 诊断（C3）、组件标签
  （参数/委托事件/`[UiParameter(Required=true)]`/`{P}Changed`，C4）、`Ref={field}` 与剩余语法收敛（C5）、
  具名插槽 `<Slot>` + `<ContentSlot>` 宿主（C6）。
  契约 v4 换轨见 [14 §14.18](./14-control-template-contract.md)。
  **C3 完成（2026-11-03）**：内置转换器补齐（float/decimal/long/short/byte/char/枚举/TimeSpan，
  接近了 spec/01 §5.2 全表的内置类型）；使用点诊断 `EUI023`（无转换器）、`EUI025`（纯普值重复属性，
  绑定同名仍 EUI033）、`EUI031`（Class/Style/附加属性/`<Slot>`/`<ContentSlot>`）落地，
  台账 D2/D3/D6/D14/D19 关闭；落地记录见 [14 §14.12.3](./14-control-template-contract.md)。
  **C4 完成（2026-11-05）**：组件标签落地——`[UiParameter]`/`[UiContent]`/委托参数进符号派生目录
  （EUI021/027/028 元数据校验、`{P}Changed` 约定链接），`.ui` 生成按 spec/02 §4.12 冻结顺序
  （new → 作用域登记 → 参数推送 U/R → 默认内容 → `Mount()` → 挂父级），委托事件/组件 `@bind`/默认内容
  全形态可用；台账 D17/D52/D54/D56 关闭，新增 D73（残余风险）；
  落地记录见 [14 §14.12.4](./14-control-template-contract.md)。
  **C5 完成（2026-11-05）**：`Ref={field}`（EUI029）落地——语法级（非简单标识符，分析器）+
  语义级（字段缺失/类型不符/只读/静态/const，生成器探针由 C# 诊断回答），生成形态冻结于
  spec/02 §4.14（Build 一次赋值、不进 `Render()`）；EUI019/EUI020/EUI021 **使用点**口径落地——
  引用程序集控件的元数据问题经 `def.Problems` 在用点暴露（消息带完整类型名），源项目控件
  仍落 EUI002/EUI006（声明处归 EUI026）；台账新增 D74（① 使用点 ✅ / ② EUI026 声明处出口开放）；
  落地记录见 [14 §14.12.5](./14-control-template-contract.md)。
  **C6 完成（2026-11-05）**：具名插槽落地——声明侧 `[UiSlot]` 进派生目录（`Element?` → Single、
  `IReadOnlyList<Element>` → Multiple，其余 EUI021 且不进表面；重名/与 `[UiParameter]` 同属性 → EUI028），
  消费侧 `<Slot Name="…">` 与宿主侧 `<ContentSlot Name="…">` 由 EUI022 校验，
  生成形态冻结于 spec/02 §4.12.5（填充在参数与默认内容之后、`Mount()` 之前赋值；宿主在自身 `Build()` 内拼接）；
  保留标签名 `Slot`/`ContentSlot` 冲突走 EUI031 + HiddenTags。本切片 54 例（C6SlotTests 26 +
  GeneratorContract C6 13 + CatalogScanner C6 15），**409 例全绿（158/190/61）**；
  台账新增 D75（解析两级口径）/D76（基链同名成员胜出规则）；
  落地记录见 [14 §14.12.6](./14-control-template-contract.md)。附加属性已随 G4 首批落地（2026-11-09，见 G4 范围）；泛型标签暂缓实现（K19，不排期）。
- 按文件增量生成（✅ 2026-11-08）；`#line` 列映射（✅ 2026-11-08，D79）；`EUI045` code-behind
  冲突诊断（✅ 2026-11-08，D57/D80）；诊断出口补全（✅ 2026-11-08，D74 ②/D81）；
  EUI024 事件处理器签名匹配（✅ 2026-11-08，D82）——G3 七个小切片至此全部收敛。
  —— ✅ **诊断出口补全切片（2026-11-08，G3 小片，D74 ②/D81）**：`UiMetadataProblem` 增加
  纯数据 `UiSourceLocation`（成员符号声明优先、类型声明兜底），生成器注册目录级
  `RegisterSourceOutput(catalogProvider)` 消费 `catalog.Problems`（类型级）与
  `UiElementDef.Problems`（成员级），把源项目元数据问题报在 C# 声明处；引用程序集无源位置，
  仍走 D74 ① 使用点。源项目 EUI019/020/021 problem 按 §14.9.2 折叠为 EUI026，
  EUI026/EUI028/EUI031 保持原码。标签使用点按类别分流 EUI016（冲突）/EUI017/EUI018/EUI030/EUI031
  （不可用）/EUI001（未知）。诊断不阻断生成输出；逐文件管道 `WithTrackingName("PerFileGeneration")`
  让增量断言只读逐文件 step。`MetadataOutletTests` 14 例。
  该切片全绿；台账 D74 ② 关闭 / 新增 D81（EUI024 随后由 D82 落地，2026-11-08）。
  —— ✅ **`EUI045` 冲突诊断切片（2026-11-08，G3 小片，D57/D80）**：常量分配 +
  `TemplateGenerator.ProbeCodeBehindConflicts` 覆盖 spec/02 §10 五类触发（`Build`/`Render`、
  生成保留名 `__el/__cmp/__blk/__run/__bind/U/R` + 数字、非 `Component` 基类、隐藏
  `Invalidate/Mount/Track/TrackContent/AddDisposable`）；诊断位置用纯数据 `UiSourceLocation`
  （路径 + span + 0 基行列）走增量 facts，`Report` 以 `Location.Create` 重建外部文件位置，
  不把 `SyntaxTree` 带进缓存；EUI045 报 Error 但不阻止生成输出，其它错误诊断仍短路。
  `CodeBehindConflictTests` 23 例（五类触发、诊断落在 code-behind 行、仍生成源码、停用码零出现）。
  **该切片 456 例全绿（207/188/61）**；口径正本 spec/02 §10，台账 D57 关闭 / 新增 D80。
  —— ✅ **按文件增量切片（2026-11-08，G3 小片）**：管道根拆为 `AdditionalTexts → UiFileInput` 与
  `Compilation → UiCatalog` 两条，逐文件 `Combine` + `Select` 出值相等 `UiFileFacts`（code-behind 表面 + 探针事实）
  后逐文件 `RegisterSourceOutput`；目录/facts 每轮重建但下游按值命中缓存，单个 `.ui` 变化只重跑该文件。
  验收直接断言 `IncrementalStepRunReason`（更正旧注释「公开 API 没有 step reason」的误读：
  `TrackedSteps[...].Outputs` 是 `(object Value, IncrementalStepRunReason Reason)` 元组）：
  无关源码改动两文件均 `Cached`/`Unchanged`、单改 A.ui 则 A `Modified` B 缓存。
  口径与落地记录见 [14 §14.10.3](./14-control-template-contract.md)。
  —— ✅ **`#line` 列映射切片（2026-11-08，G3 小片，D79）**：`Emitter.Mapped` 由行型
  `#line N "path"` 换成增强型 span 指令 `#line (sl, sc) - (el, ec) "path"`，35 个调用点
  由行号改传 `TextSpan`；`MappedToken` 从生成文本自动定位用户表达式的列偏移，`sc` 按
  「生成缩进 + token 偏移」左移并钳制 ≥ 1（生成前缀长于源列时列退化为生成列、行号仍精确）。
  实测 Roslyn 4.9.2：列平移只作用于紧接的一行、end 元组不参与单行诊断换算、start 必须
  严格早于 end（否则 `CS8939` 让整段映射失效、错误落回 `.g.cs`）——发射器对空 span/钳制后
  补 1 列宽度。`LineSpanMappingTests` 5 例固化这三条语义，并守住生成代码零 CS8939、
  靠右表达式的列精确；`GeneratorContractTests` 3 处断言同步。
  契约补写 spec/02 §2.4 规则 9/10；`#line` 携带源 span 后，旧/新语法别名的生成文本等价
  口径改为「剥离 `#line` 后比对」（spec/02 §11）。**433 例全绿（207/165/61）**。
  顺带修掉一处既有 flaky：`TextMeasurer.Current` 是进程级全局，`ShapingTests` 临时替换成
  HarfBuzz 测量器时与布局测试类并行会污染 naive 高度断言，改为程序集级禁用测试并行。
  —— EUI005/008/009/011/012/013/015 **已实现**（`TemplateGenerator` 的 code-behind 发现与 `Parser` 的指令判定），
  本项原文把它们列为待办是 stale，2026-11-05 按代码核对后更正。
  —— ✅ **三片落地记录（2026-11-08，D82/D83/D84）**：G3 收尾 + S4/S5 运行时硬化 + G2 CPU 侧，
  全量 **497 例全绿（214/218/65）**。
  ① EUI024 事件处理器签名匹配（D82）：常量 `UiDiagnostics.EventHandlerSignature` 早已分配；
  `TemplateGenerator.ValidateEventHandlerSignature` + `TemplateProbe.IsHandlerCompatible`
  （复用 `ProbeTree` 合成 partial 方法体）借 Roslyn 把处理器表达式放进 code-behind 符号上下文，
  仅「表达式自身语义干净 + 对目标委托的转换确定失败」时报 Error 于 `.ui` 属性值并短路生成输出
  （与 EUI004 一致）；符号未解析/重载失败/`dynamic`/表达式内部错误静默，交回生成代码经 `#line`
  由 C# 报错（spec/02 §2.4 规则 8 不重复报告）。控件事件复用 `Emitter.EventHandlerType`
  （`internal static`）、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`，保证诊断与生成代码
  类型判定一致；`EventHandlerSignatureTests` 16 例。G3 七个小切片至此全部收敛。
  ② S4/S5 运行时硬化部分收敛（D83）：`PropertySystem.cs` 新增 `IValueBox`/`ValueBox<T>`，
  `Element._localValues` 改 `Dictionary<StyledProperty, IValueBox>`、重复赋值只改泛型字段消除值类型装箱；
  `Component.Dispose` 锁内清 `_pendingRenders`/`_pendingSet`；`RuntimeHardeningTests` 7 例
  （分配预算、继承/绑定/通知语义、泄漏回归）。明确未做：`Element.Unmount/Detach`（design/14
  明确 v1 不提供、G4 才补）、AOT/裁剪配置、旧语法清理、最终审计清单、帧预算/分配预算门槛、
  CI 配置、S5 基准门槛。
  ③ G2 CPU 侧部分收敛（D84）：`PixelGoldenTests` 4 例 + `TestData/PixelGolden.baseline.txt`
  （纯图元、无字体依赖的 CPU 像素 golden：`ContentHash` 全图门禁 + 探针 ±2 容差 +
  `ECLIPSEUI_UPDATE_GOLDEN=1` 生成开关 + 失败输出实际 PNG 到 TestResults）；
  `SceneGraph/RetainedFrameCompositor.cs`（CPU retained 合成原型，持有上一帧 `RenderTarget`、
  按 `FrameDamage` 只 blit 脏区，断言脏区外逐字节不变、与整帧重绘等价）；
  `tools/bench-60fps.ps1` + `design/reports/target-machine-60fps-template.md`。
  边界：原型只是合成层、不减少光栅化工作量，真正局部重绘需 `IRenderBackend.Render` 增加
  脏区/scissor 重载且 VelloSharp 0.5.0-alpha.3 未提供公开 API；GPU surface 的 retained layer
  依赖 HWND + wgpu device，本机无法验证；「稳定 60fps」结论依赖目标机真实显示器
  （本机 29Hz 虚拟显示），只能给可复现测量程序与报告模板。像素探针 DSL 已随 D91 落地（见 ④）。
  ④ G2 像素探针 DSL 收敛（D91）：`PixelProbe` 流式断言门面——点 `At`/`AlphaAt`、区域
  `DominantColor`（覆盖率 + 失败时 top3 直方图诊断）/`Contains`/`NotContains`（首命中坐标）/
  `CountIn`/`Count`、帧间 `SameAs`/`SameAsOutside`（`RenderTarget` 与 byte[] 快照重载，逐字节精确）；
  失败聚合一次性报告，`Failures` 可外查（golden 聚合诊断用）。`PixelGoldenTests` 升级**双层门禁**：
  语义探针（期望值自描述于场景定义）先于基线比对，生成模式（`ECLIPSEUI_UPDATE_GOLDEN=1`）
  写基线前先过语义探针；三个纯图元场景各配区域/计数/禁色断言（描边半覆盖像素
  `At(4,32) α≈127` 的语义随之显式化）；Damage/Retained 手写像素双循环收敛为
  `SameAsOutside`/`SameAs`。既有红根因定位：`4,32` 半覆盖像素 `#FFFFFF80`→`#FFFFFF7F`
  （VelloSharp AA 舍入差 1）+ `transform-clip` 探针点外 AA 边缘漂移，基线重生成后
  `PixelGoldens_MatchBaseline` 首次全绿；`PixelProbeTests` 8 例（手工帧，不依赖后端）。
  ⑤ G2 CPU/GPU 交叉一致性门禁收敛（D92）：`CrossBackendGateTests`——场景矩阵 14 项按图元/特性
  逐项覆盖（实色/圆角/椭圆/路径/三种渐变/裁剪栈/变换/图层透明度/混合模式/阴影/1:1 图像/虚线）；
  四级容差策略 Tight ±2/0.05%、Edges ±2/0.5%（G1 口径）、Gradient ±4/2%、Loose ±16/5%（模糊/混合），
  每场景独立定容差、超阈值即门禁失败（消息聚合全部违例 + 首违点坐标与双侧颜色）；无 GPU
  （CI 容器/无驱动）自动跳过并把探测结论写入 TestResults/cross-backend-gate-report.txt，
  有 GPU 时每场景一行实际 mismatch 数据（比率/最大通道差），同时作为策略校准依据——
  首跑校准：14 场景全部 0 违例、最大通道差 ≤1（CPU/GPU 共用 VelloSharp 渲染核心）；
  文本/字体不进矩阵（字形光栅化跨机器差异大，由 DrawGlyphs 结构测试与画廊目检覆盖）；
  GPU 探测助手与仓库根定位提取到 `Render.TryCreateGpuBackend`/`Render.RepoRoot` 共享。
  ⑥ G2 retained 显示列表收敛（D93）：`ElementPainter` 段级 retained——每元素的**自绘命令**
  （类型绘制 + 卡片 + 焦点环）按 (RenderVersion, 绝对位置, 尺寸, 焦点态) 缓存在
  `ConditionalWeakTable`（元素回收随 GC、无泄漏），树未变的元素整段复用、帧间命令构造
  分配趋零；失效契约与 DamageTracker 一致（一切影响像素的变化 bump RenderVersion），
  arrange 失效（滚动平移）由段键 Position/Size 捕获；clip 作用域与滚动条在段外每帧
  生成（thumb 位置依赖 extent/viewport 不进 Version）。`LastBuildStats` 暴露复用/重画数，
  `ResetSegments` 强制全量。`RetainedPaintTests` 8 例：复用统计（无变化全复用/按下只重画
  该按钮/焦点迁移重画新旧两处/追加子只重画新子+变高的根/滚动重画 viewer+平移子树）、
  **金标准 retained 渲染 == 全量渲染逐像素**（状态迁移后逐命令类型序列 + hash 相等）、
  GPU 后端分支的 retained 合成等价（`RetainedFrameCompositor` + GPU 后端，脏区外逐字节
  = 上一帧、全帧 == GPU 整帧参照）。VelloSharp 无 scissor API（D84 结论），光栅化仍整帧、
  构建层 retained 先行；真·脏区光栅化等上游提供裁剪 API。GalleryCheck --selfcheck 全链路 PASS。
  ⑦ S5 预算门槛扩展收敛（D94）：`RuntimeHardeningTests` +3 例覆盖新增面——附加属性重复赋值
  不重分配值盒（弱表 ValueBox 复用）、布局热路径逐帧分配常数级（Grid 轨道解析缓存 +
  cells/`double[]` 缓冲复用 + `ElementCollection` 改 struct enumerator 消除 foreach 装箱）、
  retained 帧构建每帧分配 ≤512B（锁 D93 段复用收益——段失效逐元素重画会是数 KB/帧）。
  顺带优化：`Grid.MeasureCore/ArrangeCore` 不再每帧 `string.Split` 解析轨道（引用键缓存）。
  ⑧ S5 AOT/裁剪试点收敛（D95）：`Gallery.Desktop` 为试点——**full trimming 完全通过**：
  0 条 IL2xxx 警告（源生成器架构无运行时反射，天然裁剪友好），self-contained 发布产物
  实跑 `--cpu --shot` 出图 hash 正常（native P/Invoke 完好）；**AOT 编译通过、运行被上游阻塞**：
  ILC 正常（仅 VelloSharp 4 条 IL3000），链接需手动补 `LIB`/`INCLUDE`（MSVC 14.51 + Windows
  SDK 10.0.26100 的库路径，ILC 自动探测不含 SDK）；产物启动即崩于
  `VelloSharp.NativeLibraryLoader` 的 `<Module>..cctor` 调 `Assembly.GetCallingAssembly()`
  （AOT 不支持，PlatformNotSupportedException）——与 IL3000 同源，**需上游把 native 探测改为
  `AppContext.BaseDirectory` 并去掉 GetCallingAssembly** 后复测。试点配置固化在
  `Gallery.Desktop.csproj`：`PublishTrimmed`（Release 条件）+ `TrimMode=full` + AOT 开关
  走自定义属性 `EnableAot`（**坑：`-p:PublishTrimmed=true` 命令行全局属性会传染 netstandard2.0
  的源生成器项目 → NETSDK1124，必须放 csproj**）。
  ⑨ G2 retained 标签回归修复 + 画廊布局复位（2026-11-09，D97）：`PaintButton`/`PaintCheckBox`
  的 `_text.Paint` 写主帧列表 `_list` 而非段缓冲 `_paintList`（D93 重构漏改）——首帧文字命令
  先于背景 AddRange（背景后画盖掉居中文字），且文字不进段、复用帧彻底丢字；**两条路径同错
  使「retained == 全量」金标准抓不到**（教训：金标准只锁一致性，不锁语义正确性——探针回归
  测试补上，按颜色可识别的 FillRect 文字绘制器锁「文字在控件背景之后 + 复用帧存在」）。
  GalleryCheck `add task appends item` 失败 bisect 排除渲染回归：G4 showcase（~700px）排在
  add 按钮与任务卡之前把二者推出 760px 视口，点击窗口外坐标静默丢失——交互演示复位到
  showcase 之前，selfcheck PASS。全量 659 例全绿（332/241/86）。
- 属性存储去装箱（`IValueBox`/`ValueBox<T>`，✅ 2026-11-08/D83）；剩失效队列分配预算门槛、
  benchmark 基线、帧预算门槛、CI 配置。

**出口标准**

- 列表间距/条件布局有回归测试。—— ✅ 2026-11-05：`BlockLayoutTests`（10 例）+
  `TaskListEndToEndTests.ItemSpacing_EqualsRootGap`/`EmptyList_ConsumesNoGapSlot`（真实样例，根 `Gap="4"`）。
- `#line` 能把 C# 类型错误落回 `.ui` 的行与列，且不产生非法 span 指令。—— ✅ 2026-11-08（D79）：
  `LineSpanMappingTests` 5 例（Roslyn 语义 3 例 + 生成指令合法性/零 CS8939 + 靠右表达式列精确）。
- 组件注册表能发现样例项目与一个测试用第三方程序集。—— ✅ C1/C2：扫描器能发现**合成**第三方程序集
  （`SameTagInTwoReferencedAssemblies_IsAmbiguousWithCandidates` 真的 `Emit` 两个程序集再同时引用）；
  C2 起生成器目录即派生目录，样例组件（Counter/Prefs/TaskList 经 `.ui`）与第三方程序集同链路覆盖。
- 内置控件目录由符号派生（手写目录删除），目录与 `StyledPropertyRegistry` 的一致性测试通过。—— ✅ C2（2026-11-02）：
  手写目录已删除（D64），符号派生与 `StyledPropertyRegistry`/事件字段一致性测试通过。
- 控件/组件标签契约（[14](./14-control-template-contract.md)）C1–C6 落地：自定义控件与组件可写入 `.ui`，
  元数据错误在声明处或使用点报 EUI016–EUI031、EUI033/EUI037/EUI040/EUI043/EUI045（停用码零出现）。
  —— ✅ **已达成**：C1–C6 切片收敛，`EUI045` 已实现（✅ D57/D80，`CodeBehindConflictTests` 23 例），
  D74 ② 把 EUI016/017/018/026/028/030 接上编译期出口（✅ D81，`MetadataOutletTests` 14 例）；
  EUI024 事件处理器签名匹配已落地（✅ 2026-11-08/D82，`EventHandlerSignatureTests` 16 例），码表覆盖口径完整。
- 状态与反应式 S1–S3 出口按 [spec/03 §13.1](../spec/03-reactivity-and-binding.md#131-阶段定义) 执行：`dotnet test` 全绿、
  失效合并、无泄漏、普通状态驱动的 Gallery/TaskList、无 `SetValue` 生成。
- 生成器增量口径按 14.10.3 实测确立：目录每轮重建（14 程序集 / 688 类型全扫 45.1 ms），
  但无关改动后每个生成文件的 `IncrementalStepRunReason` 必须是 `Cached`/`Unchanged`。

### S6：语法对齐（K23，2026-11-08 ✅ 已实现）

**定位**：语法表面对齐 Blazor/Razor 的独立切片，插在 **G3 之后、G4 之前**。
**S6 是本文的切片编号，不是 spec/03 反应式阶段 S1–S5 的延续**；不改变 K1–K18 运行时模型。
权威决策见 [spec/02 §14 K23](../spec/02-generated-code-contract.md)，目标语法见 [spec/01 §1.5](../spec/01-template-syntax.md)，
阶段 P0–P6 与风险 R1–R8 见 [design/16](./16-blazor-alignment-plan.md)。

**范围**

- `@using`：模板导入命名空间、生成文件保留 `using Ns;`、标签按导入过滤且内置控件无豁免（K22 / D85；**@namespace 链已由 K33 / D111 修订为隐式可见**）。
- `@code`：单文件「模板 + 代码」，位置不限、可多个块、书写顺序不影响可见性，与同名 `.cs` partial 并存（K21 / D86）。
- 流程控制：`else`/`else if`（不带 `@`）与 `@switch`（`case`/`break`/`default`）；`EUI008` 停用、新诊断从 `EUI046` 起（D87）。
- 注释：`@* *@` 与 `<!-- -->` **两套等价并存**（K26 拍板：`<!-- -->` 不设弃用期、不发告警），
  未闭合都报 `EUI100` 并推进到文件尾（顺带关闭 D12 / D88）。
- 保持不动：`{expr}`、目录驱动标签/属性、`@bind-P`、`Key`/`Ref`、具名插槽、span 型 `#line`、逐文件增量、EUI 诊断体系。
- 明确不做（K23 ②）：`@page`/`@layout`/`@rendermode`/`@inject`/`@typeparam`/`@attributes` 等 Web 指令、
  HTML 语法债、渲染树 diff；泛型标签仍按 K19 暂缓、不排期。

**出口标准**

- spec/02 §12 的 **D85–D88 全部关闭**，design/03 §3.5.4 双向同步；顺带关闭 **D11**（表达式捕获改按 C# 词法扫描）
  与 **D12**（未闭合注释）。
- 样例与约 94 例 golden 完成 `@using` 一次性迁移；至少一个样例为底部 `@code` 的单文件形态。
- `dotnet test EclipseUI.slnx` 零停用诊断、样本项目编译与运行；用例基线 497 → **548**（K31 后 **567**）
  （新增 `S6SyntaxTests` 26、`ExpressionCaptureTests` 13、`SwitchBlock` 运行时 6、生成器契约 6）。
  既有例外：`PixelGoldens_MatchBaseline` 的 `solid-primitives`/`transform-clip` 两幅整图 hash 仍不符
  （探针 ±2 全过，与 S6 无关，另立 G2 项）。
- Q3/Q4/Q6/Q7 与 case 细项已按 **K24–K30** 登记（2026-11-08 拍板）并随 S6 实施 ✅。
- **K31**（2026-11-09）：`@using Alias = Ns.Type` 一体两用（C# 别名 + 模板标签别名，优先于目录 tag），
  用于第三方控件与内置控件 tag 重名；`EUI048`；回归 `UsingAliasTests` 15 / 生成器契约 4。

### G4：样式、控件与虚拟化（8–10 周）

**范围**

- 选择器编译（AOT 友好）、样式优先级、资源查找、亮暗主题。
  —— ✅ **S-A 主题令牌化（2026-11-09，D98）**：`ElementTheme` 变可替换 token 表（virtual 令牌 +
  静态 Dark/Light 预置）；`ElementPainter.ReplaceTheme` 换表丢弃全部段缓存（段内命令持有颜色
  快照）+ 重置脏区，下一帧整帧重绘；`WindowHost.SetTheme` 运行时切换；Gallery「Toggle theme」
  按钮双宿主接线。**S-B Class 样式 + 有效值分层（2026-11-09，D99）**：属性系统落分层
  （local > class > 继承 > 默认，失效语义与 SetValue 一致）；`Element.Classes` +
  `Styling.Provider`（`IStyleProvider` 注入，Rendering 层 `StyleRegistry` 类名直配 setter 序列、
  无运行时选择器引擎；晚装配补应用 pending 声明）；`Visual` 附加属性组（Background/Foreground/
  CornerRadius，null=不覆盖）+ painter 读覆盖回落主题（未设置零漂移）；「显式取消覆盖」=
  `ClearLocalValue`（null 值盒不占层）。**S-C 最小资源查找（2026-11-09，D100）**：`ThemeColor`
  暗亮双色 + `ElementTheme.IsLight` 虚标志；`Visual.BackgroundResource/ForegroundResource`
  （优先级 resource > 静态覆盖 > 主题令牌）+ `StyleBuilder` 双色重载；painter 在段构建期解析——
  `ReplaceTheme` 丢段重建即随新表刷新，无需新增失效维度；Gallery 分区卡/样式表全改双色资源
  （inline 静态色并示两种语义）。完整资源字典、`Style=` 内联与模板 `Class` 语法（目录/生成器/
  编译期类名校验）留 S-C 后续片；控件模板（S-D）远期。
- **伪类状态样式（✅ 2026-11-09，D102）**：`PseudoStates` 掩码（Hover/Pressed/Focused）由
  InputRouter/FocusManager 维护，状态变化 = 渲染失效 + class 层重应用；定义侧链式语法
  `Define("primary", s => s.Background(...)).Hover(s => ...).Pressed(s => ...)`——变体按声明
  顺序应用、同开时后声明胜；段键含状态位（状态翻转段重建）。
- **:disabled + IsEnabled（✅ 2026-11-09，D103，design/17 执行计划 E3 片）**：`PseudoStates.Disabled`
  第 4 位为**有效值**（`IsEnabled` 沿祖先链继承合成进掩码——父禁用子禁用，变体/段键零特殊处理）；
  Router 吞禁用按下/滚轮/移动（按住中途禁用释放吞 Click）、Focus 拒绝、Tab 跳过；painter 置灰
  令牌回落（class `.Disabled(...)` 变体优先）；`Visual.FocusRingWidth` 环宽进样式。样式系统
  完整设计 + 执行计划（E3✅→E1→E2→E4→E5）见 design/17。
- 宿主启动形态：**AppBuilder + 依赖注入（✅ 2026-11-09，D101）**：Avalonia 式启动——
  `AppBuilder.Configure<TApp>().UseWindows().UseVello().UseHarfBuzzText().UseTheme(...).
  UseStyles(StyleRegistry.Instance, styles.Define).ConfigureServices(...).Start(args)`；
  `Application` 基类（`Services`/`MainWindow`/`OnFrameworkInitializationCompleted`）+
  描述式 `Window`（Title/尺寸/Content/RootComponent/Theme/帧循环调节项）由平台
  `ClassicDesktopLifetime` 材料化为 Win32 + 表面 + `WindowHost`。**依赖方向**：Runtime 不引用
  Rendering/平台包——主题走 `IHostTheme` 标记接口、样式走 `IStyleProvider` 泛型传实例，
  平台能力全走 `IPlatformSetup` 扩展片；容器 Microsoft.Extensions.DependencyInjection，
  平台先注册、用户 `ConfigureServices` 后注册可覆盖（MEDI 最后注册优先）。主题运行期切换
  经 `Application.SetTheme`（`WindowHost` 实现 `IWindowHost`，生命周期注入 `Application.Host`）。
  `Build()` 与 `Start()` 分离：无头/离屏/测试只装配不运行（画廊 `--cpu/--shot` 走此路径）。
- 控件集：ListBox、TextBox、ScrollViewer ✅（2026-11-09）、Grid ✅、WrapPanel ✅、Slider 等。
- `VirtualizingPanel` 与 item 模板的容器复用。—— ✅ **v1 切片（2026-11-09）**：元素级 `VirtualizingStackPanel`（ItemCount + ItemFactory、视口窗口实例化、均匀行高）；容器复用（pooling）、变高行、item 模板编译 RenderUnit 留后续片。
- 状态与反应式 S4：组件所有权/深度排序、参数推送与相等保护、异步/后台失效、`ShouldRender`（[spec/03 §13.1](../spec/03-reactivity-and-binding.md#131-阶段定义)）。
- 控件契约 C6：具名插槽 + `ContentSlot` 宿主（✅ 2026-11-05，14 §14.12.6）；
  附加属性 ✅ **首批切片已落地（2026-11-09）**：`AttachedProperty<T>` 弱表存储、`Grid` 静态类载体（Row/Column）、
  目录 `UiAttachedPropDef`、EUI031 解禁 → 未知点号落 EUI002、生成直调静态 setter（14 §14.5.5 / spec/02 §4.19）；
  泛型标签按 K19 暂缓实现、不排期（14 §14.12.6 范围说明）。
- 键盘/焦点/IME 接口预留。

**出口标准**

- 主题切换 demo；样式变更只重排/重绘受影响子树。—— ✅ 主题切换 demo（Toggle theme，D98）；
  样式值变更按属性失效级别触发重算（与 SetValue 同一失效路径，D99）——整帧重绘为当前上屏粒度
  （脏区光栅化等上游 scissor API）。
- 1000+ 条目列表滚动流畅；容器复用有测试。
- 状态与反应式 S4 出口按 [spec/03 §13.1](../spec/03-reactivity-and-binding.md#131-阶段定义)：所有权/参数推送/异步失效/`ShouldRender` 测试通过。

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
- DevTools：元素树检查器、失效链（`Invalidate` → `Render` → 块 `Refresh`）与帧剖析。
- 无头测试 SDK 与 CI 模板。

**出口标准**

- 改 `.ui` 不丢 code-behind 状态。
- 断点可直接下在 `{}` 表达式上。
- CI 可跑无头 + 像素测试。

### G7：WebGPU 控件与平台扩展（10–14 周）

> 范围修订（2026-10-28）：3D 只做 **WebGPU 视口控件**，不做 3D 引擎、材质/场景图、相机与拾取。
> 应用自带渲染器；框架负责布局、裁剪、DPI、输入、纹理合成与 damage。

**范围**

- `WebGpuView` 控件：元素布局/裁剪/DPI/hit test；`OnDemand` / `Continuous` 帧调度。
- 向应用暴露 WebGPU 语义的 device/queue 与离屏目标纹理；应用自行建管线与编码。
- 合成：同 device 纹理 → Vello image（`CreateImageFromTexture`）单次 submit；
  CPU/CI 用 readback 兜底。
- 输入转发：指针/键盘/滚轮事件交给应用；框架不做 3D 拾取。
- 样例：raw WebGPU 旋转三角形/立方体 + 2D UI 混排、resize/DPI、多视口。
- macOS（Metal）/Linux（Vulkan）宿主与跨端回归。

**任务顺序**

1. **G7-0 spike**：同 device 纹理导入/单次 submit/同步与生命周期（先证伪风险）。
2. G7-1 控件契约与布局/裁剪/DPI。
3. G7-2 合成路径（共享纹理 + readback 兜底）。
4. G7-3 damage/帧调度集成（视口矩形脏区，不整窗恒脏）。
5. G7-4 输入转发 + 样例 + 测试。
6. G7-5 平台扩展。

> G7-0 与控件本体不依赖 IDE/样式系统，可在 G2 结束后单独提前插队（1–2 周 spike + 控件），
> 平台扩展仍留在 G7。

**出口标准**

- WebGPU 内容与 2D 同帧、同一 device/queue 单次 submit；resize/DPI 下无撕裂/错位。
- 视口空闲时不额外提交；动画视口只标记自身矩形。
- 至少两个平台跑通 0.1 场景。

**非目标**：3D 引擎、场景图、材质、相机、动画时间线、glTF、3D picking、
完整 WebGPU JS API 兼容层。

### G8：1.0 硬化（10–12 周）

**范围**

- 无障碍（UIA/屏幕阅读器）、IME 完整链路、DPI/多显示器。
- 本地化/RTL、剪贴板/拖放、多窗口/对话框。
- 性能预算、内存/启动、AOT/裁剪、API 稳定性与兼容策略。
- 状态与反应式 S5：基准/预算、AOT 样例、线程断言、`:bind`/`:value` 清理与最终审计（[spec/03 §13.1](../spec/03-reactivity-and-binding.md#131-阶段定义)）。
  —— **S5 出口已于 2026-11-09 核对收敛**（D94 分配预算门槛 / D95 AOT/裁剪试点 / D96 死循环上限测试与最终审计；
  `:bind`/`:value` 清理 ✅ S6/K27，`Element.Unmount` ✅ G4/K33，运行时硬化 ✅ D83）——
  正式性能吞吐基准表与 CI 随 G8。
- 文档、示例、迁移指南、发布流程。

**出口标准**

- 性能预算达标；无已知 P0/P1 缺陷。
- 状态与反应式 S5 出口按 [spec/03 §13.1](../spec/03-reactivity-and-binding.md#131-阶段定义)：基准/AOT/线程断言与弃用清理完成。
- 外部项目可在不修改模板语义的情况下升级。

## 9.6 关键路径与并行

```
G1 渲染 gate
  └─ G2 2D 垂直切片 ── S6 语法对齐(K23) ── G4 样式/控件/虚拟化 ── G8 1.0 硬化
        │                      │
        │                      └─ G5 IDE P0–P1 ── G6 工具链
        │
        └─ G7 WebGPU 控件 + 平台扩展

G3 语义/编译器硬化：从 G1 结束后并行，最迟在 S6 前完成关键项
S6 语法对齐：G3 之后 / G4 之前；偏差 D85–D88；不改变 K1–K18 运行时模型
```

- **关键路径**：G1 → G2 → **S6** → G4 → G8（K23 ⑦）。
- **S6 必须在 G4 前完成**：`@using` 改变标签解析与全部样例 / golden，G4 的样式与控件工作若先落地会叠加迁移成本。
- **G3 必须并行**：它修的是会污染 demo 的语义问题，不能等渲染做完再补。
- **G5 依赖 G3 的组件注册表**：没有符号元数据，LSP 补全只能靠硬编码。
- **G7 依赖 G2 的帧循环/display list**：不要在渲染契约稳定前做 WebGPU 控件。

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
- 3D 引擎、材质/场景图（3D 仅规划为 WebGPU 视口控件）；
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
3. 并行启动 G3 的块布局透明化、状态与反应式 S1–S3（`Invalidate`/`Render`/`P={expr}`/块 `Refresh`/item 重建/`@bind-P`）、`ForEachBlock.Dispose`。
4. 用 `CounterApp`/`TaskList` 作为 G2 的验收 demo。

**若 No-Go**

1. 用同一套契约和矩阵做 1–2 周 SkiaSharp/自研光栅 fallback spike。
2. 更新 `design/08` 的渲染后端决策记录，明确推翻条件。
3. 不在结论出来前继续投入 Vello 相关基础设施。

## 9.8 并行修复清单（G1–G3 期间）

| 任务 | 工期 | 验收 | 状态 |
|---|---:|---|---|
| 控制流块布局透明化 | 2–3d | `TaskList` item 间距 = 根 `Gap`；`@if` 多根元素按父容器布局 | ✅ 2026-11-05（D16 关闭/D77，`BlockLayoutTests` 10 例 + `TaskListEndToEndTests` 2 例 + GalleryApp 双路径目视） |
| `Adopt` 迁移已入队失效（spec/03 §2.7 未定义） | 0.5d | 接管前 `Invalidate()` 的组件在新根队列中仍被渲染；回归测试 | ✅ 2026-11-07（D78，`ComponentOwnershipTests` 5 例；spec/03 §2.4/§2.7 契约补写） |
| `#line` 列映射（G3） | 0.5d | span 型指令合法（无 CS8939）；类型错误落回 `.ui` 行/列；旧/新语法等价口径同步 | ✅ 2026-11-08（D79，`LineSpanMappingTests` 5 例；spec/02 §2.4/§11 契约补写） |
| `ForEachBlock.Dispose` 释放 item 作用域 | 0.5d | 组件 Dispose 后条目作用域资源全部释放（无泄漏） | ✅ S2（D51） |
| S1–S2 失效重算：`Invalidate`/`Render`/`U{N}` + 块 v4 | 2–3d | 失效后属性/文本重算；块 `Refresh()` 正确重建；`@bind-P`（S3）行为不变 | ✅ S1/S2（2026-10-30） |
| 语法级表达式重写 | 3–5d | 字符串字面量/同名局部变量不被误改；回归测试 | ✅ 收敛为 tokenizer 切片（2026-11-05，v4 无重写需求，D20） |
| 组件注册表（Roslyn 符号收集） | 1–2w | 样例项目 + 测试第三方程序集可被发现 | ✅ C1–C6（2026-11-05） |
| CI + LICENSE + 打包骨架 | 1–2d | Windows/Linux 矩阵跑 `dotnet test`；NuGet 可本地打包 | 🔴 未开始 |

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
