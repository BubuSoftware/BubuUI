# 模板引擎优先的自绘 UI 框架 — 设计文档集

> 从头开发一个模板引擎 first 的类 Blazor 自绘 UI 框架的设计与实施计划。
> 状态：架构设计定稿 + G1 渲染 Gate 已出结论（Go） + G2 第一切片（窗口 + GPU 上屏）、
> 第二切片（场景图 + 输入路由 + 可交互 Gallery）与第三切片（damage v0：元素级脏区 + 空闲跳帧）通过；
> 模板语义已切换为 **v4 失效重算模型**（普通 CLR 状态 + 组件级 `Invalidate`/`Render` + 保留元素树 + 块 `Refresh`，
> 不做依赖订阅）：[spec/01](../spec/01-template-syntax.md) v2、[spec/02](../spec/02-generated-code-contract.md) v2、
> [spec/03](../spec/03-reactivity-and-binding.md) v1；反应式 **S1–S3 已落地**、控件/组件契约 **C1–C6 已落地**
> （409 个测试全绿）；控件/模板元数据见 14（v4 换轨），15 已停止 · 日期：2026-11-05
>
> **规范正本已迁至 [spec/](../spec/README.md)**：`.ui` 语法以 [spec/01](../spec/01-template-syntax.md) 为准，
> 生成代码契约以 [spec/02](../spec/02-generated-code-contract.md) 为准，反应式与绑定以 [spec/03](../spec/03-reactivity-and-binding.md) 为准；
> 本目录保留架构说明、实现细节与偏差台账。控件/组件元数据在迁移为 `spec/04` 前仍以 14 为准。

## 文档导航

| # | 文档 | 内容 |
|---|------|------|
| 1 | [Blazonia 痛点分析](./01-pain-points.md) | 现状架构概述与 8 条痛点（含代码位置证据） |
| 2 | [总体架构](./02-architecture.md) | 分层架构、单向管线、与 Blazor 的根本分岔 |
| 3 | [模板引擎（语言规范）](./03-template-engine.md) | 词法/句法/语义、字面量、诊断映射、编译管线与逃生舱；v3 历史，含实现偏差台账 D1–D41（§3.5.3）、v4 台账 D42–D76（§3.5.4）与附录 A |
| 4 | [运行时与元素系统](./04-runtime-and-elements.md) | 组件实例模型、元素树、属性引擎、事件、组件注册表 |
| 5 | [布局、渲染与输入](./05-layout-rendering-input.md) | measure/arrange、合成器、hit test / 焦点 / IME |
| 6 | [IDE 支持](./06-ide-support.md) | VS Code + Rider 第一优先；LSP + Roslyn 投影 |
| 7 | [工具链](./07-toolchain.md) | 热重载、调试映射、无头测试、DevTools |
| 8 | [实施里程碑与风险](./08-milestones-and-risks.md) | M1–M8 阶段表、风险预警、决策记录汇总 |
| 9 | [开发计划](./09-development-plan.md) | 总体进度、G1–G8 路线图、里程碑出口标准、下一任务详细规划 |
| 10 | [G1 渲染 Gate 执行清单](./10-g1-render-gate.md) | 可勾选任务、契约、验收标准、指标表与 go/no-go |
| 11 | [G2 第一切片：窗口 + GPU Surface](./11-g2-surface-slice.md) | Win32 宿主、wgpu 上屏、sRGB/RenderFormat 坑、帧时间基线、resize 冒烟 |
| 12 | [G2 第二切片：场景图 + 输入路由](./12-g2-interactive-slice.md) | 元素树→display list、无头/窗口共用输入路由、文本 shaping 接入、Gallery 自检 |
| 13 | [G2 第三切片：damage v0](./13-g2-damage-v0.md) | RenderVersion 失效模型、脏矩形快照 diff、空闲跳帧、覆盖率与跟踪开销 |
| 14 | [控件与模板契约（详细设计）](./14-control-template-contract.md) | 标签/属性/事件/内容模型声明规则、符号派生目录、字面量转换、组件参数与插槽、生成代码与诊断契约；v4 换轨（14.18）：参数为普通 CLR 属性 + `{P}Changed`，反应式/生成形态由 spec/02、spec/03 取代；**C1–C6 已落地（2026-11-05）**，表面实测、差集评审与各切片落地记录见 §14.12.1–§14.12.6 |
| 15 | [状态与反应式契约（详细设计）](./15-state-and-reactivity.md) | **已停止（v3 历史）**：`[UiState]`/`UiObservable`/`UiComputed`、依赖分析、`UiWatch`/`Signal<T>` 订阅、EUI034–EUI044；v4 反应式见 [spec/03](../spec/03-reactivity-and-binding.md) |

## 报告

| 报告 | 内容 |
|---|---|
| [G1 渲染 Gate 报告（2026-11）](./reports/render-gate-2026-11.md) | Vello 功能矩阵、文本质量、性能基线、Go/No-Go 结论 |

## 核心理念一句话

模板不是"字符串拼 UI"的外壳，而是**编译单元**——编译期完成尽可能多的工作
（类型检查、字面量转换、静态元素树与更新点生成），运行时只执行生成好的指令并按组件失效重算。

## 边界声明

- **自持**：`.ui` 语法、编译器、运行时、元素系统、布局、渲染管线、LSP。
- **复用**：仅 Roslyn 作为基础设施（解析器、语义模型、增量生成框架、Workspace）。
- **不复用**：Blazor 运行时与 Razor 语法层（HTML 语法债，详见 [决策记录](./08-milestones-and-risks.md#决策记录汇总)）。
