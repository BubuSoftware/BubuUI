# 模板引擎优先的自绘 UI 框架 — 设计文档集

> 从头开发一个模板引擎 first 的类 Blazor 自绘 UI 框架的设计与实施计划。
> 状态：架构设计定稿 + G1 渲染 Gate 已出结论（Go） + G2 第一切片（窗口 + GPU 上屏）通过 · 日期：2026-10-28

## 文档导航

| # | 文档 | 内容 |
|---|------|------|
| 1 | [Blazonia 痛点分析](./01-pain-points.md) | 现状架构概述与 8 条痛点（含代码位置证据） |
| 2 | [总体架构](./02-architecture.md) | 分层架构、单向管线、与 Blazor 的根本分岔 |
| 3 | [模板引擎设计](./03-template-engine.md) | 框架核心：语法设计、编译管线、逃生舱 |
| 4 | [运行时与元素系统](./04-runtime-and-elements.md) | 组件实例模型、元素树、属性引擎、事件、组件注册表 |
| 5 | [布局、渲染与输入](./05-layout-rendering-input.md) | measure/arrange、合成器、hit test / 焦点 / IME |
| 6 | [IDE 支持](./06-ide-support.md) | VS Code + Rider 第一优先；LSP + Roslyn 投影 |
| 7 | [工具链](./07-toolchain.md) | 热重载、调试映射、无头测试、DevTools |
| 8 | [实施里程碑与风险](./08-milestones-and-risks.md) | M1–M8 阶段表、风险预警、决策记录汇总 |
| 9 | [开发计划](./09-development-plan.md) | 总体进度、G1–G8 路线图、里程碑出口标准、下一任务详细规划 |
| 10 | [G1 渲染 Gate 执行清单](./10-g1-render-gate.md) | 可勾选任务、契约、验收标准、指标表与 go/no-go |
| 11 | [G2 第一切片：窗口 + GPU Surface](./11-g2-surface-slice.md) | Win32 宿主、wgpu 上屏、sRGB/RenderFormat 坑、帧时间基线、resize 冒烟 |

## 报告

| 报告 | 内容 |
|---|---|
| [G1 渲染 Gate 报告（2026-11）](./reports/render-gate-2026-11.md) | Vello 功能矩阵、文本质量、性能基线、Go/No-Go 结论 |

## 核心理念一句话

模板不是"字符串拼 UI"的外壳，而是**编译单元**——编译期完成尽可能多的工作
（类型检查、字面量转换、更新路径分析），运行时只执行生成好的指令。

## 边界声明

- **自持**：`.ui` 语法、编译器、运行时、元素系统、布局、渲染管线、LSP。
- **复用**：仅 Roslyn 作为基础设施（解析器、语义模型、增量生成框架、Workspace）。
- **不复用**：Blazor 运行时与 Razor 语法层（HTML 语法债，详见 [决策记录](./08-milestones-and-risks.md#决策记录汇总)）。
