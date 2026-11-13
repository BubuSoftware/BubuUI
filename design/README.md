# 模板引擎优先的自绘 UI 框架 — 设计文档集

> 从头开发一个模板引擎 first 的类 Blazor 自绘 UI 框架的设计与实施计划。
> 状态：架构设计定稿 + G1 渲染 Gate 已出结论（Go） + G2 第一切片（窗口 + GPU 上屏）、
> 第二切片（场景图 + 输入路由 + 可交互 Gallery）与第三切片（damage v0：元素级脏区 + 空闲跳帧）通过；
> 模板语义已切换为 **v4 失效重算模型**（普通 CLR 状态 + 组件级 `Invalidate`/`Render` + 保留元素树 + 块 `Refresh`，
> 不做依赖订阅）：[spec/01](../spec/01-template-syntax.md) v2、[spec/02](../spec/02-generated-code-contract.md) v2、
> [spec/03](../spec/03-reactivity-and-binding.md) v1；反应式 **S1–S3 已落地**、控件/组件契约 **C1–C6 已落地**、
> G3 七个小切片（含 EUI024 事件处理器签名匹配 ✅ 2026-11-08/D82）全部收敛、
> S4/S5 运行时硬化与 G2 CPU 侧部分收敛（2026-11-08/D83/D84）；**567 个用例（274/228/65）、566 绿**
> （唯一红为既有像素 golden `PixelGoldens_MatchBaseline`，与 S6 无关）；
> 余项：S4/S5（Element.Unmount 属 G4、AOT/裁剪、旧语法清理、最终审计、帧预算/分配预算门槛、CI）、
> G2（GPU retained layer/真正局部重绘、目标机稳定 60fps 结论、像素探针 DSL、CPU/GPU 交叉一致性门禁）、
> G4（样式/主题/附加属性；泛型标签暂缓 K19、不排期）、IDE/DevTools；
> **S6 语法对齐已实现（K23 + K24–K31，✅ 2026-11-08）**：`@using`/`@code` 单文件/`else`/`@switch`/`@* *@`，
> **K31（2026-11-09）** 追加 `@using Alias = Ns.Type` 一体两用（C# 别名 + 标签别名，`EUI048`）；
> 目标语法见 [spec/01 §1.5](../spec/01-template-syntax.md)，偏差 D85–D88 已收敛（D11/D12 同期关闭），计划见 [16](./16-blazor-alignment-plan.md)；
> 控件/模板元数据见 14（v4 换轨），15 已停止 · 日期：2026-11-08
>
> **规范正本已迁至 [spec/](../spec/README.md)**：`.ui` 语法以 [spec/01](../spec/01-template-syntax.md) 为准，
> 生成代码契约以 [spec/02](../spec/02-generated-code-contract.md) 为准，反应式与绑定以 [spec/03](../spec/03-reactivity-and-binding.md) 为准；
> 本目录保留架构说明、实现细节与偏差台账。控件/组件元数据在迁移为 `spec/04` 前仍以 14 为准。

## 文档导航

| # | 文档 | 内容 |
|---|------|------|
| 1 | [Blazonia 痛点分析](./01-pain-points.md) | 现状架构概述与 8 条痛点（含代码位置证据） |
| 2 | [总体架构](./02-architecture.md) | 分层架构、单向管线、与 Blazor 的根本分岔 |
| 3 | [模板引擎（语言规范）](./03-template-engine.md) | 词法/句法/语义、字面量、诊断映射、编译管线与逃生舱；v3 历史，含实现偏差台账 D1–D41（§3.5.3）、v4 台账 D42–D88（§3.5.4）与附录 A |
| 4 | [运行时与元素系统](./04-runtime-and-elements.md) | 组件实例模型、元素树、属性引擎、事件、组件注册表 |
| 5 | [布局、渲染与输入](./05-layout-rendering-input.md) | measure/arrange、合成器、hit test / 焦点 / IME |
| 6 | [IDE 支持](./06-ide-support.md) | VS Code + Rider 第一优先；LSP + Roslyn 投影；**§6.9 已落地 P0–P8**（项目系统驱动的单一 Compilation：MSBuild 求值 / 真生成器 / 目录唯一事实源 / 符号查询式转定义 / 诊断双通道 / watcher / 内嵌 C# 语言服务；五条验收已化为回归夹具。边界：`{expr}` 九项全通，`@code` 的补全 / 悬停 / 重命名未接，见 §6.9.10） |
| 7 | [工具链](./07-toolchain.md) | 热重载、调试映射、无头测试、DevTools |
| 8 | [实施里程碑与风险](./08-milestones-and-risks.md) | M1–M8 阶段表、风险预警、决策记录汇总 |
| 9 | [开发计划](./09-development-plan.md) | 总体进度、G1–G8 路线图、里程碑出口标准、下一任务详细规划 |
| 10 | [G1 渲染 Gate 执行清单](./10-g1-render-gate.md) | 可勾选任务、契约、验收标准、指标表与 go/no-go |
| 11 | [G2 第一切片：窗口 + GPU Surface](./11-g2-surface-slice.md) | Win32 宿主、wgpu 上屏、sRGB/RenderFormat 坑、帧时间基线、resize 冒烟 |
| 12 | [G2 第二切片：场景图 + 输入路由](./12-g2-interactive-slice.md) | 元素树→display list、无头/窗口共用输入路由、文本 shaping 接入、Gallery 自检 |
| 13 | [G2 第三切片：damage v0](./13-g2-damage-v0.md) | RenderVersion 失效模型、脏矩形快照 diff、空闲跳帧、覆盖率与跟踪开销 |
| 14 | [控件与模板契约（详细设计）](./14-control-template-contract.md) | 标签/属性/事件/内容模型声明规则、符号派生目录、字面量转换、组件参数与插槽、生成代码与诊断契约；v4 换轨（14.18）：参数为普通 CLR 属性 + `{P}Changed`，反应式/生成形态由 spec/02、spec/03 取代；**C1–C6 已落地（2026-11-05）**，表面实测、差集评审与各切片落地记录见 §14.12.1–§14.12.6 |
| 15 | [状态与反应式契约（详细设计）](./15-state-and-reactivity.md) | **已停止（v3 历史）**：`[UiState]`/`UiObservable`/`UiComputed`、依赖分析、`UiWatch`/`Signal<T>` 订阅、EUI034–EUI044；v4 反应式见 [spec/03](../spec/03-reactivity-and-binding.md)。**§15.14 线程调度 ✅ D114 已实现（2026-11-12）**：单线程 `UiSynchronizationContext` + 帧循环排空 + 子树自动失效 |
| 16 | [`.ui` 语法对齐 Blazor 计划](./16-blazor-alignment-plan.md) | **✅ S6 已实现（K23 + K24–K30，2026-11-08）**：对齐矩阵、P0–P6、Q1–Q8（已全部拍板）、风险 R1–R8、单文件示例（附录 A）与生成契约草案（附录 B）；目标语法正本在 spec/01 §1.5；后续增量 K31（`@using` 别名）见 spec/02 §14/§4.18 |
| 17 | [样式系统完整设计](./17-style-system.md) | **✅ 五片落地（2026-11-10）**：有效值四层级联、`.eustyle` 样式表、伪类位掩码、主题双色、编译期校验；D98–D107 / E1–E5 |
| 18 | [Razor C# 智能提示借鉴](./18-razor-csharp-intellisense.md) | **调研 + 方案建议（2026-11-11，未实施）**：Razor 的生成文档 / `SourceGeneratedDocument` / `SourceMapping` / VS Code 虚拟 HTML 链路；EclipseUI 的 Document 层与统一映射差距；P0–P3 借鉴项、落地顺序、验收与待拍板 |
| 19 | [样式属性自定义化设计](./19-custom-style-properties.md) | **提案（2026-11-12，未实施）**：`[UiStyleProperty]` 字段标注 + 全局符号扫描进样式目录、值语法扩展（枚举 keyword/字符串/复用 `[UiLiteralParser]`）、`s.Set(field,…)` 发射通道、目录并集冲突规则（EUI058/059）、P1–P3 落地切分与规范影响清单 |
| 20 | [窗口模板设计](./20-window-template-root.md) | **P1 落地（2026-11-13，D117）**：`.ui` 根节点 `<Window>`（保留模板根标签，根 = 基类的 Avalonia 式规则落在 Component 家族内）→ `WindowComponent` 子类 + `CreateWindow()` 一行装配；窗口模板退出可组合表面（EUI060–063）；动态属性经失效循环回推已材料化窗口（`IWindowLiveUpdater`，live 面 Title/尺寸） |

## 报告

| 报告 | 内容 |
|---|---|
| [G1 渲染 Gate 报告（2026-11）](./reports/render-gate-2026-11.md) | Vello 功能矩阵、文本质量、性能基线、Go/No-Go 结论 |
| [Blazor 与 EclipseUI 语法对比表](./reports/blazor-vs-eclipseui-syntax.html) | 参考资料（非决策）：5 组 21 项分组对比 + Razor 11 个指令速查；状态徽标随 K21/K22/K23 同步 |

## 核心理念一句话

模板不是"字符串拼 UI"的外壳，而是**编译单元**——编译期完成尽可能多的工作
（类型检查、字面量转换、静态元素树与更新点生成），运行时只执行生成好的指令并按组件失效重算。

## 边界声明

- **自持**：`.ui` 语法、编译器、运行时、元素系统、布局、渲染管线、LSP。
- **复用**：仅 Roslyn 作为基础设施（解析器、语义模型、增量生成框架、Workspace）。
- **不复用**：Blazor 运行时与 **Razor 编译器 / HTML 语法层**（HTML 语法债，详见 [决策记录](./08-milestones-and-risks.md#决策记录汇总)）。
  **K23 修订（2026-11-08）**：不复用的是 Razor 编译器与 HTML 过渡机制，**组织形态可对齐 Blazor**；
  「不复用 Blazor 运行时」不变。目标语法见 spec/01 §1.5。
