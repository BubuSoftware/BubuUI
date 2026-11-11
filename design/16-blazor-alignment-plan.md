# 计划：`.ui` 语法向 Blazor/Razor 对齐（S6 语法对齐切片）

> **状态：✅ S6 已实现（2026-11-08）——K23 整波与 K24–K30 实施细项全部落地，D85–D88 收敛，497 例回归全绿**
> 已落地的决策：**K21**（`@code` 位置与成员可见性，§5.2）、**K22**（`@using` 与标签作用域，§5.3）、
> **K23**（整波方向与范围：对齐方向 / 不做 Web 指令 / 流程控制 / 单文件 / 表达式记号 / S6 排期）、
> **K24–K30**（Q3 循环范围 / Q4 `@code` 内容 / Q6 注释口径 / Q7 旧语法清理 / `@{}` 不采纳 / `@switch` 细节），
> 权威登记在 [`spec/02` §14](../spec/02-generated-code-contract.md)，目标语法正本在 [`spec/01` §1.5](../spec/01-template-syntax.md)。
> 提出：2026-11-08 用户需求「目前的语法我不满意，请列一个计划和 Blazor 对齐；取其精华去其 HTML 相关的糟粕；`@page`/`@rendermode` 之类的不需要；流程控制也要对齐；支持单个页面同时写代码和模板」。
> 依据：Microsoft Learn · ASP.NET Core 10.0 Razor 语法参考、Blazor 组件文档、渲染模式文档；本仓 `spec/01`–`03`、`design/01`–`15`、`design/09`。
> 按 K20 与 `spec/01` §8 规则 1/7/8：**P0 决策冻结闸门已完成（2026-11-08）**——K23 已登记、
> `spec/01` §1.5 / `spec/02` §2/§12/§14 / `spec/03` §13.3 / `design/01`/`02`/`03`/`08`/`09` / 三份 README 已同步、
> 规范先行偏差 D85–D88 已进双台账。~~本文剩余内容是实施计划与待拍板细项~~
> **2026-11-08：细项已拍板（K24–K30）并全部实施，本文保留为实施过程记录。**
> 实施顺序 P1→P6 已全部走完（见 §4 各阶段 ✅）；本轮追加两项：**K30**（section 边界口径：
> `break;` 单独结束该行、空 section 必须显式 `break;`、`case 1:` 紧跟 `case 2:` 报 `EUI100`）
> 与 **D11 关账**（表达式捕获改按 C# 词法扫描，见 §4 P1）。

---

## 1. 目标与边界

三句话：

1. **借鉴 Razor 的书写体验**：流程控制形态、模板内代码块、单文件「代码 + 模板」、Razor 注释。
2. **不复用 Razor 编译器与 Blazor 运行时**，不引入任何 Web/HTML 概念；`{expr}`、目录驱动标签/属性、`@bind-P`、`Key`/`Ref`、具名插槽等自有资产保持不变。
3. **对齐的是语法表面，不是更新语义**：v4 的保留元素树 + 失效重算 + 块协调器（K1–K18）不动。

需要明确区分：现有文档反对的是「**复用 Razor 语法层**」（连带 HTML 兜底、`@("xxx")`、属性大小写不敏感等语法债），并不排斥「**借鉴 Razor 的控制流写法**」。本次改造不推翻「不复用 Blazor 运行时」这条核心，只把「不用 Razor 语法」修订为「不用 Razor 编译器，书写形态对齐」。

## 2. 必须先处理的既有冲突

| # | 冲突点 | 位置 | 处理方式 |
|---|---|---|---|
| C1 | 「不复用 Blazor 运行时与 **Razor 语法层**」 | `design/01` §8、`design/02` 边界声明、`design/08` 决策表、`design/README` | ✅ **已同步（K23 ①）**：四处均加注「不复用 Razor 编译器 / HTML 语法债，组织形态可对齐」；**保留**「不复用 Blazor 运行时」 |
| C2 | 冻结线 1「code-behind 是唯一状态载体」 | `spec/01` §1.1/§1.2/§1.4、`spec/02` §2.3、`spec/03` §4.4/§13.3 | ✅ **已同步（K23 ④）**：冻结线 1 保留原文并加注目标口径，§1.5 定义状态载体 = 同名 `.cs` partial **与** `@code` 成员；偏差 D86 |
| C3 | 冻结线 7「结构变化只由块处理」 | `spec/01` §1.4 第 7 条 | ✅ **已同步（K23 ③）**：多分支选择仍由块协调器承担、不引入元素 diff；`for`/`while` 复用语义待 Q3 |
| C4 | 生成成员白名单「仅此四类」 | `spec/02` §2.3/§2.5 | ✅ **已同步（K23 ④）**：§2.3 登记第五类「`@code` 透传成员」并标注当前实现仍四类，§2.5 写明唯一例外；偏差 D86 |
| C5 | `EUI005` 强制存在同名 `.cs` | `spec/01` §1.1/§6.2、`TemplateGenerator.cs` L72 | ✅ **目标口径已登记（K23 ④）**：§1.1/§6.2 标注放宽为「`@code` 或同名 partial 至少其一」；代码待 P3，偏差 D86 |
| C6 | K17 `EUI045` 只守护 code-behind | `spec/02` §14 K17、`TemplateGenerator.cs` L215–278 | 扩展守护范围到 `@code` 内声明的成员与生成保留名 |
| C7 | `EUI008` 保留 `@switch`、`EUI009` 拒绝所有未知指令 | `spec/01` §2.7/§6.2、`spec/02` §4.14、`Parser.cs` L280/L285 | ✅ **目标口径已登记（K23 ③）**：`EUI008` 停用且保持停用、`EUI009` 收窄到 K23 ② 永久不提供清单、新码从 `EUI046` 起；代码待 P2/P5，偏差 D87 |
| C8 | 「`@code` 被明确列为没有的语法」 | `spec/01` §3.1、`design/03` §3.2.1 L256 | ✅ **已同步（K23）**：`spec/01` §3.1 改为「当前实现没有 + 按 §1.5 已定为目标语法」；v3 历史按 `spec/README` 第 5 条保留原文、只加注取代关系 |
| C9 | 偏差台账 | `spec/02` §12、`design/03` §3.5.4 | ✅ **已同步（K23）**：双台账新增 **D85–D88**（规范先行，目标阶段 S6），标题与全仓锚点改为 D42–D88 / `d42d88`；下一编号从 D89 起。D12 的关闭条件并入 D88，D11 口径仍待 P1 |
| C10 | `@using` 与标签作用域 | `spec/02` §2.2/§2.5、`spec/01` §1.2/§3.1、`spec/03` §4.4、`design/09` §9.5、`UiCatalogModel.cs` L213 | ✅ **已同步（K22/K23）**：规范正文全部加注目标口径，S6 切片段与三份 README 状态段已登记；偏差 D85，代码待 P1–P3、样例/golden 迁移待 P6 |

## 3. 对齐矩阵

### 3.1 采纳（精华）

| Razor 形态 | 采纳口径 | 备注 |
|---|---|---|
| `@if (...) { }` / `else if (...) { }` / `else { }` | ✅ | **`else`/`else if` 不带 `@`**，与 Razor 一致；生成侧映射为多分支 `ConditionalBlock` |
| `@foreach (...) { }` | ✅ 保持现状 | 现有 `Key={}` 复用不变；头解析可扩展到 `T item in ...`、解构（当前强制 `var`） |
| `@for` / `@while` / `@do while` | ⚠️ 待 Q3 | 动态次数与块协调器 Keyed 复用冲突最大，见 R2 |
| `@switch (...) { case ...: break; default: ... break; }` | ✅ **K23 ③ 已定** | 关闭 `EUI008` 保留位（停用且保持停用）；`design/09` §9.2 分领域状态随之更新；偏差 D87 |
| `@code { ... }` | ✅ **K21/K23 ④ 已定** | 类成员（字段/属性/方法）；**位置不限**、可多个块、书写顺序不影响可见性（K21）；与 code-behind 并存（K23 ④）；`@functions` 别名按 Q4 待定 |
| `@{ ... }` | ⚠️ 待 Q1/Q3 | Razor 的代码块（局部变量/语句，与表达式共享作用域）；若采纳需明确与 `{expr}` 的共存规则 |
| `@* ... *@` | ✅ **K23 已定** | 编译器丢弃的注释；未闭合报 `EUI100`（顺带关闭 D12，D88）；`<!-- -->` 弃用期形态待 Q6 |
| `@using Ns` | ✅ **K22/K23 已定** | 导入命名空间；生成代码保留 `using Ns;`；标签只在导入命名空间内解析，内置控件亦须 `@using EclipseUI.Elements`（§5.3 / D85） |
| `@try/catch/finally`、`@lock` | ❌ 本波不做 | 与 UI 结构生成无对应关系；需要时用 `@code` 方法 |

### 3.2 不采纳（Web/HTML 糟粕）

| 类别 | 明确不做 |
|---|---|
| 路由 / 外壳 | `@page`、`@layout`、`@attribute [Route]`、`@model`、`@section`（**`@namespace` 已于 2026-11-11 改判**：作为必填命名空间声明落地，语义与 Razor 的路由/外壳无关，见 [spec/01 §1.5](../spec/01-template-syntax.md)） |
| 渲染位置 / 交互 | `@rendermode`（Static SSR / InteractiveServer / InteractiveWebAssembly / InteractiveAuto）、`@attribute [RenderMode*]` |
| 组件类形状 | `@inherits`、`@implements`、`@attribute`、`@typeparam`（`@typeparam` 同时与 K19 泛型暂缓一致） |
| 服务 / 编译开关 | `@inject`、`@preservewhitespace`、compound `@using (...)`、`@using static`、`@functions` 别名（**注**：类型别名 `@using Alias = Ns.Type` 已由 K31 放开，并额外具备标签别名效果） |
| HTML 语法债 | `<!-- -->` 作为模板注释、HTML 标签兜底、`@("xxx")` 字面量、属性大小写不敏感、最小化属性、XML 实体 / CDATA、`@attributes` 展开、CSS `class`/`style` 语义 |
| 运行时 | 渲染树 diff、`RenderFragment`、`EventCallback`、DI、SSR/WASM、CSS isolation |

### 3.3 保持（EclipseUI 自有资产，不动）

`{expr}` 单花括号表达式与引号字面量规则、`@bind-P`、`Key`/`Ref`、`On*` 事件、`Class`/`Style`/附加属性保留位（G4）、具名插槽（C6/K10）、`EUI001`–`EUI045` 诊断体系、span 型 `#line` 映射（D79）、逐文件增量生成（D81）、目录级诊断出口（D81）、`EUI024` 签名探针（D82）、K1–K18 运行时模型、K19 泛型暂缓、K20 决策权威位置。

> 注意：用户点名「`@page`、`@rendermode` 之类的不需要」已由 **K23 ②** 冻结为「永久不提供」清单（见 `spec/01` §1.5）；
> `@using` 已由 **K22** 定为合法指令，`@implements`/`@inherits`/`@attribute` 亦由 K23 ② 定为不做。3.2 整表已生效。

## 4. 分阶段实施计划

依赖：`P0 → P1 → P2 → {P3, P4} → P5 → P6`；P3、P4 共享 `Emitter.cs`，建议 P3 先行。

### P0 · 决策冻结（闸门，文档先行）— ✅ 已完成 2026-11-08

- ✅ `spec/02` §14 登记 **K23**（K1–K23），汇总本轮 7 项决定；K21/K22 此前已登记；实施细节从 **K24** 起。
- ✅ `spec/01` 新增 **§1.5 目标语法**并改写 §1.1/§1.2/§1.4/§2.7/§2.9/§3.1/§6.2/§7.1/§7.2/§8；
  `spec/02` §2.2/§2.3/§2.5/§4.14/§12/§13/§14；`spec/03` §4.4/§13.3；
  `design/01`/`02`/`03`/`08`/`09`/`14`/`15`/`16`；`README`、`spec/README`、`design/README` 全部同步。
- ✅ 双台账新增 **D85–D88**（规范先行，目标阶段 S6），标题 D42–D88 与全仓锚点 `d42d88` 已改；里程碑归属 = **S6**（K23 ⑦，G3 之后 / G4 之前）。
- ✅ `spec/01` §8 新增规则 8「决策与实施分离」，`spec/README` 权威关系新增第 7 条；完成度百分比维持 85–90% / 55–60% / 12–17% 不上调。
- **验收现状**：全仓「`@code` 被拒绝」「不复用 Razor 语法层」表述均已加注目标口径；
  §8 规则 1「先登记状态与诊断，再改 Parser/Analyzer/Emitter」已满足；`src/` 仍一行未动。
- **遗留**：Q3/Q4/Q6/Q7 待拍板细项须在进入 P1 前登记 K24+，不影响已定项。

### P1 · 词法层 — ✅ 已完成 2026-11-08

- `Lexer.ParseDirective`（L303–349）扩展指令表与续接形态：`else`/`else if` 无 `@` 前缀的续接、`@code`/`@{}` 块体捕获、`@switch` 的 `case`/`default` 头、`@* *@` 注释；**`@code` 的识别不得依赖文件开头，token 流任意位置都要进入块体捕获（K21）**；**`@using` 进指令表、从 `EUI009` 拒绝清单移除，解析为 `UiUsingDirective`（K22）**。
- 关闭 **D12**：未闭合注释报 `EUI100`，不再吞到 EOF（`SkipComment` L188–192）。
- ✅ **D11 已关账（S6）**：`CaptureExpression` 改为 `CSharpSnippet.FindMatchingClose`——按 C# 词法跳过注释、
  字符字面量、普通/verbatim/插值/raw 字符串（含插值洞递归、`{{`/`}}`、`""` 转义、`:F2` 格式符）；
  `@code` 块体与指令参数复用同一扫描器。**不引入 Roslyn**（前端保持可独立测试），
  口径写进 `spec/01` §2.6 与 `design/03` §3.5.4 D11 行，回归见 `ExpressionCaptureTests` 13 例。
- **验收**：词法 golden、`TokenizerTests` 扩展、D12 关闭；`@code { }` 不再级联产生 `EUI009` + `EUI006`。

### P2 · AST 与分析器 — ✅ 已完成 2026-11-08

- `Ast.cs` 新增 `UiCodeBlock`、`UiUsingDirective`、分支续接节点、`UiSwitch`、`UiLoop`（视 Q3）；**`UiDocument.Roots` 允许 `@code`/`@using` 与单根元素任意交错**，`EUI006`/`EUI012` 改为「元素根节点恰好 1 个 + 顶层非元素节点仅限 `@code`/`@using`/注释」（K21/K22）。
- `Parser.ParseDirectiveInto`（L268–291）分派表重写；`Analyzer.Analyze`（L15–24）放宽根层 `EUI006` 例外；保留「块内容只允许元素与嵌套块」边界。
- 诊断：`EUI008` 停用、`EUI009` 收窄到明确拒绝清单；新增诊断从 **EUI046** 起。
- **验收**：解析/诊断快照；补齐当前缺失的 `EUI008`/`EUI009`/注释/`@code` 专门用例（全仓目前 `EUI008` 零断言、`EUI009` 仅一处）。

### P3 · 生成器 · 单文件代码（风险最高）— ✅ 已完成 2026-11-08

- `Emitter.Emit`（L77–128）类壳增加 `@code` 成员透传，并让 `Mapped`（L146–162）为 `@code` 内成员/语句建立 `.ui` span 映射。
- `TemplateGenerator.ExtractFacts`（L62–120）放宽 `EUI005`；`ProbeCodeBehindConflicts`（L215）扩展到 `@code` 成员；`IsGeneratedMemberName`（L254）白名单不变。
- **`TemplateProbe.ProbeTree`（L742–770）必须把 `@code` 成员所在语法树加入探针 `Compilation`**，否则 `InferType`/`IsWritableSource`/`IsRefTarget`/`IsHandlerCompatible` 全部看不到新成员，`EUI007`/`EUI037`/`EUI029`/`EUI024` 会集体误报（R1）。**K21 追加约束：先遍历整份文档收集全部 `@code` 成员，再生成类壳与探针 Compilation**，保证后置块成员对前置表达式同样可见；`EUI045` 与 `#line` 按各块实际 span 处理。**K22 追加**：`UiCatalog.Resolve` 按 `@using` 导入集合过滤，`EUI001` 建议补「缺失的 @using」，同名歧义仍 `EUI016`；文件头按模板顺序输出 using 行。
- 增量管道 `WithTrackingName("PerFileGeneration")` 保持；`spec/02` §2.4 `#line` 契约「必须映射 / 不映射」清单重写。
- **验收**：`GeneratorContractTests`（约 94 例 golden）、`LineSpanMappingTests`、`CodeBehindConflictTests`（23 例）、`EventHandlerSignatureTests`（16 例）、`MetadataOutletTests`（14 例）全绿；新增「无 `.cs`、纯 `@code` 单文件」端到端用例。

### P4 · 流程控制 · 运行时语义 — ✅ 已完成 2026-11-08

- `ConditionalBlock` 扩为多分支（`else if`/`else`）；新增 `SwitchBlock`；按 Q3 决定是否扩到 `@for`/`@while`/`@do while`。
- 定义分支切换时的销毁/重建、循环次数变化时的 Keyed 复用边界；K4/K6（块 setter 只保存、显式 `Refresh()`）不变。
- `Emitter.EmitIf`（L783–833）/ `EmitForeach`（L835–894）与 `EmitContentRoots`（L896–917）同步；组件内容区仍按 K10 build-once 静态展开。
- **验收**：`BlockTests`/`BlockLayoutTests`/`EndToEndTests` 扩展分支切换与循环变化用例，验证无元素泄漏、无重复失效。

### P5 · 诊断与错误恢复 — ✅ 已完成 2026-11-08

- `EUI008` 停用后保持停用（§8 规则 4）；新诊断码从 `EUI046` 起分配：`@code` 成员冲突、`switch` 缺 `default`、无界循环提示、未知 `@` 指令清单提示。
- 非法输入仍走 K16 最小 `Build` 回退，不抛异常。
- 按 Q7 决定是否同步清掉 `P:bind`/`P:value`（`EUI043` → Error/移除），顺带收 S5 旧语法清理。
- **验收**：诊断快照、零停用诊断出现、`spec/01` §6.2 出口表与 `spec/02` §12 台账一致。

### P6 · 收口与迁移 — ✅ 已完成 2026-11-08

- 样例：`CounterApp`/`TaskList` 至少一个改为单文件 `@code` 形态，其余保留 code-behind 以验证并存；**至少一个样例把 `@code` 放在模板下方**，验证「上引用下」与多块合并（K21）；**全部 `.ui` 样例、约 94 例 golden 与块/诊断测试模板统一补 `@using`（K22，一次性迁移 + 回归）**。
- 文档双向同步（spec ↔ design ↔ README）、`design/09` 里程碑进度、`spec/README`/`design/README` 边界声明。
- ✅ 台账关账（2026-11-08）：D85–D88 全部 ✅、D12 ✅ S6、D11 ✅ S6、K21/K22/K23 ✅ S6；`EUI043`/`EUI008` 停用码零出现。
- **验收**：`dotnet test EclipseUI.slnx` 全绿（基线 497 例：Headless 214 / Compiler 218 / Rendering 65）；golden 生成文本、诊断快照、样本项目编译与运行（`spec/01` §8 规则 5）。

## 5. 决策状态与待拍板细项（Q1–Q8）

| # | 问题 | 状态 | 待办 |
|---|---|---|---|
| **Q1** | 表达式记号 | ✅ **K23 ⑥ 已定**：保留 `{expr}` 作工作基线，`@` 只用于指令 / 代码块 / 注释 | 若要改用 Razor 记号，须先修订 K23 并走弃用期（§5.1 记录代价） |
| **Q2** | 单文件与 code-behind | ✅ **K23 ④ 已定**：并存；无 `.cs` 时允许纯 `@code`（放宽 `EUI005`） | 无 |
| **Q3** | 流程控制范围 | ✅ **K24 已拍板并实施**：`@for`/`@while`/`@do` 不提供，落 `EUI009` 并给 `@foreach` + `Enumerable.Range` 改写建议；循环次数变化的复用语义不做（R2 规避） | 未来有真实需求须按新 K 登记 |
| **Q4** | `@code` 允许内容 | ✅ **K25 已拍板并实施**：原文透传的类成员区域——嵌套类型允许、块内 using 不允许（C# 兜底）、空块合法、`EUI045` 只扩到生成保留名冲突 | 无 |
| **Q5** | 三项类形状指令 | ✅ **K23 ② 已定**：`@implements`/`@inherits`/`@attribute` 一律不做，类形状交 `.cs` partial | 无 |
| **Q5-`@using`** | 模板 `@using` | ✅ **K22 已定**：支持、内置控件也必须导入、生成代码保留 using（§5.3 / D85） | 无 |
| **Q6** | 注释迁移 | ✅ **K26 已拍板并实施**：`<!-- -->` 永久保留为与 `@* *@` 等价的合法注释（口径 C），不设弃用期；两种注释未闭合统一报 `EUI100`（D12 关闭） | 无 |
| **Q7** | 旧语法清理 | ✅ **K27 已拍板并实施**（口径 B）：`P:bind`/`P:value` 本波移除，`EUI043` 停用零出现，新增 `EUI046`（Error） | 无 |
| **Q8** | 排期归属 | ✅ **K23 ⑦ 已定**：新增 S6「语法对齐」，插在 G3 之后、G4 之前（design/09 §9.5/§9.6） | 若要调整位置须修订 K23 |

> 口径：✅ 已定项以 `spec/02` §14（K21–K29）与 `spec/01` §1.5 为准。
> **2026-11-08 更新**：Q3/Q4/Q6/Q7 与 case 内嵌细项已全部拍板（**K24–K30**）并随 S6 实施完成——
> `@switch` 细节另见 K29（分支体 = 完整内容模板、case 标签常量校验 `EUI047`、无贯穿但要求 `break`、`default` 至多一个、`EqualityComparer<T>.Default` 匹配）；`@{}` 代码块不采纳（K28）。

### 5.1 Q1 论证：为什么推荐保留 `{expr}`

依据 Microsoft Learn · ASP.NET Core 10.0《Razor syntax reference》与《Blazor components》核实（2026-11-08）。

**Razor 里表达式到底怎么写**

| 场景 | Razor 写法 | 规则 / 坑 |
|---|---|---|
| 文本插值（隐式） | `<p>@DateTime.Now</p>` | **不能含空格**（`await` 例外）；`@DateTime.Now - TimeSpan.FromDays(7)` 只求值前半段，后半段按字面文本输出 |
| 泛型 | `@GenericMethod<int>()` **不合法** | `<int>` 被当 HTML 标签，报「元素未闭合」或方法组转换错误；必须写 `@(GenericMethod<int>())` |
| 显式表达式 | `@(DateTime.Now - TimeSpan.FromDays(7))`、`Age@(joe.Age)` | 平衡括号包住整个表达式；不加括号时 `Age@joe.Age` 被当 email 原样输出 |
| 字面 `@` | `@@handle` 或 `@("@handle")` | email 地址（`Support@contoso.com`）不触发切换，属特例 |
| 代码块 | `@{ var x = ...; }` | 默认语言是 C#，可隐式转回 HTML，整行转回要 `@:`；官方文档明言「多余的 `@` 会导致后续语句出现难以理解的编译错误」 |
| 保留字 | `@(functions)`、C# Razor 关键字需 `@(@case)` | 双重转义 |
| 组件参数 | `Title="count"`（string 参数传字面量）、`Title="@title"`（传表达式）、`Count="count"`（非 string 参数可省略 `@`） | **值是否 C# 取决于参数类型 + 有无 `@`**；`Title="Set by @(x)"` 混合内容非法，必须整体是一个 C# 表达式 |
| 输出 | 表达式结果自动 HTML 编码 | `IHtmlContent`/`Html.Raw` 另行处理 |

**判断：`@` 不是「表达式记号」，而是「HTML → C# 的切换开关」**。`@expr`、`@()`、`@@`、`@{}`、`@:`、隐式转换、双重转义、HTML 编码这一整套，都是因为模板的宿主语言是 HTML 才存在的。`design/01` §8 把「字面量属性必须 `@("xxx")`」「HTML 标签兜底」列为语法债，正是这一套的产物。因此按用户「取其精华去其 HTML 糟粕」的划分，`@if`/`else`/`@switch`/`@code` 是**组织形态**（精华），`@expr`/`@()`/`@@`/`@:` 是**过渡机制**（糟粕）。

**保留 `{expr}` 的六条理由**

1. **`@` 已被指令占满**：对齐后 `@` 要承担 `@if`/`@foreach`/`else`/`@switch`/`@code`/`@* *@`/`@bind-`。再引入隐式 `@expr`，词法上「`@` 后跟标识符」究竟是指令、C# 标识符还是转义，只能靠名字表 + 上下文猜；Razor 靠 HTML 标签/属性位置与保留字表消歧，这里没有 HTML 层，`EUI009`「未知指令」的边界也会被迫放宽到「排除所有可能的 C# 标识符」。
2. **引号语义相反，会推翻冻结线**：Blazor 中非 string 参数 `Count="count"` 是 C# 表达式；EclipseUI 中 `Label="{x}"` 永远是字符串字面量（`spec/01` §2.5 L195、§1.4 冻结线 11）。改成 `@expr` 必须改这条已实现语义，按 §8 规则 2 要走「新语法 + 弃用期」，代价远高于收益；而 `P="..."` 原生字面量正是为了消灭 `@("xxx")` 债。
3. **隐式表达式的三个坑，`{expr}` 已经解决**：平衡大括号 + 字符串感知捕获（`Lexer.cs` L258–290）天然支持空格、lambda 块体、嵌套 `{}`、泛型 `<...>`，`>` 不截断（`Lambda_WithGt_IsNotTruncated` 有回归用例）。引入 `@expr` 等于把「什么时候必须加 `@()`」重新引回来，还附带 `@@` / `@:` / 双重转义。
4. **`@code` 与表达式边界清晰**：`{expr}` 只放表达式不放语句/声明（`spec/01` §2.6 L203），`@code` 只放类成员，不需要 Razor 那种「C# ↔ 标记」状态机与隐式转换规则。
5. **生成契约成本**：`{expr}` 的 span 直接驱动 span 型 `#line`（D79）、表达式原样输出不重写（K15）、插值用 `{(expr)}` 规避格式符（K12）。改成 `@expr` 要重做捕获边界、`UiSourceLocation`、`Emitter.Mapped`、约 94 例 `GeneratorContractTests` golden 与 5 例 `LineSpanMappingTests`，且 D11 扫描器口径要重定，纯成本无功能收益。
6. **`@bind-` 前缀冲突**：现有 `@bind-Checked={...}` 已把 `@` 用在属性位；Razor 的 `@bind` 是指令属性、靠 HTML 属性位置识别，这里文本位写 `@bind` 会报 `EUI009`。加隐式表达式后这条分流更脆。

**代价（诚实列出）**：从 Razor 转来的人要改肌肉记忆；对齐后 `@` 的角色仍比 Blazor 窄，「流程控制对齐」不等于「表达式记号对齐」；字面 `{`/`}` 仍要写 `{"{"}`/`{"}"}`；`{price:F2}` 格式符与 verbatim/raw string 仍受 D11 限制（与本次改造无关，可独立关账）。

**备选方案（若用户要求更对齐）**

- **Q1-B（折中）**：保留 `{expr}` 为主，额外接受 `@(expr)` 作为显式表达式别名，**不引入隐式 `@expr`**。降低迁移门槛，引号二分不变，但要新增一条诊断与 golden 覆盖。
- **Q1-C（全面对齐）**：引入隐式 `@expr` + `@()` + `@@` + `@:`。需改写 `spec/01` §1.4 冻结线 11、§2.5/§2.6/§2.7/§2.8/§3.1/§6.2/§8，重做 `Lexer.ParseDirective`/`CaptureExpression`、`Parser`、`Emitter.Mapped` 与 `$"..."` 插值生成（K12/K15），重定 `EUI009` 边界，并给 `P="..."` 旧写法一个弃用期。**结论：语法更像 Blazor，但会把 `@()`/`@@`/`@:`/双重转义这套 HTML 过渡机制重新引回来，与「去 HTML 糟粕」目标相冲突。**

> 状态：本节为 Q1 的**理由记录**；Q1 已按推荐口径 A 登记为 **K23 ⑥ 工作基线**（保留 `{expr}`）。
> 若要改用 Q1-B/Q1-C，须先修订 K23 并同步 `spec/01` §1.5 与偏差台账。

### 5.2 `@code` 位置与成员可见性（用户 2026-11-08 明确要求，已登记 K21）

**核实结论**（Microsoft Learn · ASP.NET Core 10.0）：

- Blazor 组件文档的标准 `Counter` 模板把 `@code` 放在所有 markup **之后**；`Markup.razor` 示例中
  `<h2 ...>@headingText</h2>` 引用的是**下方** `@code` 块里的字段；原文写明
  「Members of the component class are defined in one or more `@code` blocks」。
- Razor 语法参考 `@code` 条目：「For Razor components, `@code` is an alias of `@functions` and
  recommended over `@functions`. **More than one `@code` block is permissible.**」；
  `@functions` 的生成类示例显示成员被放进生成的类，与 `ExecuteAsync()` 的先后无关。
- 官方文档**没有**「`@code` 必须在文件顶部」这类约束；顶部只是 directive 的排序惯例
  （「By convention, a component's directives at the top ... placed in a consistent order」）。

**采纳口径（K21）**：

| 项 | 决定 |
|---|---|
| 位置 | 文档顶层**任意位置**：根元素之前、之后、或前后各一个；示例置顶或收尾都只是排版习惯（附录 A 取收尾） |
| 数量 | 允许多个 `@code` 块，成员全部合并进同一个 partial 类 |
| 可见性 | **书写顺序不影响**：上面的模板表达式可引用下面 `@code` 声明的字段/方法，反之亦然 |
| 边界 | `@code` 仍是组件级类成员声明，**不进入元素内容**（保持 P2「块内容只允许元素与嵌套块」）；不引入 `@functions` 别名 |
| 现状 | K21 位置口径 + K23 ④ 单文件口径均已登记；`spec/01` §1.5 已写目标语法、§1.4/§2.7/§3.1 已加注「当前实现」；**代码仍拒绝 `@code`（D86）** |

**由此新增的实现约束**（已并入 P1/P2/P3/P6）：

1. `Lexer`：不能假定 `@code` 出现在文件开头，token 流任意位置都要进入块体捕获。
2. `Parser`/`Analyzer`：`UiDocument.Roots` 允许 `@code` 与单根元素任意交错；`EUI006`/`EUI012`
   改为「元素根节点恰好 1 个 + 顶层非元素节点仅限 `@code`/注释」。
3. `Emitter`/`TemplateProbe`：**先全量收集所有 `@code` 成员**，再生成类壳、再建探针 `Compilation`；
   否则「模板在上、`@code` 在下」会让前置 `{expr}` 误报 `EUI007`/`EUI024`（R1 的直接延伸）。
   `EUI045` 冲突检测与 `#line` span 按各块实际位置处理（R3/D79）。
4. 样例：至少一个把 `@code` 放底部，验证「上引用下」与「多块合并」。

> 完整单文件示例见本文 **附录 A**（按 K21 重写：`@code` 收尾、模板在上）。

写法示例（同一界面，代码放下面）：

```razor
<StackPanel Gap="8">
    <TextBlock>{_title}</TextBlock>
    <Button OnClick={Increment}>已点击 {_count} 次</Button>
</StackPanel>

@code {
    private string _title = "计数器";
    private int _count;
    private void Increment() => _count++;   // 事件包装自动 Invalidate()
}
```

### 5.3 `@using` 与标签作用域（用户 2026-11-08 明确要求，已登记 K22）

用户口径：「模板标签支持 `@using` 指令，即使是内置组件也必须 `@using`，生成代码中保留 using 指令」。

**冲突点**：3.2 原把 `@using` 列入「不做」、Q5 原推荐「一律不做，交项目级 global using」；
`spec/02` §2.2 L155「不得生成任何 using 指令」、§2.5 禁止项表列 `using / global using`（两处现已按 K22 改写）；
`spec/01` §1.2 / §3.1、`spec/03` L367 写「模板不声明 using」；`design/03` §3.2.1 L256 是 v3 历史结论
（按 `spec/README` 第 5 条保留原文、加注取代关系）。现状 `UiCatalog.Resolve`（`UiCatalogModel.cs` L213）
是全局短名字典，与命名空间无关。

**采纳口径（K22）**：

| 项 | 决定 |
|---|---|
| 指令形态 | `@using {Namespace}`（不带分号，与 Razor 一致），支持多条；**不做** compound `@using (...)`、`@using static`。类型别名 `@using Alias = Ns.Type` 在 S6 之后由 **K31** 放开（原文「不做别名 using」仅指当时范围） |
| 生成代码 | 全部 `@using` 在 `#nullable enable` 之后、`namespace` 之前输出为普通 `using {Ns};`；**不生成 `global using`、不生成模板未声明的 using**；其余生成代码的 `global::` 全限定规则不变 |
| 标签解析 | `UiCatalog.Resolve(tag)` 改为按导入命名空间过滤；只有导入命名空间中的控件/组件可被短名解析 |
| 内置控件 | **无豁免**：内置控件在 `EclipseUI.Elements`（`Controls.cs` / `Panels.cs` / `TextBlock.cs`），必须 `@using EclipseUI.Elements` |
| 未导入标签 | 报 `EUI001` 并在建议里给出缺失的 `@using`；同名歧义仍 `EUI016`；不静默降级成别的状态 |
| code-behind | `.cs` 里的 `using` **不参与**标签解析，只认 `.ui` 内的 `@using`（与 Blazor imports 文件不作用于 `.cs` 一致） |
| 位置 | 文档顶层任意位置均可，生成器统一收集并提到文件头；书写顺序不影响（与 K21 一致） |
| `@code` 收益 | `@code` 原文（如 `List<string>`）不再依赖项目级 global using：写 `@using System.Collections.Generic` 即合法 |

**新增实现约束**（并入 P1/P2/P3/P6）：

1. `Lexer`/`Parser`：`@using` 进指令表、从 `EUI009` 拒绝清单移除；解析为 `UiUsingDirective`（命名空间文本 + span）。
2. `Analyzer`/`UiCatalog`：`UiElementDef` 已有 CLR 类型信息，过滤逻辑加导入集合；`EUI001` 建议文本扩展；补「缺 using」快照用例。
3. `Emitter`：文件头按模板顺序输出 using 行；using 行是否做 `#line` 映射由 §2.4 清单补（倾向不映射，它不是用户表达式）。
4. **迁移（P6 大头）**：`samples/CounterApp` 全部 `.ui`、`GeneratorContractTests` 约 94 例 golden、
   块/诊断测试的模板字符串都要补 `@using`；未迁移前 `EUI001` 会大面积失败，须按 §8 规则 2 走「一次性迁移 + 回归」，
   不做静默兼容。
5. 偏差台账：✅ **D85 已登记**「现行实现仍是全局短名目录 / §2.2 旧口径」，S6 实施（P1–P3/P6）时关闭。

**待确认边界**：`@using` 是否允许出现在根元素之后（K21 允许 `@code` 任意位置，但 using 是编译单元级）；
是否接受 imports 式的全局导入文件；`@using` 与 `@code` 内全限定名冲突时的优先级。

## 6. 风险清单

| # | 风险 | 缓解 |
|---|---|---|
| **R1** | 探针语义模型看不到 `@code` 成员 → `EUI007`/`EUI024`/`EUI029`/`EUI037` 集体误报（P3 最高风险） | P3 先做「探针 Compilation 注入」并写专门回归测试，再改类壳 |
| **R2** | Razor 的 `for`/`while` 是「重渲染时重新展开」，EclipseUI 是「块协调器 Keyed 复用」，动态次数变化的生命周期无现成语义 | ✅ K23 ③ 已先收敛到 `else` + `switch`；若要做循环，须在 P0 补写销毁/重建契约并登记 K24+（Q3 待拍板） |
| **R3** | `@code` 内成员/语句的 `#line` span 映射，影响编译错误定位与 IDE 跳转 | 扩展 `LineSpanMappingTests`；`spec/02` §2.4 清单同步重写 |
| **R4** | 文档先行纪律：K20 + `spec/01` §8 规则 1/8 要求先登记再改代码 | ✅ **P0 闸门已完成**（K23 + §1.5 + D85–D88 + 全仓同步）；`src/` 仍一行未动，Q3/Q4/Q6/Q7 待拍板细项不阻塞已定项 |
| **R5** | `GeneratorContractTests` 约 94 例 golden 依赖类壳与 `#line` 形态，改动面大 | 分两步：先加 `@code` 透传（不影响无 `@code` 模板的字节输出），再改诊断 |
| **R6** | 既有架构表述自相矛盾（`design/01/02/08/README`） | P0 一并修订并加注「v3 历史不重写」的边界 |
| **R7** | 测试覆盖缺口：`EUI008` 零断言、`EUI009` 仅一处、`@code`/注释无专门用例 | P2/P6 补齐 |
| **R8** | K19 泛型暂缓与 `@typeparam` 的关系 | 明确 `@typeparam` 属 3.2 不采纳清单，泛型标签仍不排期；K19 不变 |

## 7. 明确不做

- 不引入 Blazor 运行时、渲染树 diff、`EventCallback`、`RenderFragment`。
- 不引入路由、DI、布局、SSR/CSR、CSS isolation、属性展开。
- 不改 K1–K18 的运行时模型、不改增量生成管道与目录级诊断出口。
- 不动 K19（泛型标签暂缓）、不动 K20（决策权威位置）；K21（`@code` 位置）、K22（`@using`）、
  **K23（整波方向与范围）** 均已按 §5.2 / §5.3 / `spec/02` §14 落地。
- **本轮已完成决策与规范正文登记**（K23 / `spec/01` §1.5 / D85–D88 / 全仓同步）；
  **`src/` 下代码仍一行未动**，P1 开工前须先拍板 Q3/Q4/Q6/Q7 并登记 K24+。

---

**状态**：方向已由 **K23** 确认并全仓落地（K1–K31 / D42–D88 / S6 ✅ 2026-11-08）；本文余下内容是**实施过程记录**，
P1–P6 全部走完，Q3/Q4/Q6/Q7 已拍板为 K24–K28、`@switch` 细项为 K29/K30；
后续增量 **K31**（`@using Alias = Ns.Type` 一体两用，2026-11-09）见 spec/02 §14 与 §4.18。
按 K20，权威决策在 `spec/02` §14、目标语法在 `spec/01` §1.5；`.workbuddy/memory/` 只作会话留痕。

---

## 附录 A · 对齐后单文件示例（TaskBoard.ui，按 K21 重写）

> **2026-11-08 重写说明**：由「`@code` 置顶」改为「模板在上、`@code` 收尾」，与 Blazor 标准 `Counter`
> 模板一致。K21 同时允许置顶、置底或前后各一块，这里取最常见的一种；§5.2 有简版与多块变体。
> **本例所用语法自 2026-11-08 起全部可用**（S6 落地：`@code` / `else` / `@switch` / `@* *@` / `@using`），
> 对应偏差 **D85–D88 已关闭**。但它是文档示例、不随 `samples/` 同步验证（曾出现与实现细节不同步的情况），
> 因此仍不得放进会被 Source Generator 扫描的目录，只以 Markdown 代码块留档。
> 按 **K22**，模板头两条 `@using` 分别让内置控件与 `List<string>` 可见，且会原样保留到生成文件。

```razor
@* TaskBoard.ui — K23 目标语法形态，当前编译器（v2）尚不支持 *@
@using EclipseUI.Elements
@using System.Collections.Generic

<StackPanel Gap="8">
    <TextBlock FontSize="30">{_title} · 已点击 {_count} 次</TextBlock>

    @* 编译器丢弃的注释；<!-- --> 对齐后进入弃用期（Q6） *@
    @if (_count == 0) {
        <TextBlock>还没开始</TextBlock>
    } else if (_count < 5) {
        <TextBlock>热身中：还差 {5 - _count} 次</TextBlock>
    } else {
        <TextBlock>达标了</TextBlock>
    }

    @switch (_view) {
        case "list":
            @foreach (var t in _tasks) {
                <ListBoxItem Key={t}>
                    <TextBlock>{t}</TextBlock>
                </ListBoxItem>
            }
            break;
        case "grid":
            <TextBlock>网格视图（G4 之后提供）</TextBlock>
            break;
        default:
            <TextBlock>未知视图</TextBlock>
            break;
    }

    <StackPanel Gap="4">
        <Button OnClick={ShowList}>列表</Button>
        <Button OnClick={ShowGrid}>网格</Button>
        <Button OnClick={AddTask}>加一条</Button>
    </StackPanel>

    @if (_showButton) {
        <Button OnClick={Increment}>+1</Button>
    }
    <CheckBox @bind-Checked={_showButton}>显示 +1 按钮</CheckBox>
</StackPanel>

@code {
    // K21：模板在上、代码在下；上方所有 {expr} / OnClick 都可直接引用这里的成员
    private string _title = "Task Board";
    private int _count;
    private bool _showButton = true;
    private string _view = "list";

    // K22/K23：@using 已定为合法指令，命名空间由模板头 @using 提供
    private List<string> _tasks = new() { "写规范", "写生成器", "写测试" };

    private void Increment() => _count++;                  // 事件包装自动 Invalidate()
    private void AddTask() => _tasks.Add($"任务 {_tasks.Count + 1}");
    private void ShowList() => _view = "list";
    private void ShowGrid() => _view = "grid";
}
```

**逐项说明**：

| 项 | 本例的体现 |
|---|---|
| `@code` 位置（K21） | 单个块置于根元素 `StackPanel` **之后**；上方 `{_title}`、`{_count}`、`{5 - _count}`、`OnClick={Increment}` 全部引用下方声明 |
| 多块变体（K21） | 可拆成「顶部一块放 `_title` + 底部一块放状态与方法」，成员合并进同一个 partial 类；本例取单块 |
| 流程对齐 | `@if` → `else if` → `else`（`else` 不带 `@`）、`@switch` 的 `case`/`break`/`default`、`@foreach` + `Key={}` |
| 注释对齐（K23/D88） | `@* *@`；`<!-- -->` 按 Q6 给弃用期，本例不再使用 |
| 单文件（K23 ④ 已定） | 无同名 `.cs`，状态与方法都在 `.ui`；`EUI005` 放宽为「`@code` 或同名 partial 至少其一」 |
| 保持不动 | `{expr}`、`Gap="8"` 字面量、`@bind-Checked={...}`、`OnClick={}`、单根元素树、`Invalidate()` 由事件包装自动触发 |
| `@using`（K22） | 头两条：`@using EclipseUI.Elements` 让内置控件可见（无豁免），`@using System.Collections.Generic` 让 `@code` 里的 `List<string>` 合法；生成文件保留为 `using ...;` |
| 不出现（K23 ② 已定） | `@page`、`@rendermode`、`@inject`、`@inherits`、`@typeparam`、`@attributes`、`class`/`style` |
| 待定边界 | `case` 内嵌 `@foreach` 属 **P4 待定义**（若不允许则抽成子组件）；`@for`/`@while`/`@do` 待 Q3；`@{}` 待定；`<!-- -->` 弃用期形态待 Q6 |
| 实现注意（P3） | 生成器须**先全量收集 `@code` 成员**再建探针 Compilation，否则本例上方的 `{_title}` 会误报 `EUI007`（R1/K21） |


---

## 附录 B · 生成契约草案（对应附录 A 的 `TaskBoard.ui`）

> 状态：**草案**，随 Q3/Q4/Q6/Q7 / P3 / P4 定稿，不占 K 编号；`@using` 部分已由 **K22** 定稿（§5.3），
> 整波方向由 **K23** 定稿（`spec/02` §14）；规范先行偏差见 D85–D88。
> 依据：`spec/02` §2.1–2.4（文件 / 类壳 / 白名单 / `#line`）、§4.3/4.4/4.6/4.7/4.9/4.10，
> 以及 `Emitter.cs` 实际输出。下方为**示意**：编号、字段提升、目标属性按真实规则；
> `// ──` 讲解注释不属于生成文本（§2.3 禁止生成注释）。

### B.1 文件头、`@code` 透传与类壳

```csharp
// <auto-generated> 由 EclipseUI 模板编译器生成；请勿手动修改。 </auto-generated>
#nullable enable
using EclipseUI.Elements;              // K22：由模板 @using 透传
using System.Collections.Generic;     // K22：让 @code 原文 List<string> 合法

namespace Samples;

internal partial class TaskBoard : global::EclipseUI.Component
{
    // ── @code 成员：原样透传，本例来自模板尾部；K21 顺序无关 ──
#line (33, 5) - (33, 45) "C:\Workspace\EclipseUI\samples\TaskBoard.ui"
    private string _title = "Task Board";
    private int _count;
    private bool _showButton = true;
    private string _view = "list";
    private List<string> _tasks = new() { "写规范", "写生成器", "写测试" };
    private void Increment() => _count++;
    private void AddTask() => _tasks.Add($"任务 {_tasks.Count + 1}");
    private void ShowList() => _view = "list";
    private void ShowGrid() => _view = "grid";
#line hidden

    // ── 生成字段：仅跨方法引用者提升（§2.3）──
    private global::EclipseUI.Elements.TextBlock __el1 = null!;
    private global::EclipseUI.ConditionalBlock __blk2 = null!;
    private global::EclipseUI.SwitchBlock<global::System.String> __blk3 = null!;   // P4 新增
    private global::EclipseUI.ForEachBlock<global::System.String> __blk4 = null!;
    private global::EclipseUI.UiBinding __bind5 = null!;
```

### B.2 `Build()` 关键片段

```csharp
    protected override global::EclipseUI.Element Build()
    {
        var el0 = new global::EclipseUI.Elements.StackPanel();
        el0.Gap = 8;
        __el1 = new global::EclipseUI.Elements.TextBlock();
        el0.Children.Add(__el1);                       // 动态文本由 U0() 写，不在此处求值

        __blk2 = new global::EclipseUI.ConditionalBlock();
        el0.Children.Add(__blk2);
#line (6, 5) - (6, 22) "C:\...\TaskBoard.ui"
        __blk2.AddBranch(() => _count == 0, scope6 => { /* 还没开始 */ });
#line hidden
#line (8, 5) - (8, 27) "C:\...\TaskBoard.ui"
        __blk2.AddBranch(() => _count < 5, scope7 => { /* 热身中：{5 - _count} */ });
#line hidden
        __blk2.SetDefault(scope8 => { /* 达标了 */ });
        Track(__blk2);
        __blk2.Refresh();

        __blk3 = new global::EclipseUI.SwitchBlock<global::System.String>();
        el0.Children.Add(__blk3);
#line (13, 5) - (13, 24) "C:\...\TaskBoard.ui"
        __blk3.SetSelector(() => _view);
#line hidden
        __blk3.AddCase("list", scope9 => { /* @foreach 条目模板 */ });
        __blk3.AddCase("grid", scope10 => { /* 占位文本 */ });
        __blk3.SetDefault(scope11 => { /* 未知视图 */ });
        Track(__blk3);
        __blk3.Refresh();

        __blk4 = new global::EclipseUI.ForEachBlock<global::System.String>();
        el0.Children.Add(__blk4);
        __blk4.SetItems(() => _tasks);
        __blk4.SetKeySelector((global::System.String t) => (object?)(t));
        __blk4.SetTemplate((global::System.String t, global::EclipseUI.BlockScope scope12) =>
        {
            var el13 = new global::EclipseUI.Elements.ListBoxItem();
            /* ... */
            scope12.AddRoot(el13);
        });
        Track(__blk4);
        __blk4.Refresh();

        var btn14 = new global::EclipseUI.Elements.Button();
#line (30, 25) - (30, 34) "C:\...\TaskBoard.ui"
        var h15 = (global::System.Action)(Increment);
#line hidden
        btn14.AddHandler(global::EclipseUI.Elements.Button.ClickEvent,
            () => { h15(); Invalidate(); });          // 事件包装自动失效所属组件
        el0.Children.Add(btn14);

        var chk16 = new global::EclipseUI.Elements.CheckBox();
        __bind5 = global::EclipseUI.UiBinding.TwoWay(
            chk16, global::EclipseUI.Elements.CheckBox.CheckedProperty,
            readTarget: () => chk16.Checked, writeTarget: v => chk16.Checked = v,
            readSource: () => _showButton, writeSource: v => _showButton = v,
            invalidate: Invalidate);
        Track(__bind5);
        el0.Children.Add(chk16);
        return el0;
    }

    protected override void Render()                  // 只调用 U/Refresh，不构造元素
    {
        U0();
        __bind5.Refresh();
        __blk2.Refresh();
        __blk3.Refresh();
        __blk4.Refresh();
    }

    private void U0()
    {
#line (2, 26) - (2, 60) "C:\...\TaskBoard.ui"
        __el1.Label = $"{(_title)} · 已点击 {(_count)} 次";   // K12：{(expr)} 防格式符
#line hidden
    }
}
```

### B.3 必须处理的契约冲突

| 点 | 现状（`spec/02`） | 草案 | 归属 |
|---|---|---|---|
| 生成成员白名单 | 仅字段 / `Build` / `Render` / `U{N}` 四类 | 增加「`@code` 透传成员」一类，**原文不改** | C4 / P3 |
| `global::` 与 using（**K22 已定**） | 生成代码不得有 using，具名类型一律 `global::` | **模板 `@using` 透传为生成文件的普通 `using Ns;`**；`@code` 原文不改写，由这些 using 提供命名空间；其余生成代码仍全限定 | K22 / §5.3 |
| 标签解析作用域（**K22 已定**） | `UiCatalog.Resolve` 全局短名字典，与命名空间无关 | 按导入命名空间过滤；未导入报 `EUI001` + 缺 using 建议，歧义仍 `EUI016`；内置控件无豁免 | K22 / §5.3 |
| `#line` | 含用户表达式/字面量的语句做 span 映射 | `@code` 整段（或逐成员）映射回 `.ui`，`R{N}`/`U{N}` 不变 | R3 / D79 |
| 语义探针 | 只看 code-behind 声明 | **合成一个只含 `@code` 成员的 partial 类语法树**加入 `ProbeTree` 的 Compilation；生成器不得读自己的输出（§2.1） | R1 / K21 / P3 |
| 冲突守护 | `EUI045` 只守 code-behind | 扩到 `@code` 成员与生成保留名（`Build`/`Render`/`U*`/`__el*` 等），新诊断从 `EUI046` 起 | C6 / P5 |
| 分支块 | `SetCondition` + `SetContent` 单分支 | `AddBranch(...)` + `SetDefault(...)`；`Refresh()` 取首个为真分支 | **P4 待定** |
| switch 块 | 无 | `SwitchBlock<T>` + `SetSelector` / `AddCase` / `SetDefault`；常量比较用 `EqualityComparer<T>.Default` | **P4 待定** |
| 输出文件数 | 一个 `.ui` → 恰好一个 `partial class` / 一个 hint name | **仍是一个**，不另出 `{Name}.code.g.cs`（避免违反 §2.1 与增量确定性） | §2.1 |

> **K22 后**：`@code` 原文里的 `List<string>` 由模板头 `@using System.Collections.Generic` 解析，不再依赖项目级 global using；标签则全部（含内置）由 `@using EclipseUI.Elements` 提供作用域。

> `@code` 成员进的是**生成文件**，不是另开 partial 文件：C# 编译器看到同一个类，所以模板在上、代码在下天然可见（K21）；
> 但生成器做 `InferType` / `IsWritableSource` / `IsHandlerCompatible` 判定时生成文件尚未编译，
> 必须靠探针里的合成语法树，这正是 R1 最容易踩空的地方。

