# 3. 模板引擎（语言规范）

> **v4 换轨（2026-10-29）**：模板反应式改为**组件级失效重算**——`Build()` 构建保留元素树并完成首次求值，
> `Invalidate()` 标记脏，宿主在 Measure 前 `ProcessInvalidations()` 重算根级动态绑定，`@if`/`@foreach` 走显式 `Refresh()`；
> 模板状态是普通 CLR 成员，无 `[UiState]`/`Signal<T>`/依赖分析。
> 规范正本：[spec/01](../spec/01-template-syntax.md) 语法、[spec/02](../spec/02-generated-code-contract.md) 生成形态、
> [spec/03](../spec/03-reactivity-and-binding.md) 反应式。本文 §3.1–§3.6（§3.5.4 除外）、附录 A 为 v3 历史；
> §3.5.3 的 D1–D41 是 v3 代码差距台账，v4 新增差距见 [§3.5.4](#354-v4-实现偏差台账d42d88)。与 spec 冲突时以 spec 为准。
>
> 状态：v3 定稿（2026-10-29；v4 换轨后转为历史记录）。
> 本文定义 `.ui` 语言的**词法、句法、语义、字面量与编译管线**，是模板作者、分析器与生成器共同遵守的语法规范。
> 控件与组件如何向模板暴露标签/属性/事件/内容模型，见 [14 控件与模板契约](./14-control-template-contract.md)；
> 反应式语义与运行时契约见 [spec/03](../spec/03-reactivity-and-binding.md)（[15](./15-state-and-reactivity.md) 已停止，仅存历史）；
> 布局与输入见 [05](./05-layout-rendering-input.md)；调试映射与热重载见 [07](./07-toolchain.md)。
>
> **权威关系**：语法（词法/句法/诊断触发点）以 [spec/01](../spec/01-template-syntax.md) 为准，
> 生成形态以 [spec/02](../spec/02-generated-code-contract.md) 为准；反应式语义以 [spec/03](../spec/03-reactivity-and-binding.md) 为准。
> 本文 §3.1–§3.6（§3.5.4 除外）转为历史/架构说明；与 `spec/` 冲突时以 `spec/` 为准。
>
> **偏差规则**：规范与当前代码的差异只允许出现在 [§3.5.3](#353-当前实现偏差s1s5-收敛清单)（v3 台账）与
> [§3.5.4](#354-v4-实现偏差台账d42d88)（v4 台账）；两表之外一律以 `spec/` 为准，代码按表中映射的阶段收敛。
> 不允许"文档写一套、代码静默另一套"。
>
> **规范迁移（2026-10-29）**：词法/句法/语义/诊断触发点的规范正本已迁至 [spec/01](../spec/01-template-syntax.md)，
> §3.6 编译管线与生成形态的规范正本已迁至 [spec/02](../spec/02-generated-code-contract.md)。
> 本文 §3.1–§3.6（§3.5.4 除外）转为架构说明与历史记录；冲突时以 `spec/` 为准。§3.5.3/§3.5.4 的 D 台账与附录 A 仍是代码差距的活记录。

## 3.0 定位、状态标记与冻结线

### 3.0.1 本文管什么

| 本文定义 | 本文不定义（去哪看） |
|---|---|
| `.ui` 的词法、EBNF、元素/属性/文本/指令的句法 | 控件作者如何声明标签、属性、事件、内容模型（[14](./14-control-template-contract.md)） |
| 属性值的语法形态、求值时机与诊断触发点 | 状态模型、失效队列、`Render`/`UiBinding` 运行时（[spec/03](../spec/03-reactivity-and-binding.md)；[15](./15-state-and-reactivity.md) 已停止） |
| 表达式作用域、`@if`/`@foreach`/`Key` 的语法语义 | 布局/渲染/hit test（[05](./05-layout-rendering-input.md)） |
| 字面量到 C# 的编译期转换 | 调试映射、热重载、DevTools（[07](./07-toolchain.md)） |
| 语法项状态、诊断码映射、生成代码形态 | `StyledProperty`/`RoutedEvent`/`Signal<T>` 的运行时实现（[04](./04-runtime-and-elements.md)） |

R1–R10 是运行时/生成器不变量（编译期不执行用户程序集、生成代码只用公开 API、确定性输出、所有权与释放等）：
R1–R10 见 [spec/02 §1](../spec/02-generated-code-contract.md#1-生成器不变量-r1r10)；v3 的 R1–R9 见 [14 §14.3](./14-control-template-contract.md) 与
[15 §15.5.7](./15-state-and-reactivity.md)（历史）。本文引用时不重复展开。

### 3.0.2 状态标记

本文对每个语法项标注实现状态；**规范本身对目标状态生效**，代码可以暂时落后，但落后项必须出现在 §3.5.3（v3）或 §3.5.4（v4）。

| 标记 | 含义 | 使用者预期 |
|---|---|---|
| ✅ 已实现 | 当前生成器可用，行为与本文一致（§3.5.3 列出的偏差除外） | 可直接写进项目 |
| S1–S5 | 状态/反应式语义已冻结，按 [spec/03 §13](../spec/03-reactivity-and-binding.md) 分阶段实现 | 现在写会得到表中指定的诊断（或 §3.5.3/§3.5.4 的临时诊断），不会"看似可用、运行时才炸" |
| G3 | 语义已冻结，排在 G3 实现（非反应式项） | 同上 |
| G4 | 语法/形状已冻结，排在 G4 | 同上；不要用未知标签/属性兜底 |
| ⛔ 保留/拒绝 | 语法位已占用，永不按未知标签/属性回退 | 必须得到指定诊断 |

约束词：**必须** = 违反即诊断；**不得** = 生成器/分析器禁止；**应当** = 诊断可暂缺，但评审必须记录。

### 3.0.3 冻结线

以下决定已经冻结，后续只能增量扩展，不得改义：

1. **一个 `.ui` = 一个类 = 一个根元素**。文件名即类名；code-behind 是唯一状态载体；模板不是独立的函数/模块。
2. **模板作者只写普通 CLR 成员**：页面/组件状态就是 code-behind 的字段/自动属性/普通类；
   不要求 `[UiState]`、`Signal<T>`、INPC/INCC；模板里写 `Count`，不写 `.Value`（[spec/01 §1.4](../spec/01-template-syntax.md#14-冻结线)）。
3. **反应式单位是组件渲染**：`Invalidate()` → `Render()` 重算该组件的动态表达式；不存在按路径/按信号的定向订阅
   （[spec/03 §3–§4](../spec/03-reactivity-and-binding.md)）。
4. **`P={expr}` 与文本 `{expr}` 每次渲染重算**：控件属性直接赋值；组件参数带相等保护推送并失效子组件；
   不做依赖分析，不因方法调用/索引器/lambda 退化（spec/01 §4.4/§4.9）。
5. **`@bind-P={expr}` 是双向绑定**：源必须是可写左值；控件目标走 `UiBinding`，组件目标要求 `{P}Changed`；
   初始方向源 → 目标（[spec/03 §8](../spec/03-reactivity-and-binding.md)）。
6. **`P:bind` / `P:value` 是旧语法**：分别按 `@bind-P` / `P={expr}` 解释并报 `EUI043` Warning，S5 移除。
7. **大小写敏感，不做别名**。`fontSize` ≠ `FontSize`，`key` ≠ `Key`。
8. **表达式就是 C# 表达式**。语言不引入自己的运算符、类型系统或字符串格式语法；`Signal<T>` 不作隐式重写，必须显式 `.Value`。
9. **不兼容 XAML**：不解析 XML 实体，不做属性元素语法，不做 `x:Class`/`Binding` 标记扩展。
10. **未实现的语法必须显式保留并报诊断**（`EUI031` / `EUI029` / `EUI030` / `EUI008`；插槽错误从 G4 起用 `EUI022`），
    不得落进"未知属性/未知标签"的兜底，避免用户在正确语法上收到拼写错误提示。

任何违反冻结线的语法提案，必须先在本文新增状态项与诊断映射，再改代码；不接受"先生效、后补文档"。

### 3.0.4 术语

| 术语 | 含义 |
|---|---|
| 模板 | 一个 `.ui` 文件及其生成的 `Build()` 实现 |
| code-behind | 与 `.ui` 同名的顶层 `partial` 类，承载字段、普通属性/状态、方法、辅助属性 |
| 信号 | `EclipseUI.Signal<T>` 运行时类型；v4 中不是模板反应式机制，读取/写入必须显式 `.Value`，写后需 `Invalidate()`（spec/01 §4.7） |
| 状态属性（v3 术语） | `[UiState]`/`[UiObservable]` 生成的 public partial 属性；v4 已取消，状态就是普通 CLR 成员 |
| 可观察（v3 术语） | 写入时能同步通知订阅者，且分析器可静态识别其写入路径（15 §15.0.2）；v4 无此概念 |
| 可观察路径（v3 术语） | 从 `this`/item 根出发、逐段可观察的成员访问链；v4 无此约束 |
| 依赖（v3 术语） | 表达式对状态属性、`Signal<T>` 成员、可观察路径的读取；v4 由组件级重算取代 |
| 一次性表达式（v3 术语） | v3 中无依赖/不可分析而只求值一次的表达式；v4 所有动态表达式每次渲染重算 |
| 定向更新函数 | 生成器为某个绑定点生成的 `void U{N}()`（根级）或 `void R{N}()`（块内）；重新求值并写目标 |
| 目录 | `UiCatalog`：标签 → 元素/组件定义（属性、事件、内容模型、参数） |
| 内容模型 | `None` / `Children` / `Text` / `TextRuns`，决定子内容如何生成（14.4.4） |
| 块 | `@if` / `@foreach` 生成的 `ConditionalBlock` / `ForEachBlock<T>` |
| 条目 | `@foreach` 的一次模板实例化（`BlockScope`：根元素 + updater 列表 + 作用域订阅） |

## 3.1 词法

### 3.1.1 源文件

- 扩展名 `.ui`，以 `AdditionalFiles` 进入编译；文件内容的编码按 Roslyn 的文本读取（UTF-8）。
- 文件名（去扩展名）必须是合法 C# 标识符，并作为生成类名；否则 `EUI013`。
- 换行支持 LF/CRLF；`#line` 使用 1 基行号。
- 一个文件只包含一个文档；文件级只有注释、空白和一个根元素（§3.2.2）。

### 3.1.2 两种扫描模式

- **文本模式**：标签之外。识别文本、`{expr}`、`@directive`、`<`、`}`、`<!-- -->`。
- **标记模式**：`<` 之后到 `>` / `/>` 之间。识别标签名、属性名、`=`、双引号字符串、`{expr}`；空白（含换行）被忽略。

模式切换只由 `<`、`>`、`/>` 驱动；表达式与指令内部的 `<`/`>` 不切换模式（lambda 的 `=>`、泛型参数不会截断标签）。

### 3.1.3 名字

规范只承认 ASCII 标识符：

```
ident      ::= [A-Za-z_][A-Za-z0-9_]*
tag-name   ::= ident
attr-base  ::= ident ( "." ident )?
attr-name  ::= attr-base ( ":" ident )?
bind-attr  ::= "@bind-" attr-base
```

- 标签名是单段短名：`Button`、`TextBlock`。**不带命名空间、不带泛型实参**；泛型标签是 G4（`EUI030`）。
- 属性名支持 `Owner.Name` 形状仅为附加属性预留（G4，如 `Grid.Row`）；附加属性实现前按未知属性报 `EUI002`（D19）。
- 末尾 `:ident` 是旧修饰符：`bind` / `value` 均已弃用（S3 起报 `EUI043`）；其它修饰符按未知属性处理（`EUI002`）。
- `@bind-P` 是双向绑定的**独立属性形态**：只出现在标记模式的属性名位置，`@bind-` 是字面前缀，`P` 是 `attr-base`；
  它**不是**指令，不参与 `@if`/`@foreach` 的 `@name` 解析（§3.1.7）。
  - 大小写敏感：`@bind-Checked` 合法，`@bind-checked` 解析到未知属性（`EUI002`/`EUI040`）。
  - 后缀带 `:` 修饰符（`@bind-P:bind`）或带空白（`@bind- P`）是语法错误 `EUI100`。
  - 不能绑定保留属性 `Key`/`Ref`/`Class`/`Style`、事件 `On*` 或 `[UiContent]` 参数 → `EUI040`。
- 名字**大小写敏感**；不做命名空间优先级；标签冲突由目录报 `EUI016`（14.5.3）。
- `Key` / `Ref` / `Class` / `Style` 是保留属性名，控件不得声明同名属性（14.4.2）。

实现说明：`Lexer` 的名字字符集比规范更宽（允许以 `.`、`:`、`-` 开头，且允许 Unicode 字母），
这只是扫描器的宽松性，不是语言 API；最终由分析器按目录报 `EUI001`/`EUI002`。规范不保证这些名字的行为。
当前 Lexer 在标记模式把 `@` 当错误 token，`@bind-` 需要新增词法分支（§3.5.3 D24）。

### 3.1.4 记号

| 记号 | 含义 |
|---|---|
| `<` / `</` / `>` / `/>` | 标签开/闭/自闭合 |
| `=` | 属性赋值；两侧可有空白 |
| `"..."` | 属性字面量（仅双引号） |
| `{...}` | C# 表达式（§3.1.6） |
| `@bind-属性名` | 双向绑定属性（仅标记模式属性位置，§3.1.3） |
| `@name` / `@name(...)` | 指令（§3.1.7） |
| `<!-- ... -->` | 注释（§3.1.9） |

标签/属性之间允许任意空白与换行；`<StackPanel>`、`<StackPanel >`、`<StackPanel\n  Gap="8"\n>` 等价。
属性必须有 `=` 和值；`<Button disabled>` 是语法错误 `EUI100`，不是布尔属性简写。

### 3.1.5 属性字面量

- 仅双引号字符串；单引号不是属性定界符。
- 字面量是**原始文本**：不解析 XML 实体（`&amp;` 原样是 `&amp;`），不做空白折叠，可跨行。
- 词法上 `\` 使其后的一个字符不参与"结束引号"判定；**但值保留原始字符，不做反转义**：
  `\"` 在值中是 `\` 和 `"` 两个字符。需要精确的引号/反斜杠时，用 C# 表达式：`P={"a\"b"}`。
- 值是否为空串合法由目标属性决定；`Label=""` 合法。
- **字面量还是表达式由 token 决定，不由内容决定**：带引号的 `P="{x}"` 是字符串字面量 `{x}`，不是表达式。

### 3.1.6 表达式

`{ csharp-expression }`：

- 从 `{` 开始按平衡大括号捕获；`"` 和 `'` 字符串内部的大括号不计数；`\` 转义字符串内的下一个字符。
- 可以包含嵌套 `{}`、lambda 块体、`=>`、泛型 `<...>`；`>` 不会截断表达式。
- 表达式必须是 **C# 表达式**，不是语句/声明。需要复杂逻辑时调用 code-behind 方法或在 code-behind 里准备好值。
- 当前词法限制（由当前扫描器决定，C5 换 Roslyn tokenizer 后消除）：
  - 不支持 verbatim 字符串 `@"..."` 与 raw string `"""..."""`（其中的引号/大括号按普通字符处理，可能提前结束）；
  - 不支持 C# 注释里出现不平衡的 `{`/`}`；
  - 不支持插值格式说明符：`{price:F2}` 会被当作表达式 `price:F2`，不是合法 C#；请写 `{price.ToString("F2")}`。
- 空表达式 `{}`、不完整表达式不在词法层报错，交给生成代码的 C# 编译错误（经 `#line` 指回 `.ui`）。
- 文本/指令里的表达式不做 trim；C# 自行忽略外围空白。

### 3.1.7 指令

- `@name` 后可选 `(...)`；括号按平衡圆括号捕获，字符串感知；指令名只含字母/数字/`_`。
- 已实现：`@if (...)`、`@foreach (...)`。
- `@switch`：保留，`EUI008`；不解析其块体。
- `@bind-属性名` 只在标记模式属性位置识别，不是指令；文本模式写 `@bind` 仍是未知指令 `EUI009`。
- 其它 `@name`：`EUI009`。
- `@` 没有文本转义；要输出字面 `@`，写 `{"@"}`。
- 指令只允许出现在元素内容或块内容中；不能出现在文档根（§3.2.2）。

### 3.1.8 文本与特殊字符

文本模式下的特殊字符：

| 字符 | 行为 | 写字的办法 |
|---|---|---|
| `<` | 开始标签 | `{"<"}` 或整段用表达式 |
| `{` | 开始表达式 | `{"{"}` |
| `}` | 结束块；不在块内则 `EUI100` | `{"}"}` |
| `@` | 开始指令 | `{"@"}` |
| `>`、`&`、`=`、引号 | 普通文本 | 直接写 |

- 没有 CDATA、没有 XML 实体、没有 HTML 标签白名单；任何 `<` 都按标签解析。
- 文本节点可跨行；原始换行/缩进按 §3.1.10 规范化。
- 文本里不能直接写 `<` 做比较；表达式 `{a < b ? "x" : "y"}` 必须在 `{...}` 内。

### 3.1.9 注释

- `<!-- ... -->` 只在文本模式识别；不能写在标签内部。
- 注释不嵌套；内容丢弃，不进入 AST，不是 trivia；相邻文本会拼成同一文本节点（`a<!--c-->b` → `ab`）。
- 未闭合注释报 `EUI100`（✅ S6：`Lexer` 的 `SkipComment`/`SkipHtmlComment` 不再静默吞 EOF，D12 关闭）；`@* ... *@` 为等价合法注释（K26）。
- 注释不参与 pretty-printer（当前没有 pretty-printer，见 §3.6）。

### 3.1.10 空白与规范化

- 标记模式内空白忽略；属性之间、`=` 两侧、`/>` 前均可换行。
- 文本内容在**每一层**（文档、元素、`@if`、`@foreach`）规范化：
  1. 第一个文本节点的首个静态段 `TrimStart`；
  2. 最后一个文本节点的末个静态段 `TrimEnd`；
  3. 纯空白文本节点整体丢弃。
- 中间空白（包括内部换行与缩进）保留；多行动态文本的缩进会进入字符串，需要精确排版时用表达式或 code-behind 方法。
- 规范化的结果是：模板的缩进不会渲染成前导/尾随空格；这与 XAML/JSX 直觉一致（[Parser.cs:299](../src/EclipseUI.Compiler/Parser.cs#L299)）。
- 注释移除发生在规范化之前；`a<!--c-->b` 的两个文本段按中间内容处理，不会被首尾 trim 影响。

## 3.2 句法

### 3.2.1 EBNF

```ebnf
document      ::= node* EOF
node          ::= element | text-run | if-block | foreach-block

element       ::= "<" tag-name attribute* ( "/>" | ">" node* "</" tag-name ">" )
attribute     ::= ( attr-name | bind-attr ) "=" ( string | expression )
attr-base     ::= ident ( "." ident )?
attr-name     ::= attr-base ( ":" ident )?
bind-attr     ::= "@bind-" attr-base
text-run      ::= ( text-char | expression )+

if-block      ::= "@if" "(" csharp-expression ")" "{" node* "}"
foreach-block ::= "@foreach" "(" "var" ident "in" csharp-expression ")" "{" node* "}"

expression    ::= "{" csharp-expression "}"
string        ::= '"' raw-char* '"'
```

说明：

- `text-char` 排除 `<`、`{`、`}`、`@`；注释在词法层删除，不进入 `node*`。
- 结束标签必须与开始标签同名（区分大小写）；自闭合 `<X />` 等价于 `<X></X>`。
- `attribute` 必须有值；不允许只写属性名。
- `bind-attr` 的值**必须**是 `expression`：`@bind-P="true"` 报 `EUI037`（不是字面量转换）。
- 同一元素上 `@bind-P` 与 `P=`/`P:bind`/`P:value` 同时出现 → `EUI033`（同一目标重复赋值）；
  完全相同的属性名重复 → `EUI025`。
- 根层（`document`）在去掉注释/空白后必须恰好是一个 `element`；`text-run` 或块出现在根层是 `EUI006`，
  0 个或多个元素根是 `EUI012`。
- `node*` 的合法组合由内容模型进一步限制（§3.3.5），句法层先接受再语义拒绝。
- 没有 `else`、`else if`、`@switch`、`@code`、`@inherits`、`@using`、`@model`；未知 `@` 指令是 `EUI009`。
  （v3 历史结论，按 `spec/README` 第 5 条保留原文；`@using` 已由 K22 定为合法指令，其余由整波 K23 取代，偏差见 D85–D88。）
- 泛型标签（`DataGrid<Order>`）、附加属性（`Grid.Row`）、插槽标签（`<Slot>`）不在当前句法内；
  分别按 G4 / G4 / G4 保留（§3.3.9）。

### 3.2.2 结构规则

| 规则 | 诊断 | 状态 |
|---|---|---|
| 文档恰好一个根元素 | `EUI012` | ✅ |
| 根层不允许文本或控制流块 | `EUI006` | ✅ |
| 开始/结束标签同名且大小写一致 | `EUI100` | ✅ |
| 属性必须有 `=` 和值 | `EUI100` | ✅ |
| 同一元素重复属性 | `EUI025` | G3（当前未检，§3.5.3 D6） |
| `@if`/`@foreach` 只能出现在 `Children` 内容模型内 | `EUI006` | ✅ |
| 块内容只允许元素与嵌套块，不允许裸文本 | `EUI006` | ✅ |
| `Key` 只能出现在 `@foreach` item 模板的第一个元素根上 | `EUI010` | ✅（`@if` 误放行，D8） |
| `@foreach` 必须写 `var 名字 in ...` | `EUI100` | ✅ |
| `@bind-P` 的值不是 `{expr}` | `EUI037` | S3（当前 `@` 在标记模式报 `EUI100`，D24） |
| `@bind-P` 与同名属性/`:bind`/`:value` 重复 | `EUI033` | S3（当前未检） |
| `@bind-P` 目标为保留属性/事件/不可写/不可观察 | `EUI040` | S3 |
| 注释不写在标签内部 | 标签内的 `<!--` 不是注释，按错误 token 报 `EUI100` | ✅ |

### 3.2.3 内容模型的句法后果

块（`@if`/`@foreach`）的内容**永远按"元素容器"处理**，即使外层是 `Text`/`TextRuns` 模型；
因此：

- `@if`/`@foreach` 里不能直接写裸文本或 `{expr}` 文本，必须先包一个 `TextBlock`；
- 在 `Text`/`TextRuns` 模型里写块直接 `EUI006`；
- 这意味着"条件文本"不能用 `<TextBlock>@if ...</TextBlock>`：把 `@if` 放在容器层控制整个 `TextBlock`，
  或在 `TextBlock` 内写文本表达式（复杂逻辑放 code-behind 方法）。

## 3.3 语义

### 3.3.1 模板类、code-behind 与作用域

- 文件名 → 类名；生成代码是 `{accessibility} partial class {Name} : global::EclipseUI.Component`，
  重写 `protected override Element Build()`（[Emitter.cs:75](../src/EclipseUI.Compiler.Generator/Emitter.cs#L75)）。
- code-behind 必须是与文件同名的**顶层** `partial` 类：
  - 缺失 → `EUI005`；嵌套 → `EUI015`；非 partial → `EUI011`；文件名非法 → `EUI013`。
- 生成部分提供 `Component` 基类；code-behind 不要声明不同的基类，也不要再写 `Build()`。
- code-behind 可标 `[UiState]`（组件自身可观察）或对少量属性标 `[UiObservable]`；状态生成器另生成一个
  `partial` 部分实现 `IUiObservable`（15 §15.2.4）。两者不冲突：模板部分声明基类，状态部分只声明接口。
- 表达式的作用域：

| 位置 | 可见名字 | 重写与订阅 | 订阅归属 |
|---|---|---|---|
| 根元素/根属性 | code-behind 实例成员（`this` 隐式） | 状态属性/可观察路径直接读取；`Signal<T>` 裸名在值上下文取 `.Value`（§3.3.3.4） | 组件 `Track` |
| `@if` 内容 | 同上 | 同上 | 内容订阅表（分支卸载释放） |
| `@if` 条件 | 同上 | 条件依赖可观察时订阅；否则静态 | `ConditionalBlock` 依赖表 |
| `@foreach` 集合 | 同上 | 集合路径/`Signal<T>` 可观察时订阅；否则静态 | `ForEachBlock` 依赖表 |
| `@foreach` item | item 变量 + code-behind 实例成员 | item 成员不重写；**item 自己的可观察属性允许**；组件级状态禁止（`EUI014`） | item 作用域（条目移除/重建释放） |
| `Key={expr}` | item 变量 + 常量/静态/普通成员 | 不订阅；不得引用可观察状态/`Signal<T>`（`EUI039`） | 无 |

- 模板不声明局部变量、方法、类型参数；复杂逻辑放 code-behind 方法/属性，模板只写表达式。
  （v3 历史结论，按 `spec/README` 第 5 条保留原文；K23 ④ 已定 `@code` 可声明类成员，见 spec/01 §1.5 / D86。）
- 依赖分析按**符号**解析（S2，15 §15.5），不再依赖"本类型信号名表"；基类/状态属性/可观察路径都按语义模型分类。
  旧的正则重写限制（D10/D31）在 S2 前仍适用。
- item 变量遮蔽遵循 C#：item 变量遮蔽同名 code-behind 成员；规范建议不要制造这种遮蔽。
- 事件处理器里的读取不参与依赖分析；触发时读取当前值（§3.3.7）。

### 3.3.2 标签与属性解析

解析顺序按目录项的 `Origin` 分支（14.5.3）：

| Origin | 普通属性 | `@bind-P` |
|---|---|---|
| 控件（`Element` 派生） | 保留属性（`Key`/`Ref`/`Class`/`Style`）→ 事件（`On{Event}`）→ 属性表 → 附加属性（G4）→ 未知 | 目标必须是属性表内的可写属性 + `{Name}Property`；事件/保留/只读 → `EUI040` |
| 组件（`Component` 派生） | 保留属性 → 参数表（委托参数就是事件，不做 `On*` 推断）→ 未知 | 目标必须是 `[UiParameter]` + 可观察 partial 属性；否则 `EUI040` |

- 标签必须解析到目录中的已知控件/组件；未知 → `EUI001`；冲突 → `EUI016`；不可用 → `EUI017`/`EUI018`/`EUI030`。
- 属性名大小写敏感；未知 → `EUI002`，并给最近名建议（Levenshtein ≤ 3，最多 3 个候选）。
- 事件属性只对控件按 `On{EventName}` 解析；组件的 `OnClose` 若存在同名委托参数，按参数处理，不按事件字段处理。
- `@bind-P` 的 `P` 按上表解析；解析失败先报 `EUI002`（未知属性），解析成功但不可绑报 `EUI040`。
- `P:bind` / `P:value` 先按 `P` 解析目标，再按 §3.3.3.3 映射到新语义并报 `EUI043` Warning。
- 事件/属性同名的元数据冲突由 14.4.2 的 `EUI026` 拦截。
- 保留属性优先于控件同名属性；控件不得声明 `Key`/`Ref`/`Class`/`Style` 同名属性。
- 控件的内容模型目标若是普通可写属性（如 `Button.Label`），可作为 `@bind-P` 目标；但同一元素不得再有文本内容（`EUI033`）。
  组件的 `[UiContent]` 参数永远是 build-once，不可作为 `@bind-P` 目标（`EUI040`）。

### 3.3.3 赋值、反应式与信号重写（核心 · v3 历史）

> **v4 换轨**：本节的可观察路径/`Signal<T>` 重写/依赖订阅语义已废止。v4 的语义是：
> 普通 CLR 状态 + 组件级 `Invalidate`/`Render` 重算根级 `P={expr}` 与文本，块内走 `RenderActions`；
> 规范见 [spec/01 §3](../spec/01-template-syntax.md) 与 [spec/03](../spec/03-reactivity-and-binding.md)。
> 下表只用于解释 v3 代码现状与历史决策，MUST NOT 作为实现依据。

| 写法 | 求值 | 反应式 | 订阅 | 约束/诊断 | 状态 |
|---|---|---|---|---|---|
| `P="literal"` | Build 一次 | 否 | 无 | 编译期转换（`EUI003`/`EUI023`） | ✅（§3.4.2 全表，C3） |
| `P={expr}` | Build + 依赖变化 | 全部依赖可观察时 | 每条依赖路径一个 watcher（15 §15.4.2） | 目标可写；不可观察 → `EUI036`/`EUI038` | S2（当前一次性，D23） |
| `@bind-P={expr}` | Build + 双向 | 是 | 源 watcher + 目标事件/订阅 | 源可写可观察路径或 `Signal<T>`；目标可写 | S3（D24） |
| `P:bind={expr}` | 同 `@bind-P` | 是 | 同 | 旧语法，`EUI043` Warning | ✅ → 弃用（S3） |
| `P:value={expr}` | 同 `P={expr}` | 是 | 同 | 旧语法，`EUI043` Warning | 未实现 → 弃用（S3） |
| `OnE={expr}` | Build 时 `AddHandler` | 否 | 无 | `Action` / `Action<RoutedEventArgs>` | ✅（控件）/ C4 组件 |
| `Key={expr}` | 条目创建 | 身份 | 无 | 第一个元素根；不得可观察（`EUI039`） | ✅（S2 加严） |
| `Ref={ident}` | Build 时赋给字段 | 否 | 无 | 简单标识符、字段可写 | G3 / 保留 `EUI029` |
| `Class`/`Style` | — | — | — | — | G4 / 保留 `EUI031` |

#### 3.3.3.1 单向反应式 `P={expr}`

- `P={expr}` 在 Build 时求值一次并赋值；随后，只要表达式中的**全部**依赖可观察，任一依赖变化就重跑 updater。
- 依赖规则是**全有或全无**（15 §15.4.2）：
  - 全部实例读取可观察 → 生成订阅；同一表达式多个依赖共享一个 updater；
  - 含任何普通实例属性/字段 → 整条表达式一次性，报 `EUI036` Warning；
  - 含方法调用/索引器/lambda/`dynamic`/`await`/集合表达式等不可分析结构 → 整条一次性，报 `EUI038` Warning；
  - 纯常量/静态成员/`nameof`/`typeof` → 一次性，不报警。
- 不做部分订阅：`{Count + GetName()}` 不会因 `Count` 变化而更新；要更新就把 `GetName()` 改成可观察属性/`[UiComputed]`。
- `Signal<T>` 的 `.Value` 是值边界：之后的 `List.Count`/`Dictionary[...]` 不形成独立依赖；信号替换触发更新。
- 目标：
  - 控件属性：必须可写并存在 `{Name}Property`；写入走公开 CLR setter（`Element.SetValue` 内部短路/失效，R3/R7）；
    不可写/缺失 → `EUI019`/`EUI040`（S2 统一；当前由生成 C# 报错）；
  - 组件可观察参数：updater 写参数，子组件内部 watcher 更新，不重跑 `Build()`；
  - 组件普通参数：updater 仍写参数（源变化时赋新值），但子 UI 不刷新；有可观察依赖时报 `EUI044` Warning；
  - 组件 `Signal<T>` 参数（legacy）：表达式类型必须是 `Signal<T>`，传对象、不订阅、不生成 updater；子组件负责；
  - 只读属性/无 `{Name}Property` → C# 编译错误（S2 起统一为 `EUI019`/`EUI040`）。
- `P={expr}` 与文本内容同时指向同一 `Text` 目标 → `EUI033`（同一目标重复赋值），不生成两套 updater。
- 字面量转换在编译期完成（R2）；第三方转换器因 R1 不支持（14.7.3）。

#### 3.3.3.2 双向绑定 `@bind-P={expr}`

- 语法见 §3.1.3/§3.2.1；值必须是 `{expression}`，字面量 → `EUI037`。
- 目标 `P`：
  - 控件：public 可写属性 + `{Name}Property`；不得是事件/保留属性；`[UiContent]` 目标不可绑（组件）；
  - 组件：`[UiParameter]` + 可观察 partial 属性；`Signal<T>` 参数、普通参数、`[UiContent]`/`[UiSlot]` 参数 → `EUI040`；
  - 其它 → `EUI040`。
- 源 `expr` 必须满足（15 §15.4.4）：
  1. 是**可写可观察路径**：`this.Count`、`Vm.Count`、`item.Count`；每段可观察，叶有 public setter；
  2. 或者是 `Signal<T>` 字段/属性（迁移用）；
  3. 不得是方法调用、索引器、`?.`、静态成员、常量、只读属性、`[UiComputed]`；
  4. 类型与目标属性类型一致（忽略可空注解）。
  违反 → `EUI037`。
- item 模板内：源路径必须 rooted at item 变量；引用组件级状态 → `EUI042`。
- 目标属性与源路径解析到同一成员/同一对象时视为自绑，`EUI037`；静态无法判定时不报，运行时 `_syncing` 防回环。
- 初始方向：**源值胜出**（Build 时目标被源初始化）；随后双向。控件用户操作走公开 setter → `PropertyChanged` → 源；
  组件内部写参数 → `IUiObservable` 通知 → 源。
- 生成：`UiBinding.TwoWay` / `UiBinding.TwoWayComponent`（15 §15.3.4）；两侧写入用相等判断短路，`_syncing` 防回环；
  **不得**用 `SetValue/GetValue` 绕过属性引擎（R3/R7）。
- 同一属性不得同时写 `P={expr}` 和 `@bind-P` → `EUI033` 类重复目标。
- 示例生成形态见 §3.6.2；组件目标与控件目标的差别只在目标侧事件/订阅。

#### 3.3.3.3 旧语法 `:bind` / `:value`（弃用）

- `P:bind={expr}` ≡ `@bind-P={expr}`：`expr` 可以是 `Signal<T>` 对象（v2 兼容）或状态路径（v3 新增）；
  `:bind={sig.Value}` 仍是类型错误（不能把 `T` 传给 `Signal<T>` 源）。
- `P:value={expr}` ≡ `P={expr}`：v2 中 `:value` 是一次性→反应式的显式写法；v3 中 `P={expr}` 已是单向反应式，
  因此 `:value` 没有额外语义，仅作为兼容别名。
- S3 起两者都报 `EUI043` Warning；至少保留一个发布周期，S5 评估改为 Error/移除（15 §15.4.12）。
- 迁移：`:bind` → `@bind-P`；`:value` → `P=`；状态从 `Signal<T>` 迁到 `[UiState]` 后写裸名（15 附录 B）。

#### 3.3.3.4 信号重写与值上下文

生成器按**目标类型与符号**决定是否取 `.Value`；状态属性是普通 CLR 属性，直接读取，**没有** `.Value`：

| 上下文 | 目标 | 状态属性/可观察路径 | `Signal<T>` 裸名 |
|---|---|---|---|
| 文本插值 `{x}` | 插值字符串 | 直接读取 | 取 `.Value` |
| `P={x}` | 控件属性 `T` | 直接读取 + 订阅（可观察时） | 取 `.Value` + 订阅信号 |
| `P={x}` | 组件 `Signal<T>` 参数（legacy） | — | **不取**，整个信号对象传入 |
| `P={x}` | 组件可观察参数 `T` | 直接读取 + 订阅 | 取 `.Value` + 订阅 |
| `P={x}` | 组件普通参数 `T` | 直接读取 + updater | 取 `.Value` + updater；`EUI044` |
| `@bind-P={x}` | 源 | 路径原样（读写） | 信号对象原样（读写 `.Value`） |
| `OnE={x}` | 事件处理器 | 直接读取当前值 | 取 `.Value`（触发时读） |
| `@if (x)` | `bool` 条件 | 直接读取 + 订阅 | 取 `.Value` + 订阅 |
| `@foreach (var i in x)` | 集合 | 直接读取 + 订阅 | 取 `.Value` + 订阅 |
| `Key={x}` | 身份 | **禁止**（`EUI039`） | **禁止**（`EUI039`） |

- 裸名与 `.value`/`.Value` 后缀只对 `Signal<T>` 归一；`Count.Value` 对状态属性是 C# 错误。
- item 成员不重写：item 的普通 CLR 属性直接读；item 的 `Signal<T>` 成员取 `.Value`（S2 语义分析）。
- 字符串字面量、注释、同名局部变量不参与重写；S2 起用 Roslyn 语义模型/`SyntaxRewriter` 替代正则（D10/D31）。
- 目标为 `Signal<T>` 参数时保持对象：这是 14.6.1 的 legacy 组件反应式写法，不能把 `.Value` 传进去。

#### 3.3.3.5 组件参数赋值

- 参数 `P={expr}`：
  - 可观察参数（`partial` + `[UiState]`/`[UiObservable]`）：源变化 → updater 写参数 → 子 `IUiObservable` → 子模板；
    不重跑 `Build()`；
  - 普通参数：源变化 → updater 写参数，但子 UI 不刷新；有可观察依赖时报 `EUI044` Warning；
  - `Signal<T>` 参数（legacy）：传对象，不生成 updater；子组件内部订阅。
- `[UiContent]`/`[UiSlot]` 参数 build-once，不可观察（`EUI034`/`EUI040`）。
- 参数没有 diff；`P={t.Name}` 这类依赖 item 的表达式在条目复用时按新 item 重跑 updater，组件不重建（§3.3.6.4）。
- `EUI032` 停用，由 `EUI044` 取代（15 §15.8.3）。

### 3.3.4 订阅与生命周期（v3 历史）

> **v4 换轨**：以下"订阅"表已不适用；v4 的释放模型是 `Track`/`Adopt`/`TrackContent` + `BlockScope.Disposables`
> （规范见 [spec/02 §5](../spec/02-generated-code-contract.md) 与 [spec/03 §2.8](../spec/03-reactivity-and-binding.md)）。
> 本节仅保留 v3 实现的历史说明。

| 位置 | 生成/归属 | 何时释放 |
|---|---|---|
| 根属性/根文本/根 `@bind` | `Track(...)`（组件作用域） | 组件 `Dispose()` |
| `@if` 内容内的订阅 | `ConditionalBlock._contentSubscriptions` | 分支切换/块 Dispose |
| `@if` 条件依赖 | `ConditionalBlock._depSubscriptions` | 重新 `SetCondition`/块 Dispose |
| `@foreach` 集合依赖 | `ForEachBlock._depSubscriptions` | 重新 `SetItems`/块 Dispose |
| `@foreach` item 内订阅/子块 | `BlockScope.Disposables` | 条目移除/条目重建/块 Dispose |
| 组件实例 | 调用方作用域 | 同上 |
| `UiBinding` | 绑定所在作用域 | 同上 |
| 事件处理器 | 不产生订阅 | 元素生命周期 |

- 模板不写 `Subscribe`/`Dispose`；作用域由生成代码负责（R8）。
- `@if` 分支重建会销毁旧内容（元素重新构造），控件的构造/setter 必须可重复执行且无副作用（14.4.5）。
- `@foreach` 条目复用/重建规则见 §3.3.6.4：同 Key 同 item 复用；同 Key 不同 item 重建并释放旧 scope。
- `ForEachBlock.Dispose` 必须释放仍存在条目的 `BlockScope.Disposables`（修复 D18/D27）；整块卸载不得泄漏 item 订阅。
- 条目/分支里的组件实例也加入同一作用域，移除时 `Dispose`。
- `ConditionalBlock`/`ForEachBlock` 自身也是 `IDisposable`，由所在作用域 `Track` 或加入父块的作用域
  （[Blocks.cs:29](../src/EclipseUI.Runtime/Blocks.cs#L29)、[:102](../src/EclipseUI.Runtime/Blocks.cs#L102)）。
- 生成代码不得把订阅存静态字段/单例/`ConditionalWeakTable`（R6/R8）；每个订阅可追踪到唯一所有者。

### 3.3.5 文本与内容模型

内容模型由控件/组件声明（14.4.4）：

| 模型 | 允许的子内容 | 生成目标 | 违规 |
|---|---|---|---|
| `None`（默认） | 无 | — | 任意子内容 `EUI006` |
| `Children` | 元素、嵌套块；纯空白文本忽略 | 目标 `.Add(Element)` | 非空白文本 / 非法结构 `EUI006` |
| `Text` | 文本段与 `{expr}` | 目标 `string` 属性（如 `Button.Label`） | 元素/块 `EUI006` |
| `TextRuns` | 文本段与 `{expr}` | `ITextContentHost.AddRun(string)`（`TextBlock`） | 元素/块 `EUI006` |

内容模型是控件元数据（14.4.4）；目录 5 个内置标签各自显式声明模型，枚举尚无 `None`；
`None` 默认随 C1 的符号派生落地（14.12）。

文本语义：

- 静态文本在 Build 时赋值：`Text` 模型合并所有静态段为一条字符串；`TextRuns` 模型每个静态段一个 `TextRun`。
- 含 `{expr}` 的文本是**反应式**的，依赖规则与 `P={expr}` 相同（§3.3.3.1，全有或全无）：
  - `Text`：整段文本一个完整插值串的 updater；动态段依赖并集；更新时重设整个目标值。
  - `TextRuns`：每个动态段 `AddRun("")` 创建一个空 `TextRun`，updater 设置该 run 的 `Text`；静态段不受影响。
- `{Tasks.Count}`：`Tasks` 是状态属性 + `ObservableCollection<T>` 时，订阅属性 + INCC；增删后 Count 更新。
- `{tasks.Value.Count}`：`tasks` 是 `Signal<T>` 时订阅信号；值内部集合原地修改不触发（`EUI041` 如适用）。
- 文本插值使用 C# `$"..."`：当前区域文化、`ToString()` 语义；不支持格式说明符（写 `.ToString("F2")`，§3.1.6）。
- 文本里不能嵌套元素或块；`TextRuns` 也只接受文本段，需要多段不同样式等富文本能力时另设控件（G4）。
- **同一目标重复赋值是错误**：`Text` 模型下显式 `Label="..."` 和文本内容同时存在，或 `P={expr}` 与文本同时存在，
  规范要求 `EUI033`；当前实现静默双写（§3.5.3 D7）。
- `Children` 模型中的纯空白文本在规范化后消失，不报 `EUI006`；非空白裸文本报错，必须包元素。
- 组件的默认内容参数（C4）按组件声明的 `[UiContent]` 模型处理；具名插槽 G4。

### 3.3.6 控制流（v3 历史）

> **v4 换轨**：`@if`/`@foreach` 的规范语义是 `ConditionalBlock`/`ForEachBlock` + 显式 `Refresh()`、
> 无障碍读取普通集合与 item 成员（[spec/01 §3.4](../spec/01-template-syntax.md)、[spec/03 §6–§7](../spec/03-reactivity-and-binding.md)）。
> 本节关于"可观察依赖/订阅工厂"的条款已废止，只保留 v3 历史。

#### 3.3.6.1 `@if`

- 形式：`@if (condition) { ... }`；条件必须是 C# `bool` 表达式；没有 `else`/`else if`（用两个 `@if` 或取反）。
  （v3 历史结论，按 `spec/README` 第 5 条保留原文；K23 ③ 已定 `else`/`else if`/`@switch`，见 spec/01 §1.5 / D87。）
- 首次求值在 Build 时；条件依赖可观察时，依赖变化会重新求值并切分支；否则为静态条件（一次求值）。
- 生成：`SetCondition(Func<bool>, Func<Action, IDisposable>?)`（15 §15.4.6）；订阅工厂存入块依赖表，旧订阅先释放；
  兼容旧 `params ISignal[]` 重载（Obsolete）。
- 分支内容只允许元素与嵌套块；可以有多个根元素；分支切换销毁旧元素并重建新元素（不保留子树状态）。
- 条件依赖不可观察/不可分析 → 静态条件 + `EUI036`/`EUI038`；不生成订阅，不切分支。
- 在 item 模板内：条件可依赖 item 的可观察属性；条目重建时内容重建；同 Key 同 item 引用时属性变化通过 item 订阅更新。
  **不再需要把数据版本并入 Key**。
- 布局：✅ G3 已收敛为**布局透明**（D16 关闭，实现口径见 D77）。块仍是元素树节点，
  但不引入自己的间距/内边距，内容按最近布局祖先的 `Gap` 落位，空块不占间距槽。
  规范正本见 [spec/01 §4.10](../spec/01-template-syntax.md) 末条与 §4.11。

#### 3.3.6.2 `@foreach`

- 形式：`@foreach (var item in collection) { ... }`；**`var` 必写**，不支持 `T item in ...`、解构、`await foreach`。
- `collection` 允许：
  - 可观察属性路径（`Tasks`、`item.Children`）；
  - `Signal<T>` 成员（`tasks`，legacy）；
  - 上述路径的叶类型实现 `IEnumerable<TItem>`。
- 不允许方法调用/LINQ/索引器/`??`/强制转换：`EUI007` 或 `EUI038`；在 code-behind 里预先组合成可观察属性。
- item 类型由 `IEnumerable<TItem>` 推断，生成 `ForEachBlock<TItem>`；item 变量只在块内可见。
- item 模板：
  - 允许引用 item 自己的成员（普通属性/字段，或 item 自身的可观察属性）；
  - **禁止引用组件级状态/`Signal<T>`**（`EUI014`，仅这一范围）；`@bind-P` 源必须 rooted at item，否则 `EUI042`；
  - `Key` 不得依赖可观察状态/`Signal<T>`（`EUI039`）；
  - `@bind-P={item.X}` 合法（item 可观察属性双向）；
  - 嵌套 `@foreach`（S4）：内层集合可以是 item 的可观察属性/集合；内层块登记在 item scope，条目移除/重建时释放；
    方法调用/LINQ 仍禁止。
- 集合变化（S4）：
  - 属性替换/`Signal<T>` 替换 → `Refresh`；
  - `INotifyCollectionChanged` 的 Add/Remove/Reset/Move/Replace → `Refresh`；
  - `List<T>`/数组原地修改 → **不通知**；静态类型为具体非 INCC 集合时 `EUI041` Warning；
  - 接口类型（`IList<T>`/`IReadOnlyList<T>`/`IEnumerable<T>`）不强制警告，运行时只有实现 INCC 才观察内容变化。
- item 模板可有多个根元素；`Key` 只允许出现在第一个元素根上，标识整个条目。
- 空集合/单元素不特殊；`Key` 缺失时用 item 自身作为 key。

#### 3.3.6.3 `Key` 语义

- `Key={expr}` 只用于 `@foreach` 条目身份；非 item 模板根报 `EUI010`；字面量报 `EUI010`；
  引用可观察状态/`Signal<T>`/可观察路径 → `EUI039`（`Key` 必须是稳定身份，不能随数据更新）。
- 无 `Key` 时默认 key = item 自身（`ForEachBlock` 生成 `item => (object?)item`）。
- `Key` 表达式返回 `null` 时归一到一个共享空键；应避免 null，否则多个条目会互相复用。
- 重复 key 不报错；同 key 的条目按出现顺序复用（`Queue<Entry>`）；新出现的重复 key 新建条目。
- key 比较发生在对象装箱后；字典身份按 `Equals`/`GetHashCode`。
- **复用条件 = key 相等且 item 身份相等**（15 §15.4.8）：
  - 值类型：`EqualityComparer<T>.Default.Equals(oldItem, newItem)`；
  - 引用类型：`ReferenceEquals(oldItem, newItem)`。
- key 相同 + item 相同：复用结构，重跑 `BlockScope.Updaters`；item 内部订阅保持。
- key 相同 + item 不同：释放旧 `BlockScope.Disposables`，**重建条目**；禁止闭包捕获旧 item。
- key 不同/新增：新建条目；旧条目未复用则释放。
- `Key` 应只依赖 item 的稳定身份字段；条目重建会丢失元素局部状态（焦点/滚动/动画）。
  需要保留局部状态时，让 item 对象稳定、通过可观察属性改数据，而不是替换 item 对象。

#### 3.3.6.4 条目复用时什么会重跑

| 模板内容 | key 同 + item 同 | key 同 + item 不同 | key 不同/新增 |
|---|---|---|---|
| 元素结构 | 不重建 | **重建** | 重建 |
| 根层文本/`P={item.X}`/`@bind` | 依赖 item 属性时按订阅更新；否则不重跑 | 重建时求值 | 新建时求值 |
| 根层 `Scope.Updaters` | 重跑 | 重建时不再需要 | 新建时求值 |
| 事件处理器 | 捕获当前 item（同引用） | 重建并绑定新 item | 重建 |
| `@if (item.X)` | item 属性变化时重算（若可观察） | 重建 | 重建 |
| 嵌套 `@foreach`（S4） | item 属性/集合变化时更新 | 重建 | 新建 |
| 组件参数 updater | 重跑（参数表达式可能依赖外部状态） | 重建组件 | 新建 |

表中"重跑 updater"只覆盖 item 模板**根层**的文本/`P={expr}`/`@bind`；`@if` 分支内的文本与属性随分支内容创建，
条件重算时按 §3.3.6.1 处理。

由此得到三条硬规则：

1. item 模板里需要随 item 数据刷新的属性/事件，优先把 item 改成 `[UiState]` 可观察属性；否则用 **Key 包含数据版本**
   触发重建，或把展示逻辑做成组件（参数可观察）。
2. 事件处理器捕获的是当前 item；同 Key 不同 item 会重建条目并重新绑定，不会留下旧闭包。
3. 不要依赖条目复用保留事件处理器里的旧 item；需要最新 item 时在 handler 中通过 key/id 查当前数据。

### 3.3.7 事件

- 控件事件：属性名 `On{EventName}`，对应 `{Name}Event` 的 `public static readonly RoutedEvent` 字段（14.4.3）。
  - 只接受 `{expression}`；字面量或 `:bind`/`@bind-` → `EUI004`/`EUI040`。
  - 处理器必须能转换为 `Action` 或 `Action<RoutedEventArgs>`；签名不匹配在**可静态判定**时由分析器
    报 `EUI024`（Error，✅ 2026-11-08/D82，落 `.ui` 属性值并短路生成输出），无法静态判定时静默、
    交回生成代码经 `#line` 由 C# 报错（spec/02 §2.4 规则 8 不重复报告）。判定口径见 spec/02 §4.6.1 规则 7
    与 §12 D82。
  - 生成：`el.AddHandler(global::...Event, handlerExpr);`；事件按源码顺序在 Build 时订阅，元素挂载前完成。
- 组件事件（C4）：组件当前没有路由事件；委托类型参数即事件，属性名就是参数名（如 `OnClose`），
  不做 `On*` 前缀推断；同样只接受 `{expression}`。
- 事件处理器里的状态属性/`Signal<T>` 读取在事件触发时发生：状态属性直接读当前值，`Signal<T>` 取 `.Value`；不建立订阅。
- 事件处理器表达式**不参与依赖分析**；`EUI036`/`EUI038` 不适用于事件体（语法/类型错误由 C# 报）。
- 事件处理器捕获 item：同 Key 不同 item 会重建条目并重新绑定；同 item 引用复用捕获当前对象，安全。
- 事件名区分大小写；`Onclick` 是未知属性。C2 后控件事件表面为 8 个（`OnClick` + 7 个输入/焦点事件），
  迁移与清单见 14.4.3。

### 3.3.8 组件（C4 目标）

组件是 `.ui` + code-behind 生成的 `Component` 子类；标签规则与控件相同（14.4.1），但属性来自参数而非 StyledProperty。

- 参数必须标 `[UiParameter]`，`public`、有 `public` setter（`init` 不允许）；必填写 `[UiParameter(Required = true)]`。
  缺失必填 → `EUI027`；`required`/`init`/无 setter → `EUI028`。
- 参数类型任意；字面量按 §3.4 转换；委托类型参数就是事件。
- 参数可观察性由 partial + `[UiState]`/`[UiObservable]` 决定（15 §15.2.7）：
  - `[UiParameter] public partial string Title { get; set; }` + 类 `[UiState]` → 可观察参数；
  - `[UiParameter] public string Title { get; set; }` → 一次性参数；父 `P={expr}` 仍可在源变化时赋值，但子 UI 不刷新（`EUI044`）；
  - `[UiParameter] public Signal<string> Title { get; set; }` → legacy 信号参数，子组件内部订阅。
- 生成顺序（冻结；块内把 `Track(c0)` 换成 `subs.Add(c0)`/`itemSubs.Add(c0)`）：

```csharp
var c0 = new global::App.UserCard();
c0.Title = "Hi";                 // 参数按源码顺序
c0.Body = body;                  // 默认内容（若有）在 Mount 前
Track(c0);                       // 先进入调用方作用域
c0.Mount();                      // 只调用一次；内部 Build
parent0.Children.Add(c0.Root);   // 最后挂到父级
```

- `Track(c0)` 在 `Mount()` 之前：先把实例登记进调用方作用域；`Build()` 内 `Track(...)` 的订阅落在组件自身的
  `_tracked` 上，由 `Component.Dispose` 释放（[Component.cs:29](../src/EclipseUI.Runtime/Component.cs#L29)）。
  顺序颠倒则 Mount 之后、登记之前一旦出错，实例无人释放。
- `Mount()` 幂等；`Dispose()` 幂等；组件实例由调用方作用域释放，组件内订阅随实例释放（14.6.4）。
- 参数没有 diff：Build 只跑一次；参数 setter 变化不重建组件模板。
  - 可观察参数：父 updater 写参数 → 子 watcher → 子模板定向更新；
  - 普通参数：父 updater 可写，但子 UI 不刷新（`EUI044`）；
  - `Signal<T>` 参数：父传对象，子内部订阅。
- `@bind-P` 目标必须是可观察参数；子内部写参数 → `IUiObservable` → 父回写（§3.3.3.2）。
- 默认内容（C4）按 `[UiContent]` 参数处理；具名插槽 G4（`<Slot>`/`<ContentSlot>` 保留 `EUI031`，G4 后插槽错误 `EUI022`）。
  内容/插槽 build-once，不可观察。
- 组件标签不继承控件的 StyledProperty 表面；`Padding` 等控件属性不会自动成为组件参数。
- 当前目录无组件（派生目录同样只含 5 个内置标签），组件标签报 `EUI001`；C4 落地（14.12）。
- 组件自身 `[UiState]` 的非参数状态只对子模板可见；父不能通过标签访问。

### 3.3.9 保留属性与保留语法

| 写法 | 语义 | 状态 | 现在的诊断 |
|---|---|---|---|
| `Key={expr}` | `@foreach` 条目身份 | ✅ | `EUI010`（位置/形式）/ `EUI039`（可观察） |
| `Ref={ident}` | 元素/组件实例赋给 code-behind 字段 | G3 | 当前 `EUI002`（D4） |
| `Class="..."` | 样式类 | G4 | 当前 `EUI002`（D2） |
| `Style="..."` | 内联样式 | G4 | 当前 `EUI002`（D2） |
| `P:bind={expr}` | 旧双向绑定（≡ `@bind-P`） | ✅ → 弃用 | S3 起 `EUI043` Warning |
| `P:value={expr}` | 旧单向别名（≡ `P={expr}`） | 未实现 → 弃用 | 当前 `EUI002`（D1）；S3 起 `EUI043` |
| `<Slot Name="...">` | 具名插槽（消费侧） | G4 | 当前 `EUI001`（D3） |
| `<ContentSlot Name="..." />` | 插槽宿主（组件模板侧） | G4 | 当前 `EUI001`（D3） |
| `<DataGrid<Order>>` | 泛型标签 | G4 | 当前 `EUI100`（D5） |
| `Grid.Row="1"` | 附加属性 | G4 | 当前 `EUI002`（D19）；规范 `EUI031` |

保留语法的统一规则：

- 不得把保留名用作普通控件/组件属性（控件作者侧 `EUI026`）；
- 使用保留语法时**必须**报"已保留、未实现"类诊断（`EUI031`/`EUI029`/`EUI030`；插槽错误从 G4 起用 `EUI022`），
  不得退化成 `EUI001`/`EUI002` 的拼写建议；
- `@bind-` 不是保留语法：它是双向绑定的语法形态；目标/源非法分别报 `EUI040`/`EUI037`，不得退化成 `EUI001`/`EUI002`；
- 实现保留语法时只在本文/14 增加状态，不改变已有语法的含义。

## 3.4 字面量

### 3.4.1 判定与转换时机

- 属性值 token 是双引号字符串 → 字面量；是不带引号的 `{...}` → 表达式。引号内的 `{x}` 是字符串，不是表达式。
- 字面量在**编译期**按目标类型转换；生成代码直接写 C# 字面量，运行时不解析字符串、不反射（R2）。
- 数字/布尔/枚举/TimeSpan 转换前 `Trim()`；`string` 保留原样（包括前后空格与换行）。
- 使用 `InvariantCulture`；小数点固定 `.`，不使用千分位；`EUI003` 消息给出期望格式示例。
- 无转换器的类型：字面量报 `EUI023`，提示用 `{expr}` 代替 —— ✅ C3：`UiLiterals.HasConverter(shape)`
  区分"无转换器"（EUI023）与"有转换器但格式/范围/成员错"（EUI003），D14 关闭。

### 3.4.2 内置转换器

| 目标类型 | 接受的写法 | 生成的 C# | 状态 |
|---|---|---|---|
| `string` | 任意原始文本；空串合法 | C# 转义字符串字面量 | ✅ |
| `bool` | `true` / `false`（小写） | `true` / `false` | ✅ |
| `double` | 十进制/科学计数，可选符号；`1`、`8.5`、`1e-3` | `R` 格式 + `d` 后缀（`8d`） | ✅ |
| `int` | 十进制整数，可选符号；范围 `int` | 十进制字面量 | ✅ |
| `float` | 同 `double` | `R` + `f` 后缀 | ✅ C3 |
| `long` / `short` / `byte` | 十进制整数，范围检查 | `L` 后缀 / 显式窄化 | ✅ C3 |
| `decimal` | 十进制数，范围检查 | `m` 后缀 | ✅ C3 |
| `char` | 单字符或 `\n`/`\r`/`\t`/`\\`/`\"`/`\'`/`\0`/`\uXXXX` | C# 字符字面量 | ✅ C3 |
| 枚举 | 成员名，大小写敏感；不接受逗号组合/Flags | `global::Ns.Enum.Member` | ✅ C3 |
| `TimeSpan` | `250ms`、`1.5s`、`2m`、`1h`、`1d`，或 `[-][d.]hh:mm[:ss[.fffffff]]` | `new global::System.TimeSpan({ticks}L)`（编译期换算） | ✅ C3 |
| `Color` / `Thickness` / `CornerRadius` | 类型引入时定义 | — | G4 |

规则：

- 数字范围错误、格式错误、枚举成员不存在 → `EUI003`，消息带期望格式/候选成员。
- 整数不接受小数/指数；`double` 接受 `1e3`；不接受 `NaN`/`Infinity` 字面量，需要时写表达式。
- 枚举不接受 `A, B` 组合；Flags 用表达式 `{ MyFlags.A | MyFlags.B }`。
- TimeSpan 单位只允许单个：`1h30m` 不是字面量；用 `{ TimeSpan.Parse("01:30:00") }` 或 `1.5h`。
- 字面量中的 `{`/`}`/`<` 不受文本特殊字符规则影响（它们只在引号内是普通字符）。

### 3.4.3 字面量与表达式的边界

- `P="{x}"` → 字符串 `{x}`；`P={x}` → 表达式 `x`。
- `P="true"` 对 `bool` 属性是字面量；`P={true}` 是表达式；两者生成结果相同。
- 字符串里需要引号/反斜杠且要求精确字符时，用表达式：`P={"a\"b"}`。
- 字面量不能被当作事件处理器（`OnClick="Foo"` → `EUI004`）；事件必须表达式。
- 字面量不能作为 `@bind-P` 的值（`@bind-Checked="true"` → `EUI037`）；双向绑定必须给可写表达式。

## 3.5 诊断、状态与偏差

### 3.5.1 诊断责任与 span

- 每个诊断带稳定代码、精确 span、英文消息；文档/编辑器可本地化消息，代码不变。
- 标签诊断指向标签名；属性诊断指向属性名/值；表达式错误由 C# 编译通过 `#line` 指回 `.ui` 行。
- 未知名给最近建议（Levenshtein ≤ 3，最多 3 个）；建议不改变解析结果。
- 诊断码定义与控件侧责任划分：EUI001–EUI033 见 [14.9.1](./14-control-template-contract.md)；EUI034–EUI045 的 v4 口径见
  [spec/03 §11](../spec/03-reactivity-and-binding.md#11-诊断) 与 [spec/01 §6.2](../spec/01-template-syntax.md#62-诊断表)；
  本文只负责语法触发点。以下为 v3 码表历史，含已停用码，仅作追溯，不作实现依据。

| 构造 | 诊断码（v3 历史） |
|---|---|
| 未知/冲突/不可用标签 | `EUI001` / `EUI016` / `EUI017` / `EUI018` / `EUI030` |
| 未知属性/事件/参数 | `EUI002`（附 `EUI019`/`EUI020`/`EUI024`/`EUI026`/`EUI028`） |
| 保留语法 | `EUI022`（插槽错误，G4）/ `EUI029`（`Ref`）/ `EUI031`（`Class`/`Style`/插槽/附加属性） |
| 字面量 | `EUI003` / `EUI023` |
| 事件值 | `EUI004` / `EUI024` |
| 内容模型 | `EUI006` / `EUI021` |
| `@foreach` | `EUI007` / `EUI014`（收窄为组件级状态）/ `EUI041`（集合原地变更） |
| 指令 | `EUI008`（`@switch`）/ `EUI009`（未知） |
| `Key` | `EUI010` / `EUI039`（可观察依赖） |
| 根/类/code-behind | `EUI012` / `EUI013` / `EUI005` / `EUI011` / `EUI015` |
| 重复属性/重复目标 | `EUI025` / `EUI033` |
| 状态声明 | `EUI034` / `EUI035` |
| 反应式依赖 | `EUI036`（不可观察）/ `EUI038`（不可分析） |
| 双向绑定 | `EUI037`（源）/ `EUI040`（目标）/ `EUI042`（item 根）/ `EUI043`（旧语法） |
| 组件参数 | `EUI027` / `EUI028` / `EUI044`（`EUI032` 停用） |
| 纯语法错误 | `EUI100` |

### 3.5.2 语法项状态总表（v3 历史）

> **v4 换轨**：状态以 [spec/01 §7](../spec/01-template-syntax.md#7-实现状态与阶段) 为准；
> 下表保留 v3 的阶段划分与代码差距说明，不再作为排期依据。

| 语法项 | 规范 | 状态 | 备注 |
|---|---|---|---|
| 元素/自闭合/闭合标签 | 3.2 | ✅ | 名称区分大小写 |
| 属性字面量 | 3.1.5 / 3.4 | ✅ | §3.4.2 全表内置类型（C3） |
| 属性表达式 `P={expr}` | 3.1.6 / 3.3.3.1 | S2 | 当前一次性（D23）；S2 起单向反应式 |
| `@bind-P` | 3.1.3 / 3.2.1 / 3.3.3.2 | S3 | 当前 `@` 在标记模式报 `EUI100`（D24） |
| `:bind` | 3.3.3.3 | ✅ → 弃用 | S3 起 `EUI043`；item 不再一律禁止 |
| `:value` | 3.3.3.3 | 未实现 → 弃用 | 当前 `EUI002`（D1）；S3 起 `EUI043` |
| 控件事件 `On*` | 3.3.7 | ✅ | C2 后 8 个事件 |
| 属性/事件/内容目标元数据校验 | 3.3.2 / 3.3.7 / 3.3.5 | C3/S2 | 当前由生成 C# 报错（D21） |
| 组件委托事件 | 3.3.7 / 3.3.8 | C4 | 参数名即事件名 |
| 文本插值 `{expr}` | 3.3.5 | ✅ / S2 | 当前信号反应式；S2 起状态属性依赖 |
| `@if` | 3.3.6.1 | ✅ / S2 | 无 `else`；S2 加订阅工厂；块布局 G3 |
| `@foreach` | 3.3.6.2 | ✅ / S2–S4 | 当前集合必须是信号名；S2 支持可观察属性，S4 INCC/嵌套 |
| `Key` | 3.3.6.3 | ✅ / S2 | S2 起同 Key 不同 item 重建；`EUI039` |
| `Ref` | 3.3.9 | G3 | 当前 `EUI002`（D4） |
| `Class`/`Style` | 3.3.9 | G4 | 保留 `EUI031`；当前 `EUI002`（D2） |
| 具名插槽/`ContentSlot` | 3.3.9 | G4 | 当前 `EUI001`（D3） |
| 泛型标签 | 3.3.9 | G4 | 当前 `EUI100`（D5） |
| 附加属性 | 3.3.9 | G4 | 保留 `EUI031`；当前 `EUI002`（D19） |
| 组件/参数/必填 | 3.3.8 | C4 | 当前 `EUI001` |
| 可观察组件参数 | 3.3.8 / 15.7 | S3 | 当前实现只推荐 `Signal<T>` 参数（D35） |
| item 可观察属性/item `@bind` | 3.3.6.2 / 15.4.8 | S2/S3 | 当前 `EUI014` 全面禁止（D34） |
| 嵌套 `@foreach` | 3.3.6.2 | S4 | 当前不支持 |
| 集合 INCC | 3.3.6.2 | S4 | 当前无 INCC 订阅 |
| 注释 `<!-- -->` | 3.1.9 | ✅ | 未闭合 D12 |
| 空白规范化 | 3.1.10 | ✅ | 每层首尾 trim |
| 字面 `{ } < @` | 3.1.8 | ✅ | 用表达式 |
| 重复属性 | 3.2.2 | G3 | `EUI025` 未实现（D6） |
| 同一目标重复赋值 | 3.3.5 / 3.2.1 | S2/S3 | 文本+属性双写（D7，`EUI033`）；`@bind` 冲突 S3 |
| 信号重写（目标类型感知） | 3.3.3.4 | S2 | 当前正则（D10/D31） |
| 语义依赖分析 | 3.3.3.1 / 15.5 | S2 | 当前无（D23/D31） |
| 状态声明 `[UiState]`/`[UiObservable]` | 15.2 | S1 | 运行时/生成器不存在（D32/D33） |
| `[UiComputed]` | 15.2.3 | S4 | 不存在（D33） |
| EUI034–EUI044 | 15.8 | S1–S4 | EUI032 停用；EUI035 属 S4；当前只有 EUI001–EUI033（部分未实现） |
| `@switch` | 3.1.7 | ⛔ | `EUI008` |
| `else`/`else if` | 3.2.1 | ⛔ | 用两个 `@if` |
| 未知指令 | 3.1.7 | ⛔ | `EUI009` |
| XML 实体 | 3.1.5 | ⛔ | 原样保留 |
| pretty-printer | — | G5 | 注释不保留 trivia（§3.6.1） |
| Roslyn 语义模型诊断 | — | S2/G5 | S2 依赖分析；C# 类型错误仍靠 `#line` |

### 3.5.3 当前实现偏差（S1–S5 收敛清单）

> **v4 换轨**：本表是 v3 台账。以 `[UiState]`/`Signal<T>`/`UiWatch`/依赖分析/可观察路径为目标的条目已作废，
> 代码差距改按 §3.5.4 与 spec/02 §12 收敛；其余词法/诊断/布局等条目仍适用。

| # | 规范 | 当前实现 | 影响 | 收敛 |
|---|---|---|---|---|
| D1 | `:value` 是弃用别名（≡ `P={expr}`），报 `EUI043` Warning | ✅ S6（K27）：旧语法 `P:bind`/`P:value` 直接移除，解析到 `:` 后缀属性名即报 `EUI046` Error；`EUI043` 停用且保持停用（[Parser.cs](../src/EclipseUI.Compiler/Parser.cs)） | 不再有静默降级或误导性拼写错误 | ✅ S6 |
| D2 | ✅ C3：`Class`/`Style` 在使用点报 `EUI031`（G4 实现样式后语义不变） | 已修复（2026-11-03，[Analyzer.cs](../src/EclipseUI.Compiler/Analyzer.cs)） | ✅ C3 |
| D3 | ✅ C3：`<Slot>`/`<ContentSlot>` 在使用点报 `EUI031`（仅当目录无同名真实控件；G4 实现后有插槽错误从 `EUI022` 报） | 已修复（2026-11-03） | ✅ C3 |
| D4 | `Ref` G3，当前应报保留/`EUI029` | 未知属性 → `EUI002` | 同上 | C5 |
| D5 | 泛型标签**暂缓**（K19，不排期），精确诊断 `EUI030` 随之暂缓 | `<DataGrid<Order>>` 在标记模式遇到 `<` → `EUI100`（保持现状）；目录侧「泛型类型标签不可用 → EUI030」使用点分流不变 | 非当前阻塞；暂缓 ≠ 取消，D5 仍是开放项 | 暂缓（K19，2026-11-08 用户决策） |
| D6 | ✅ C3：完全重复普值属性在属性级报 `EUI025`；同名且任一侧是绑定（`@bind-P`/`:bind`）仍走目标级 `EUI033`（与 S2 语义一致） | 已修复（2026-11-03，`Analyzer` 带 `attrSeen` 字典） | ✅ C3 |
| D7 | 文本内容与显式目标属性互斥 `EUI033` | ✅ S2：Analyzer 检测文本 + 显式目标、同目标重复绑定并报 `EUI033`（[Analyzer.cs](../src/EclipseUI.Compiler/Analyzer.cs)） | 已消除静默双写 | ✅ S2 |
| D8 | `Key` 只允许 `@foreach` item 根 | ✅ S2：`VisitBlock` 仅对 `@foreach` 的第一个元素根放行 `Key`，`@if` 报 `EUI010`（[Analyzer.cs](../src/EclipseUI.Compiler/Analyzer.cs)） | 误放行已修复 | ✅ S2 |
| D9 | item 模板禁止组件级状态（`EUI014`）；item 自己的可观察属性与 item `@bind` 允许；`Key` 不得依赖可观察状态（`EUI039`） | 只检查根层元素属性/根层文本并豁免 `Key`（[TemplateGenerator.cs:196](../src/EclipseUI.Compiler.Generator/TemplateGenerator.cs#L196)）；嵌套元素、条件/嵌套块与 `Key` 未查 | 可能生成无订阅/陈旧更新 | S2/S3 |
| D10 | 重写按符号与目标类型：状态属性直接读、`Signal<T>` 取 `.Value`、item 成员不重写 | 正则盲替换（[Emitter.cs:125](../src/EclipseUI.Compiler.Generator/Emitter.cs#L125)） | 字符串/同名 item/`Signal<T>` 参数误改 | S2（语义模型，同 D31） |
| D11 | 表达式按 **C# 词法**捕获 | ✅ S6：`CaptureExpression`/`@code` 体/指令参数统一走 `CSharpSnippet.FindMatchingClose`（[CSharpSnippet.cs](../src/EclipseUI.Compiler/CSharpSnippet.cs)）——跳过 `//`/`/* */` 注释、字符字面量、普通/verbatim/插值/raw 字符串（含插值洞递归与 `{{`/`}}`、`""` 转义、`:F2` 格式符）。刻意不引 Roslyn（前端保持可独立测试），用最小状态机对齐 C# 词法 | 已消除：注释内大括号、`@"a""}""b"`、`"""a}b"""`、`$"{(flag ? "a" : "b")}"` 均正确配对（`ExpressionCaptureTests` 13 例） | ✅ S6 |
| D12 | 未闭合注释报 `EUI100` | ✅ S6：`SkipComment`/`SkipHtmlComment` 未闭合报 `EUI100`（K26，2026-11-08），静默吞 EOF 行为废止 | 已修复 | ✅ S6 |
| D13 | 依赖分析按符号解析，可见成员范围由 C# 语义决定（含基类成员；`[UiState]` 基类的 `IUiObservable` 覆盖基类属性） | `GetMembers()` 不扫描基类，基类信号既不被发现也不报错（[TemplateGenerator.cs:81](../src/EclipseUI.Compiler.Generator/TemplateGenerator.cs#L81)） | 基类信号按普通成员生成，值陈旧且无订阅 | S2 |
| D14 | ✅ C3：无转换器报 `EUI023`（`HasConverter` 区分），格式/范围/成员错仍 `EUI003` | 已修复（2026-11-03，[Analyzer.cs](../src/EclipseUI.Compiler/Analyzer.cs)） | ✅ C3 |
| D15 | 每个 `.ui` 独立增量、稳定输出名 | `Collect()` 全部文件 + `Compilation`（[TemplateGenerator.cs:29](../src/EclipseUI.Compiler.Generator/TemplateGenerator.cs#L29)） | 任一模板变化重跑全部；输出名随文件序号漂移 | S2（spec/02 §2.1/§8.2；§3.6.3 同） |
| D16 | ✅ G3：块布局透明落地——新增 `Element.IsLayoutTransparent`，`StackPanelBase` 对透明节点取零内边距 + 最近布局祖先的 `Gap`，空块不占间距槽（实现口径 D77） | 已修复（2026-11-05，[Panels.cs](../src/EclipseUI.Runtime/Panels.cs)、[Blocks.cs](../src/EclipseUI.Runtime/Blocks.cs)）；`IsVisualTransparent` 仍只管绘制 | 实测：条目间距 19.6 → 27.6（根 `Gap=8`），空块两侧 35.6 → 27.6 | ✅ G3（D77） |
| D17 | ✅ C4：组件标签/参数落地——`[UiParameter]`/`[UiContent]`/委托参数进目录（符号派生），`.ui` 组件标签按 spec/02 §4.12 生成（参数推送/委托事件/默认内容/生命周期）；具名插槽由 C6 收敛（D75/D76） | 组件不可写 | ✅ C4（2026-11-05，[design/14 §14.12.4](../design/14-control-template-contract.md)） |
| D18 | 块 Dispose 释放条目作用域 | ✅ S2：`ForEachBlock.Dispose`/条目移除逆序释放条目 `BlockScope`（[Blocks.cs](../src/EclipseUI.Runtime/Blocks.cs)） | 已修复（同 D51） | ✅ S2 |
| D19 | ✅ C3：附加属性在使用点报 `EUI031`（直到 G4 实现真实附加属性） | 已修复（2026-11-03，`Analyzer` 拦截任意 `Owner.Prop` 形式属性名） | ✅ C3 |
| D20 | `TextRuns` 走 `ITextContentHost.AddRun(string)` | ✅ S2：Emitter 生成 `((ITextContentHost)el).AddRun("...")`；接口与 `TextBlock` 实现已落地（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 已修复（同 D59） | ✅ S2 |
| D21 | 属性/事件/内容目标元数据缺失报 `EUI019`/`EUI020`/`EUI021` | Analyzer 不校验这些符号；Emitter 直接生成 `{Name}Property`/`{Name}Event`/`AddRun`，由 C# 报 CS0117 等 | 错误不在 `.ui` span 上 | C3 |
| D22 | ✅ C2：生成代码类型全用 `global::` 限定、不依赖 using（R4/14.8.2）；`@foreach` 生成的未限定 `List<...>` 已改为全限定，去掉 `ImplicitUsings` 依赖 | 已修复（2026-11-02） | ✅ C2 |

状态/反应式落地新增的偏差（与 15 §15.13.4 同一张表；D23/D27/D29/D31 与 D1/D10/D18/D29 互为映射）：

| # | 规范 | 当前实现 | 影响 | 收敛 |
|---|---|---|---|---|
| D23 | `P={expr}` 单向反应式（§3.3.3.1） | 一次性赋值（[Emitter.cs:125](../src/EclipseUI.Compiler.Generator/Emitter.cs#L125)） | 普通状态不更新 | S2 |
| D24 | `@bind-P` 双向（§3.3.3.2） | 只有 `:bind`（且右侧必须是 `Signal<T>` 对象） | 无法绑定普通属性/组件参数 | S3 |
| D25 | `Signal<T>` 写入零分配（15 §15.3.5） | 每次 `subs.ToArray()`（[Signals.cs:57](../src/EclipseUI.Runtime/Signals.cs#L57)） | GC 压力 | S1 |
| D26 | 同 Key 但 item 引用/值不同时重建条目（§3.3.6.3） | ✅ S2：`ForEachBlock` 同 Key 且 item 身份相同才复用，否则释放旧作用域重建（[Blocks.cs](../src/EclipseUI.Runtime/Blocks.cs)，同 D50） | 已修复 | ✅ S2 |
| D27 | 块 Dispose 释放 item scope（§3.3.4） | ✅ S2：`Dispose`/条目移除均释放条目作用域（[Blocks.cs](../src/EclipseUI.Runtime/Blocks.cs)，同 D51） | 已修复 | ✅ S2 |
| D28 | 块接受订阅工厂（`Subscribe` 回调），不限于 `params ISignal[]` | `SetCondition`/`SetItems` 只有 `params ISignal[]`（[Blocks.cs:39](../src/EclipseUI.Runtime/Blocks.cs#L39)、[:127](../src/EclipseUI.Runtime/Blocks.cs#L127)） | 无法订阅状态属性/可观察路径 | S2 |
| D29 | 双向走公开 setter（R3/R7） | `Bind.TwoWay` 用 `SetValue/GetValue`（[Component.cs:97-111](../src/EclipseUI.Runtime/Component.cs#L97)） | 绕过属性引擎 | S3 |
| D30 | 生成器测试可解析 partial 属性 | 生成器/测试 Roslyn 4.9.2，parser 报 CS0267（实测） | `CSharpGeneratorDriver` 无法覆盖状态生成器 | S1（升测试 Roslyn 或宿主集成测试） |
| D31 | 语义级依赖分析（15 §15.5） | 正则重写（[Emitter.cs:125](../src/EclipseUI.Compiler.Generator/Emitter.cs#L125)，同 D10） | 字符串/同名变量误改 | S2 |
| D32 | `IUiObservable`/`UiWatch`/`UiBinding` 运行时契约（15 §15.3） | 运行时不存在 | 无法实现 | S1/S3 |
| D33 | `[UiState]`/`[UiObservable]`/`[UiComputed]` 状态生成器（15 §15.2） | 不存在 | 普通类型不可观察 | S1/S4 |
| D34 | item 可观察属性与 item `@bind`（§3.3.6.2） | item 内禁止信号/`:bind`（原 `EUI014`） | 列表编辑不可写 | S2/S3 |
| D35 | 组件可观察参数（§3.3.3.5） | 只推荐 `Signal<T>` 参数 | 父更新不刷新子 UI | S3 |
| D36 | `EUI034`–`EUI044` | 只有 `EUI001`–`EUI033`（部分未实现） | 无诊断/误导 | S1–S4 |
| D37 | ✅ S2：v4 只映射赋值语句；`U{N}` 声明与 `U{N}();` 调用不映射（spec/02 §2.4 规则 5） | 与 spec 一致 | 已收敛 | ✅ S2 |
| D38 | `Text` 模型整段文本一个 updater，初次只写一次（spec/02 §4.7.2、15 §15.4.5） | ✅ S2：整段一个 `$"..."` 赋值，`U{N}`/`R{N}` 只写一次（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 已修复 | ✅ S2 |
| D39 | item 根 `P={item.X}` 生成定向更新（spec/02 §4.11.1） | ✅ S2：item 模板内动态属性生成 `R{N}` + `scope.AddRenderAction`（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 已修复 | ✅ S2 |
| D40 | `TextRuns` 静态段不建局部变量（spec/02 §4.8.1） | ✅ S2：静态段直接 `AddRun("...")`，不分配变量（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 已修复 | ✅ S2 |
| D41 | 生成文本形态固定：空分支不生成 `SetContent`、Key `(T t) => (object?)(expr)`、作用域名 `scope{N}`、首次调用紧跟定义（spec/02 §4.8/§4.10/§4.9） | ✅ S2：与 spec/02 形态一致（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 已收敛 | ✅ S2 |

D 表只记录"规范已定、代码未对齐"的差异，收敛阶段见末列；落地这些差异不改变本文的冻结语义。
D37–D41 描述的生成文本偏差已随 v4 S2 收敛，形态以 spec/02 为准。

### 3.5.4 v4 实现偏差台账（D42–D88）

v4 换轨后，**S1 已收敛（2026-10-30）、S2 已收敛（2026-10-30）、S3 已收敛（控件侧 2026-11-02、组件侧随 C4 2026-11-05）、C1 已收敛（2026-11-02，目录派生）、C3 已收敛（2026-11-03，字面量转换器/使用点诊断/保留语法）、C4 已收敛（2026-11-05，组件标签）、C5 已收敛（2026-11-05，Ref/使用点诊断）、C6 已收敛（2026-11-05，具名插槽）、G3 七个小切片全部收敛（块布局透明 2026-11-05/D77；§3.5.3 的 D16 随之关闭、失效迁移 2026-11-07/D78、按文件增量 2026-11-08、`#line` 列映射 2026-11-08/D79、EUI045 code-behind 冲突诊断 2026-11-08/D57/D80、诊断出口补全 2026-11-08/D74 ②/D81、EUI024 事件处理器签名匹配 2026-11-08/D82）、S4/S5 运行时硬化部分收敛（2026-11-08/D83）、G2 CPU 侧部分收敛（2026-11-08/D84）、**S6 语法对齐已收敛（2026-11-08/D85–D88 关闭：`@using`（K22/D85）、`@code` 单文件（K21/K23④/D86）、`else`/`else if`/`@switch`（K23③/K29/D87）、`@* *@` 与未闭合注释（K23/K26/D88，D12 随之关闭）、`EUI046`/`EUI047` 新诊断（K27/K29）、样例与约 94 例 golden 补 `@using` 一次性迁移，`dotnet test EclipseUI.slnx` 497 例全绿）**；
其余偏差按下列余项收敛：S4/S5（[spec/03 §13](../spec/03-reactivity-and-binding.md#13-阶段-s1s5) 的 S4；AOT/裁剪、最终审计、帧预算/分配预算门槛、CI——Element.Unmount 已随 D89/K33 收敛——旧语法清理已随 S6 提前完成（K27/D85–D88 ✅ 2026-11-08））、G2（GPU retained layer/真正局部重绘、目标机稳定 60fps 结论、像素探针 DSL、CPU/GPU 交叉一致性门禁）、G4（样式/主题/附加属性；泛型标签暂缓 K19、不排期）、IDE/DevTools。本表与 [spec/02 §12](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88) 是同一张台账，
任一侧更新必须同步另一侧；台账之外的差异视为实现缺陷。

| # | 偏差 | 目标（spec） | 阶段 |
|---|---|---|---|
| D42 | ✅ S2：块模板内动态写入生成 `void R{N}()` 局部函数 + `scope{N}.AddRenderAction(R{N})`；根级 `U{N}`/`Render()` ✅ S1 | spec/02 §4.3.2/§4.9–§4.10 | ✅ S2 |
| D43 | ✅ S1：`Invalidate`/`Render`/`ProcessInvalidations`/`ShouldRender` 已实现 | spec/02 §7 | ✅ S1 |
| D44 | ✅ S1：`Adopt`/`Track(Component)`/`TrackContent` 已实现 | spec/02 §7/§5.3 | ✅ S1 |
| D45 | ✅ S1：根级动态绑定提升 `__el{N}`/`__run{N}` 字段 | spec/02 §4.1.1/§4.3.1 | ✅ S1 |
| D46 | ✅ S1：失效队列、深度排序、迭代上限已实现 | spec/03 §2.4 | ✅ S1 |
| D47 | ✅ S2：`BlockScope` 提供 `AddRoot`/`AddRenderAction`/`AddDisposable`，块创建并传入模板 | spec/02 §7 | ✅ S2 |
| D48 | ✅ S2：`ConditionalBlock.SetCondition(Func<bool>)`；`SetContent(Action<BlockScope>)`；公开 `Refresh`；setter 只保存 | spec/02 §4.9/§7 | ✅ S2 |
| D49 | ✅ S2：`ForEachBlock.SetItems(Func<IEnumerable<T>?>)`；`SetTemplate(Action<T, BlockScope>)`；公开 `Refresh`；setter 只保存 | spec/02 §4.10/§7 | ✅ S2 |
| D50 | ✅ S2：条目复用要求 Key 相同 **且** item 身份相同（值类型按相等） | spec/03 §7.3（Key + item 身份） | ✅ S2 |
| D51 | ✅ S2：条目移除/块 `Dispose` 均逆序释放条目作用域资源 | spec/02 §5.4/spec/03 §2.8 | ✅ S2 |
| D52 | ✅ C4：组件参数推送生成 `var v = (T)(expr)` + `EqualityComparer<T>.Default` 相等保护 + 差异才写 setter 并 `Invalidate()` 子组件（根级 `U{N}`、块内局部 `R{N}`） | spec/02 §4.3.3 | ✅ C4 |
| D53 | ✅ S3：控件 `@bind-P` 生成 `UiBinding.TwoWay`（根级字段 + `Track`，块内局部 + `AddDisposable` + `R{N}` 刷新）；`readSource`/`writeSource` 表达式原样输出、不加括号；`P:bind`/`P:value` 兼容并报 EUI043 | spec/02 §4.4/§7 | ✅ S3 |
| D54 | ✅ C4：组件 `@bind-P` 生成「`{P}Changed` 回调赋值（build-once）+ 推送 `U{N}`/`R{N}`」；组件目标判定/内容体内 `@bind` 禁用已落地（EUI040）；内层推送形态与 §4.3.3 一致。回调 `{P}Changed` 的**类型**与参数严格匹配仍静态不可判，由 C# 编译错误兜底（D73） | spec/02 §4.4.3/§4.4.4 | ✅ C4 |
| D55 | ✅ S1：事件生成 `h{N}` 委托局部 + 包装内 `Invalidate()` | spec/02 §4.6 | ✅ S1 |
| D56 | ✅ C4：组件默认内容 build-once 落地——`List<Element>` 累加、单根 `content{N}.Count == 0 ? null : content{N}[0]`、内容子组件 `receiver.TrackContent`、内容内 `@if`/`@foreach` 展开为普通 C# 语句、内容内动态属性为 Build 快照、内容内 `@bind`/`Key`/裸文本分别报 EUI040/EUI010/EUI006 | spec/02 §4.12.3 | ✅ C4 |
| D57 | ✅ G3（2026-11-08）：`EUI045` 落地——常量分配、`TemplateGenerator.ProbeCodeBehindConflicts` 覆盖 spec/02 §10 五类触发（Build/Render、生成保留名、非 Component 基类、隐藏生成依赖成员）、诊断落在 code-behind 源声明、生成器不改名也不跳过输出；`CodeBehindConflictTests` 23 例。实现载体见 D80 | spec/02 §10、spec/02 §12 D80 | ✅ G3 |
| D58 | ✅ S1：hint name 为 `{ClassName}.{stableHash}.g.cs`（SHA-256 前 16 hex） | spec/02 §2.1 | ✅ S1 |
| D59 | ✅ S2：`ITextContentHost.AddRun(string)` 已实现；`TextBlock` 实现该接口，运行时不再需要手建 `TextRun` | spec/02 §4.8/§7 | ✅ S2 |
| D60 | ✅ S2：`ForEachBlock.UpdateChildren` 仅在根序列实际变化时改 `Children` | spec/03 §7.3 | ✅ S2 |
| D61 | ✅ S2：样例与 README 已迁移到普通 CLR 状态；`Signal<T>` 仅留作运行时遗留原语与历史章节说明，不参与模板生成 | spec/02/spec/03 | ✅ S2 |
| D62 | ✅ C2：`EUI040` 的目标可绑性此前只来自手写目录 `UiPropDef.Bindable`；C1 的派生目录已对每个模板可见属性给出符号级 `CanWrite`（public 可写 + `{Name}Property`），并有覆盖测试证明派生 ⊇ 手写标注；C2 起分析器改读派生 `CanWrite`（[Analyzer.cs](../src/EclipseUI.Compiler/Analyzer.cs) `propDef.CanWrite`） | spec/01 §4.2 | ✅ C2 |
| D63 | ✅ C1：运行时声明特性落地 `Runtime/UiAttributes.cs`（`UiContentKind`/`[UiContent]`/`[UiIgnore]`/`[UiParameter]`/`[UiSlot]`；`[UiTag]` 已于 K32 退役），内置控件加标注（`Panel`=Children、`Button`/`CheckBox`=Text→Label、`TextBlock`=TextRuns；`TextRun`/`ConditionalBlock`/`ForEachBlock`=`[UiIgnore]`） | design/14 §14.4.6/§14.4.4 | ✅ C1 |
| D64 | ✅ C1+**C2**：`Compiler.Metadata` 目录模型 + Roslyn 符号扫描器（`UiCatalogFactory.Build`）落地，纯函数、不抛异常，元数据问题进 `Problems`/`HiddenTags`。C1 只建派生、分析器与生成器行为不变；**C2 删除手写目录，分析器/生成器全部读派生目录**（`TemplateGenerator` 管道值 = `UiCatalogFactory.Build(compilation)`） | design/14 §14.5/§14.10.2/§14.12.2 | ✅ C2 |
| D65 | ✅ C2：`EclipseUI.Compiler.Metadata` 源码链接进生成器程序集（`Compile Include`，排除 Metadata 的 `Polyfills.cs`，诊断码常量经 Metadata→Compiler 单一定义）；测试项目只引用生成器避免重复类型身份 | design/14 §14.10.1/§14.12.2 | ✅ C2 |
| D66 | ✅ C1：`UiContentKind` 与目录内部 `UiContentModel` 数值一一对应，断言测试 `ContentKind_And_MetadataContentModel_NumbersMatch` | design/14 §14.4.4 | ✅ C1 |
| D67 | ✅ C1：失效级别守卫白名单收敛为唯一例外 `Element.IsHitTestVisible`；`Panel.ClipToBounds` 失效级别 `None`→`Render`（裁剪影响像素） | design/14 §14.11.1 | ✅ C1 |
| D68 | ✅ C1：派生目录属性项 6→17，差集逐条钉进快照测试 `DerivedCatalog_DiffAgainstHandCatalog_IsExactlyTheEnumeratedSet`（新增 `Padding`/`ClipToBounds`/`IsHitTestVisible` 继承项）；模板实际变宽在 C2 生效 | design/14 §14.12 | ✅ C1 |
| D69 | ✅ C2：8 个输入/焦点事件从静态宿主 `InputEvents` 迁入 `Element`（静态类删除，19 处调用改名），`CheckBox` 补 `CheckedChanged`；模板事件表面 1 → 9 个事件名（37 事件项），扫描快照可证 | design/14 §14.4.3/§14.12.2 | ✅ C2 |
| D70 | ✅ C1：`UiElementDef`/`UiCatalog` 手写结构相等（record 对字典成员按引用比较，会破坏 §14.10.3 管道值比较）；`CatalogValueEq` 按 Ordinal 稳定排序 + unchecked 哈希（netstandard2.0 无 `System.HashCode`） | design/14 §14.10.3 | ✅ C1 |
| D71 | `[UiIgnore]` 与 `[UiParameter]`/`[UiSlot]` 同时出现时，扫描器让 `[UiIgnore]` 生效并按 **EUI028** 记问题；design/14 §14.6.1 只写「不得」，没为这条分配诊断码，EUI028 原文只覆盖「参数声明非法」。C4 未处理（沿用 EUI028，不复用新码）；需要细化时另起新码并先记台账 | design/14 §14.6.1/§14.9.1 | 开放 |
| D72 | ✅ C2（语义修订）：golden 快照落成仓库内文件 `TestData/BuiltInSurface.golden.txt`（`ECLIPSEUI_UPDATE_SURFACE_GOLDEN=1` 再生）。§14.11.1 原文的「固定目录从快照反序列化」不采纳：快照改为**扫描结果的渲染评审门**（显式再生成 + diff 强制评审），纯结构测试各自现扫 `Compilation`（实测全扫 45 ms，反序列化反而多一道漂移源） | design/14 §14.11.1/§14.12.2 | ✅ C2 |
| D73 | ✅ C4（残余风险登记）：组件 `@bind-P` 只静态校验「存在 `{P}Changed` 委托参数」（EUI040），不校验其实参类型与 `P` 匹配——恰与 spec/01 §4.14 「缺失/类型不符 → EUI040」中的「类型不符」不可严格一致。类型不匹配由生成代码的 C# 编译错误兜底（经 `#line` 指回 `.ui`）；不要在运行时静默吞掉。若未来 Roslyn 类型推断下放到扫描期，可再收紧 | spec/02 §4.4.3 规则 1 / spec/01 §4.14 | ✅ C4 |
| D74 | ✅ G3（2026-11-08，② 收敛）：① 使用点 EUI019/EUI020/EUI021 落地（C5）——`UiElementDef.IsFromReferencedAssembly` 区分来源，只有引用程序集控件的 `def.Problems` 在用点暴露（`PropertyNotBindable`→EUI019、`EventFieldMissing`→EUI020、`ContentTargetInvalid` 接管 EUI006→EUI021）；源项目控件仍落 EUI002/EUI006。`Ref`（EUI029）同切片落地。② 声明处出口落地：`UiMetadataProblem` 携带纯数据 `UiSourceLocation`，生成器注册目录级 `SourceOutput` 把源项目 problem 报在 C# 声明处（成员级取 `def.Problems`、类型级取 `catalog.Problems`），与逐文件管道解耦、每条只报一次；标签解析失败按类别分流 EUI016/EUI017/EUI018/EUI030/EUI031，只有真正未知落 EUI001。EUI024 已落地（2026-11-08，D82）。实现口径见 [spec/02 §12 D81](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)，`MetadataOutletTests` 14 例 | design/14 §14.5.4/§14.9.1/§14.12.5、spec/02 §4.14/§12 | ✅ C5 ① / ✅ G3 ② |
| D75 | ✅ C6（解析口径两级）：自身插槽的解析——分析器按「目录显示名与 `.ui` 文件名短名匹配」投影自身 def（静态前端无符号信息）；生成器用 code-behind **符号**沿基类链读 `[UiSlot]` 传给分析器/发射器作权威（code-behind 类在源码里不派生 `Component`，目录不会有它的 def）。两级在正常房间结构下指向同一组插槽；目录匹配失败而符号可用时生成侧仍有效 | design/14 §14.12.6、spec/02 §4.12.5 | ✅ C6 |
| D76 | ✅ C6（契约补写）：基链同名**成员**的胜出规则原文未定。Roslyn 的 `GetAttributes()` 不把特性带进 override，一律按名字去重会让 `public override Element? Header`（不写特性）静默丢掉基类的 `[UiSlot]`/`[UiParameter]`，不去重又把同一 override 误报成重复插槽（EUI028）。实现定为：派生侧显式写了模板特性或 `[UiIgnore]` 才算覆盖，否则基类声明继续生效；已回写 design/14 §14.10.2 与 spec/01 §4.15 | design/14 §14.10.2/§14.6.1 | ✅ C6 |
| D77 | ✅ G3（契约补写 + 实现口径）：块布局透明落地为「保留块节点 + 采用祖先排布参数」，不是把条目摊平进父容器子序列——摊平会让块 `FinalRect` 与子元素坐标系脱钩，绘制偏移/`PointFromRoot`/命中/脏区四条路径都要改；保留节点则一行不动且排布等价。新增 `Element.IsLayoutTransparent`，`StackPanelBase` 对透明节点取零内边距 + 最近非透明布局祖先的 `Gap`，零尺寸透明块退出间距计数但仍 arrange 到零矩形。补写三条原文未定规则：块自身 `Gap`/`Padding` 不参与布局、空块（含嵌套空块）不占间距槽、空块 `FinalRect` 必须刷新。实测条目间距 19.6 → 27.6（根 `Gap=8`）、空块两侧 35.6 → 27.6；`BlockLayoutTests` 10 例 + `TaskListEndToEndTests` 2 例，421 例全绿。D16 关闭；G4 的 Grid/WrapPanel 另定透明口径 | spec/01 §4.10/§4.11、spec/02 §12 D77、本文 D16 | ✅ G3 |
| D78 | ✅ G3（契约补写 + 实现）：`Adopt` 接管不迁移子组件原根队列里已入队的失效（所有权变更后该队列不再被排空，接管前 `Invalidate()` 静默丢弃；GalleryApp 空 Tasks 卡根因）；`Depth` 接管时缓存、链长变化后残留旧深度，可能把孙排在父前。实现：`Adopt` 按入队顺序 drain 子队列重入新根（按待渲染标记合并），`Depth` 改所有权链实时计算。契约补写 spec/03 §2.4/§2.7；`ComponentOwnershipTests` 5 例 | spec/03 §2.4/§2.7、spec/02 §12 D78 | ✅ G3 |
| D79 | ✅ G3（契约补写 + 实现）：`#line` 由行型 `#line N "path"` 换成 span 型 `#line (sl, sc) - (el, ec) "path"`。实测 Roslyn 4.9.2：列平移 `sc−1` 只作用于紧接的一行、end 不参与单行诊断换算、start 必须严格早于 end（否则 `CS8939` 让整段映射失效）。`Emitter.Mapped` 按「生成缩进 + 用户 token 偏移」左移 `sc` 并钳制 ≥ 1（前缀过长时列退化为生成列、行号仍精确），空 span/钳制后给 end 补 1 列；`MappedToken` 自动定位表达式偏移，35 个调用点由行号改传 `TextSpan`。补写 spec/02 §2.4 规则 9/10 与 §11 等价口径（旧/新语法别名改为剥离 `#line` 后比对）。`LineSpanMappingTests` 5 例 + `GeneratorContractTests` 3 处断言同步 | spec/02 §2.4/§12 D79 | ✅ G3 |
| D80 | ✅ G3（实现口径补写）：EUI045 的「诊断位置在 code-behind 声明」原文未定载体。实现用纯数据 `UiSourceLocation`（路径 + span + 0 基行列）走逐文件 facts，`Report` 以 `Location.Create(path, span, lineSpan)` 重建外部文件位置（不把 `SyntaxTree`/`Location` 带进增量缓存）；保留名 = `__el/__cmp/__blk/__run/__bind/U/R` + 数字后缀，生成依赖成员白名单 = `Invalidate/Mount/Track/TrackContent/AddDisposable`，基类白名单 = 省略/`Component`/`EclipseUI.Component`/`global::EclipseUI.Component`；EUI045 报 Error 但不阻止生成输出，其它错误诊断仍短路。`CodeBehindConflictTests` 23 例 | spec/02 §10/§12 D80 | ✅ G3 |
| D81 | ✅ G3（实现口径补写）：D74 ② 原文未定位置载体/去重/码映射。实现：① `UiMetadataProblem` 增加可选纯数据 `UiSourceLocation`（成员符号 `Locations` 优先、类型声明兜底；引用程序集无源位置 → null），进 record 值相等/哈希；② 目录级 `RegisterSourceOutput(catalogProvider)` 与逐文件管道解耦，同一 problem 只报一次，`Distinct()` 去重 catalog/def 两处重复项；③ 源项目控件的 EUI019/EUI020/EUI021 problem 按 §14.9.2 折叠为 EUI026，EUI026/EUI028/EUI031 保持原码；④ 诊断不阻断生成输出，`Report` 对无 path 的内存语法树退化为 `Location.None`；⑤ 逐文件管道 `WithTrackingName("PerFileGeneration")`，增量断言不再混读所有 `SourceOutput` step；⑥ 标签码分流只改 `Analyzer.VisitElement` 的 code，消息与 span 不变。`MetadataOutletTests` 14 例。口径正本在 spec/02 §12 D81 | spec/02 §12 D81、design/14 §14.9.1/§14.9.2 | ✅ G3 |
| D82 | ✅ G3（2026-11-08，实现口径）：EUI024 事件处理器签名匹配落地——常量 `UiDiagnostics.EventHandlerSignature` 早已分配；`TemplateGenerator.ValidateEventHandlerSignature` + `TemplateProbe.IsHandlerCompatible`（复用 `ProbeTree` 合成 partial 方法体）借 Roslyn 把处理器表达式放进 code-behind 符号上下文，仅当「表达式自身语义干净 + 对目标委托的转换确定失败」时在 `.ui` 属性值报 Error 并短路生成输出（与 EUI004 一致）；符号未解析/重载失败/`dynamic`/表达式内部错误静默，交回生成代码经 `#line` 由 C# 报错（spec/02 §2.4 规则 8 不重复报告）。控件事件复用 `Emitter.EventHandlerType`（`internal static`）、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`，保证诊断与生成代码类型判定一致。`EventHandlerSignatureTests` 16 例 | spec/02 §4.6.1 规则 7、§9 T20、design/14 §14.11.2 | ✅ G3 |
| D83 | 部分收敛（S4/S5，2026-11-08）：`PropertySystem.cs` 新增 `IValueBox`/`ValueBox<T>`，`Element._localValues` 由 `Dictionary<StyledProperty, object?>` 改为 `Dictionary<StyledProperty, IValueBox>`，重复赋值只改泛型字段、消除值类型装箱；`Component.Dispose` 在锁内清 `_pendingRenders`/`_pendingSet`。`RuntimeHardeningTests` 7 例（分配预算、继承/绑定/通知语义、泄漏回归）。**明确未做**：`Element.Unmount/Detach`（design/14 明确 v1 不提供、G4 才补）、AOT/裁剪配置、旧语法清理、最终审计清单、帧预算/分配预算门槛、CI 配置、S5 基准门槛 | spec/03 §13.1、design/09 §9.3 G8/S5 | 部分收敛（S4/S5） |
| D84 | 部分收敛（G2 CPU 侧，2026-11-08）：`PixelGoldenTests` 4 例 + `TestData/PixelGolden.baseline.txt`（纯图元、无字体依赖的 CPU 像素 golden：`ContentHash` 全图门禁 + 探针 ±2 容差 + `ECLIPSEUI_UPDATE_GOLDEN=1` 生成开关 + 失败输出实际 PNG 到 TestResults）；`SceneGraph/RetainedFrameCompositor.cs`（CPU 侧 retained 合成原型：持有上一帧 `RenderTarget`，按 `FrameDamage` 只 blit 脏区，断言脏区外逐字节不变、与整帧重绘等价）；`tools/bench-60fps.ps1` 与 `design/reports/target-machine-60fps-template.md`。**明确未做/边界**：原型只是合成层、不减少光栅化工作量，真正局部重绘需 `IRenderBackend.Render` 增加脏区/scissor 重载且 VelloSharp 0.5.0-alpha.3 未提供公开 API；GPU surface 的 retained layer 依赖 HWND + wgpu device 本机无法验证；「稳定 60fps」结论依赖目标机真实显示器（本机 29Hz 虚拟显示），只给可复现测量程序与报告模板；像素探针 DSL（render-gate-2026-11 backlog #7）仍缺 | design/09 §9.3 G2、design/reports/render-gate-2026-11.md | 部分收敛（G2 CPU 侧） |
| D85 | 规范先行（K22，2026-11-08）：模板 `@using {Ns}` 已定为合法指令、生成文件须按模板顺序输出普通 `using {Ns};`、标签只在导入命名空间内解析且内置控件无豁免（未导入报 `EUI001` + 缺 using 建议）。**当前实现**：`Lexer.ParseDirective` 仍把 `@using` 归 `EUI009`，`Emitter` 不输出任何 using，`UiCatalog.Resolve` 仍是全局短名字典、与命名空间无关；spec/02 §2.2/§2.5 旧口径已加注取代关系。收敛条件：P1 指令表、P2 `UiUsingDirective`、P3 文件头与目录过滤、P6 全部样例 / 约 94 例 golden 一次性迁移补 `@using` 并回归 | K22、spec/01 §1.5、design/16 §5.3 | ✅ S6（2026-11-08 收敛） |
| D86 | 规范先行（K21/K23 ④，2026-11-08）：`@code { }` 为合法顶层节点，位置不限、可多个块、书写顺序不影响成员可见性，成员合并进同一 partial 类并与 code-behind 并存，`EUI005` 放宽。**当前实现**：`@code` 撞 `EUI009`，`EUI005` 仍强制同名 `.cs`，`TemplateProbe.ProbeTree` 只注入 code-behind 声明。收敛条件：P1 块体捕获、P2 `UiCodeBlock` + 根层例外、P3 成员透传 + **先全量收集再建探针 Compilation**（否则前置 `{expr}` 误报 `EUI007`/`EUI024`）+ `EUI045` 扩到 `@code` 成员、P6 单文件端到端用例与底部 `@code` 样例 | K21、K23、spec/01 §1.5、design/16 §5.2/附录 B | ✅ S6（2026-11-08 收敛） |
| D87 | 规范先行（K23 ③，2026-11-08）：`else`/`else if`（不带 `@`）与 `@switch`（`case`/`break`/`default`）纳入目标语法，`EUI008` 停用、新码从 `EUI046` 起。**当前实现**：只有单分支 `@if`，分支续接撞 `EUI100`，`@switch` 报 `EUI008`。收敛条件：P1 续接与 case 头、P2 分支/switch 节点、P4 `ConditionalBlock` 多分支（`AddBranch`/`SetDefault`）与 `SwitchBlock<T>`（草案见 design/16 附录 B）、P5 停用码零出现 + 诊断快照；`case` 内嵌 `@foreach` 与 `@for`/`@while` 属 Q3/P4 待定 | K23、spec/01 §1.5、design/16 §3.1/附录 A | ✅ S6（2026-11-08 收敛） |
| D88 | 规范先行（K23 / design/16 §3.1，2026-11-08）：新增 `@* ... *@` 编译器丢弃注释，未闭合报 `EUI100`（顺带关闭 **D12**）；`<!-- -->` 按 spec/01 §8 规则 2 给弃用期，具体形态待 Q6。**当前实现**：只识别 `<!-- -->`，且 `Lexer.SkipComment` 未闭合时静默吞到文件尾（D12 开放）。收敛条件：P1 词法、`TokenizerTests` 与未闭合注释用例、P6 样例迁移；Q6 未拍板前不删除 `<!-- -->` 支持 | K23、D12、design/16 §3.1 | ✅ S6（2026-11-08 收敛） |
| D89 | ✅ S4/G4：Element.Unmount 卸载链路落地（K33）——UnmountedEvent（Direct，[UiIgnore] 退出事件表面）、Unmount() 子树级钩子与事件、组件根订阅自 Dispose（Dispose 退订防泄漏回归）、FocusManager.ClearFocusIfDetached 接入两个宿主；UnmountTests 9 例，全量 575 例（Rendering 1 既有红除外）全绿 | spec/03 §13.1、design/14/15 | ✅ S4/G4（2026-11-09） |
| D90 | ✅ G4 布局：**WrapPanel 流式布局**——`Orientation`（Horizontal/Vertical）+ `Gap`；主轴装箱换行、期望尺寸取最长行（首版漏 maxMain 已修）、越界子项独占一行不裁剪；目录表面 5→6 标签、17→22 属性项、37→44 事件项，golden 重生成（+15 行）；`WrapPanelTests` 5 例（水平/竖直换行、Gap+Padding、不换行、越界独占、空面板） | design/09 §9.3 G4 布局补全 | ✅ G4（2026-11-09） |

- 每项收敛必须同时删除对应的 v3 路径；不得两套模型并存。
- 新增偏差必须登记编号（从 D89 起）并注明目标阶段；不得只改代码不改台账。
- **2026-11-08 补充（K23）**：D85–D88 是「决策已进规范正文、代码仍是 v2」的规范先行偏差，目标阶段统一为 **S6 语法对齐**（design/09 §9.5/§9.6），与 [spec/02 §12](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88) 双向同步；未拍板的 Q3/Q4/Q6/Q7 细项不登记偏差。
- §3.5.3 是 v3 台账：以 `[UiState]`/`Signal<T>`/`UiWatch`/依赖分析/可观察路径为目标的条目（D9/D10/D13/D23–D29/D31–D36/D39）
  随 v4 换轨作废，不再按原阶段实现；其中代码差距已在 v4 语义下重新登记并按阶段收敛
  （D23→D42/D45、D24→D53/D54、D26→D50 ✅ S2、D18/D27→D51 ✅ S2、D32→D53、D36→spec/03 §11 诊断表；D33/D35 无替代项）。
  D37/D38/D40/D41 描述的是 v3 生成文本；v4 形态按 spec/02 判定，已随 S2 落地（U/R、单次插值、静态段不建变量、空分支省略 `SetContent`）。
  其余词法/诊断/布局/组件标签/确定性条目不受影响。

## 3.6 编译管线与生成契约

### 3.6.1 管线与生成器分工

> **v3 历史**：下图是 v3（依赖订阅）管线。v4 管线以 [spec/02 §2](../spec/02-generated-code-contract.md) 为准：
> 词法 → 语法 → 语义分析（无 `DepAnalyzer`/`StateGenerator`）→ 生成 `Build`/`Render`/`U{N}`/`R{N}`。
> 本节后续"当前实现"描述均指 v3 代码（v4 差距见 §3.5.4）。

```
.ui AdditionalFiles ─┐
                     ├→ Lexer       词法：文本/标记两模式、{expr}、@directive、@bind-、注释删除
                     │→ Parser      AST + span；闭合检查；文本规范化
                     │→ Analyzer    目录解析、属性/事件/@bind 校验、字面量转换、内容模型、Key
                     │→ DepAnalyzer 表达式根读取分类、可观察路径、全有或全无退化（S2）
                     │→ Emitter     Build() 结构 + 定向 updater + 订阅接线 + #line
                     │→ {ClassName}_{index}.g.cs
.cs 声明 ────────────┴→ StateGenerator（S1/S4）
                        [UiState]/[UiObservable]/[UiComputed] → {Type}.EuiState.g.cs
```

- 全部是 Roslyn 增量源生成器：`.ui` 作为 AdditionalFile 进入编译（[TemplateGenerator.cs:27](../src/EclipseUI.Compiler.Generator/TemplateGenerator.cs#L27)）。
- **两个生成器互不读取对方输出**：模板生成器按用户声明（`partial` 属性 + 特性 + 语义模型）判定可观察性，
  只生成对 `IUiObservable`/`UiWatch` 等公开 API 的调用；状态生成器负责实现这些成员。最终编译合并两者。
  因此模板生成器不得依赖 `{Type}.EuiState.g.cs` 里的字段名/私有成员。
- 当前 Analyzer 不引用 Roslyn 语义模型；S2 起接入（依赖分类、`@bind` 源校验、目标符号校验）。
- 注释在 Lexer 删除，不进入 AST；当前**没有 trivia、没有 pretty-printer**。
- 当前管线是 `Collect()` 全量文件 + Compilation（D15）；输出名含文件序号，文件增删会改变序号。
- 目录由符号派生（C2 起，14.10），当前 5 个内置标签；C1 先建派生目录、不改行为。

### 3.6.2 生成代码形态（v3 历史；**已被 spec/02 取代**）

> **v4 换轨**：本节的 `UiWatch`/`subscribeSource`/依赖订阅形态已废止；生成代码的规范形态以
> [spec/02](../spec/02-generated-code-contract.md) 为准（组件级 `Invalidate`/`Render`/`U{N}`、块 `Refresh`、`UiBinding` v4）。
> 下表仅保留 v3 生成形态的历史记录，MUST NOT 作为实现依据。

| 模板 | 生成代码（简化） | 说明 |
|---|---|---|
| `<Button />` | `var el0 = new global::EclipseUI.Elements.Button();` | public 无参构造 |
| `Gap="8"` | `el0.Gap = 8d;` | 编译期转换 |
| `Gap={vm.Gap}` | `void U0() => el0.Gap = vm.Gap; U0(); Track(global::EclipseUI.UiWatch.Subscribe(this, path, U0));` | 单向反应式（S2）；不可观察时只留 `U0()` + `EUI036` |
| `@bind-Checked={vm.Show}` | `Track(global::EclipseUI.UiBinding.TwoWay(el0, global::...CheckBox.CheckedProperty, readTarget: () => el0.Checked, writeTarget: v => el0.Checked = v, readSource: () => vm.Show, writeSource: v => vm.Show = v, subscribeSource: h => global::EclipseUI.UiWatch.Subscribe(this, path, h)));` | 双向（S3）；源值胜出 |
| `Checked:bind={show}` | `Track(global::EclipseUI.UiBinding.TwoWay(el0, global::...CheckBox.CheckedProperty, readTarget: () => el0.Checked, writeTarget: v => el0.Checked = v, readSource: () => show.Value, writeSource: v => show.Value = v, subscribeSource: h => show.Subscribe(h)));` | 旧语法，`EUI043` Warning（S5 移除） |
| `OnClick={() => n++}` | `el0.AddHandler(global::...Button.ClickEvent, () => n++);` | 事件表达式 |
| 文本静态 | `el0.Label = "OK";` | `Text` 模型 |
| 文本插值（状态） | `void U0() { el0.Label = $"Count: {Count}"; } U0(); Track(((global::EclipseUI.IUiObservable)this).Subscribe(nameof(Count), U0));` | 状态属性直接读（S2） |
| 文本插值（`Signal<T>`） | `void U0() { el0.Label = $"Count: {count.Value}"; } U0(); Track(count.Subscribe(U0));` | 兼容形态 |
| TextRuns | `var run0 = ((global::EclipseUI.ITextContentHost)el0).AddRun("Hi ");` | 每段一个 run（C2） |
| 子元素 | `el0.Children.Add(el1);` | `Children` 模型 |
| `@if` | `var blk0 = new global::EclipseUI.ConditionalBlock(); parent.Children.Add(blk0); blk0.SetCondition(() => Show, h => ((global::EclipseUI.IUiObservable)this).Subscribe(nameof(Show), h)); blk0.SetContent(subs0 => { ... return new Element[] { ... }; }); Track(blk0);` | 依赖为空时省略 subscribe 参数（S2） |
| `@foreach` | `var blk0 = new global::EclipseUI.ForEachBlock<T>(); parent.Children.Add(blk0); blk0.SetItems(() => Tasks, h => global::EclipseUI.UiWatch.Subscribe(this, path, h, watchCollection: true)); blk0.SetKeySelector(t => t.Id); blk0.SetTemplate(t => { ... return new BlockScope(...); }); Track(blk0);` | 集合路径 + INCC（S2/S4） |
| 组件（C4） | `var c0 = new global::App.Card(); c0.Title = ...; Track(c0); c0.Mount(); parent.Children.Add(c0.Root);` | 参数→内容→Track→Mount→挂根 |
| 组件可观察参数（S3） | `void U0() => c0.Title = vm.Name; U0(); Track(UiWatch.Subscribe(this, path, U0));` | 不重跑 `Build()`，子组件内部 watcher 更新 |

- 块与绑定的订阅作用域按 §3.3.4 选择 `Track` / `subs.Add` / `itemSubs.Add`；表中以根上下文示意。
- 生成代码只用公开运行时 API（R4），不写反射/字符串查找；类型全用 `global::` 限定（D22 已在 C2 修复）。
- 变量/函数编号按遍历顺序固定，同一输入生成相同文本（R6/14.8.4）。
- 表达式行带 `#line`，随后 `#line hidden`（[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)）。
- ✅ G3（D79，2026-11-08）：指令已由行型换成 span 型 `#line (sl, sc) - (el, ec) "<path>"`，
  口径见 spec/02 §2.4 规则 9/10；本节其余生成形态仍是 v3 历史记录。

### 3.6.3 增量、确定性与工具链

- **S2 目标**：按 `.ui` 文件的 `IncrementalValuesProvider` 生成；目录由 `IncrementalValuesProvider<UiCatalog>` 提供，
  依赖管道值比较，生成器不持可变静态状态（14.10.3，14.14 决策 D14）。
- 输出名改为稳定名（类名 + 路径哈希/相对路径），不因文件顺序增删漂移。
- LSP（G5）复用同一 `FrontEnd`/`UiCatalog`/诊断；悬停/补全只显示符号可派生信息，
  默认值与 `Invalidate` 是运行期事实（14.14 决策 D13）。S1 起补全 `[UiState]` 属性与 `@bind-P` 目标。
- 编译期不执行用户程序集/转换器（R1）；运行时零反射、零字符串解析（R2）。
- 生成的绑定/订阅只调用公开 API（R7）；每个订阅有唯一作用域所有者，卸载后无残留（R8）；
  依赖分析不执行用户代码，输出只依赖输入（R9，15 §15.5.7）。
- 诊断质量是生命线：每个错误带 span 与修复建议；生成器遇到非法元数据不崩溃，按使用点报诊断（R5）。

## 3.7 逃生舱

模板不是能力天花板，但也不承诺"95% 场景"这类未测量的数字。能力边界用规则表达：

- **code-behind 是正式逃生舱**：复杂条件/循环/异步/局部变量/类型分发写在 C# 方法或属性里，模板用 `{Helper(...)}` 调用。
- **C# 直接构造**：`Component.Build()` 或命令式代码使用与生成代码完全相同的公开 API（元素构造、属性、`Children.Add`、
  `AddHandler`、`UiBinding.TwoWay`、`UiWatch`、`ConditionalBlock`/`ForEachBlock`）。生成代码是公开 API 的展开，不存在第二套运行时。
- **状态也可直接在 C# 中用**：`[UiState]` 实例的 `IUiObservable.Subscribe` 与 `Signal<T>` 都对 C# 开放；
  命令式代码与模板共享同一写契约（15 §15.3）。
- **`Ref`（G3）**：需要长期持有元素/组件实例时，用 `Ref` 赋给 code-behind 字段；在此之前用 C# 直接构造元素/组件
  并保存为字段（与生成代码同一公开 API）。
- **能对等就不模板化**：模板无法表达的任意语句、`switch`、`try`、`async` 流程，用 C# 渲染函数；不要在模板里加入
  半成品控制流语法。
- 对等性是测试目标而非口号：R4 要求每个模板构造都有等价公开 API；C1 的出口标准要求派生目录 ⊇ 手写目录
  并逐条评审差集（[14 §14.12](./14-control-template-contract.md)）。

## 3.8 变更规则

1. 新增语法/修饰符/指令：先在本表登记状态与诊断，再改 Parser/Analyzer/Emitter；不得先实现后补文档。
2. 已实现语义不得改义；必须改时走新语法 + 弃用期，并在 03/09/14 同步记录。
3. 语法项从 G3/G4/S1–S5 转 ✅ 时，必须同时更新 §3.5.2、§3.5.3 的偏差行与 14.12 的阶段表。
4. 诊断码不复用；新增从 `EUI046` 起（`EUI034`–`EUI044` 中多数已停用，见 [spec/03 §11](../spec/03-reactivity-and-binding.md#11-诊断)；其余见 14.9.1）。
5. 每次变更跑：golden 生成文本、诊断快照、样本项目（CounterApp/TaskList）编译与运行。

## 附录 A：完整示例（v3 目标语义 · 历史）

下面是同一份 TodoList 在 v3 语义下的完整写法（S2/S3 目标）。它演示普通状态、双向、条件与列表，每段之后给出依据。
**v4 换轨后本附录仅作历史记录**；规范示例以 [spec/01 附录 A](../spec/01-template-syntax.md#附录-a完整示例) 与
[spec/02 附录 A](../spec/02-generated-code-contract.md) 为准。
当前实现下的等价写法见 A.6（可编译、可运行）。

### A.1 `TodoList.ui`

```xml
<StackPanel Gap="8">
    <TextBlock FontSize="24">待办清单</TextBlock>

    <CheckBox @bind-Checked={ShowCount}>显示任务数量</CheckBox>

    @if (ShowCount) {
        <TextBlock FontSize="14">共 {Tasks.Count} 项</TextBlock>
    }

    <Button OnClick={() => AddTask()}>添加任务</Button>

    @foreach (var t in Tasks) {
        <StackPanel Key={t.Id} Gap="4">
            <CheckBox @bind-Checked={t.Done}>{t.Title}</CheckBox>
        </StackPanel>
    }
</StackPanel>
```

### A.2 `TodoItem.cs`

```csharp
using EclipseUI;

namespace Examples;

[UiState]
public partial class TodoItem
{
    public int Id { get; init; }                 // 普通属性：用于 Key，不得可观察
    public partial string Title { get; set; }
    public partial bool Done { get; set; }
}
```

- `Id` 是普通 init 属性，`Key={t.Id}` 合法（`EUI039` 只拦可观察依赖）。
- `Title`/`Done` 是可观察状态：item 文本与 `@bind-Checked` 都能随 item 数据更新。

### A.3 `TodoList.cs`（code-behind）

```csharp
using System.Collections.ObjectModel;
using EclipseUI;

namespace Examples;

[UiState]
public partial class TodoList : Component
{
    public partial ObservableCollection<TodoItem> Tasks { get; set; }
    public partial bool ShowCount { get; set; }

    private int _nextId = 3;

    public TodoList()
    {
        Tasks = new ObservableCollection<TodoItem>
        {
            new() { Id = 1, Title = "读 03 语言规范" },
            new() { Id = 2, Title = "补 EUI034" },
        };
    }

    private void AddTask()
    {
        Tasks.Add(new TodoItem { Id = _nextId++, Title = $"任务 {_nextId}" });
    }
}
```

- `Tasks` 是状态属性 + `ObservableCollection<T>`：属性替换与集合增删都会通知。
- `ShowCount` 是组件状态：CheckBox 双向写它，`@if` 订阅它。
- 状态生成器为两个属性生成隐藏 `Signal<T>` 字段与 `IUiObservable` 实现（15 §15.2.4）；模板部分不引用这些私有成员。
- 构造函数在 `Mount()`/`Build()` 之前执行，Build 读到的是初始化后的值。

### A.4 语法对照

| 写法 | 语义 | 依据 |
|---|---|---|
| `Gap="8"` / `FontSize="24"` | 编译期字面量转换，一次性赋值 | §3.4 |
| `@bind-Checked={ShowCount}` | 双向绑定；源值胜出；两侧走公开 setter | §3.3.3.2 |
| `@if (ShowCount) { ... }` | 条件订阅状态；分支切换销毁/重建 | §3.3.6.1 |
| `共 {Tasks.Count} 项` | 文本插值反应式；集合属性 + INCC | §3.3.5 |
| `@foreach (var t in Tasks)` | 集合是状态属性/可观察路径；INCC 增删刷新 | §3.3.6.2 |
| `Key={t.Id}` | item 普通属性；不得依赖可观察状态 | §3.3.6.3 |
| `@bind-Checked={t.Done}` | item 可观察属性双向；订阅归 item 作用域 | §3.3.3.2 / §3.3.6.2 |
| `{t.Title}` | item 文本 updater；同 Key 同 item 复用并重跑，item 引用变化重建 | §3.3.6.4 |
| `OnClick={() => AddTask()}` | Build 时 `AddHandler`，不产生订阅 | §3.3.7 |

### A.5 目标生成代码节选

完整、逐字节的目标生成文本见 [spec/02 附录 A](../spec/02-generated-code-contract.md)；
本文不再保留简化副本，避免两份生成文本互相漂移。

> 旧版本此处示例的 `new[]`、`nameof` 未带声明类型、`null` updaters、把 `sub1` 放进数组、
> `void U1()` 捕获旧 item 等形态均已废止；勘误见 [spec/README](../spec/README.md) E1/E2。

### A.6 当前实现的等价写法（S1 前可运行）

在 S1/S2 落地前，同一功能用 `Signal<T>` + `:bind` 写：

```xml
<StackPanel Gap="8">
    <TextBlock FontSize="24">待办清单</TextBlock>

    <CheckBox Checked:bind={showCount}>显示任务数量</CheckBox>

    @if (showCount) {
        <TextBlock FontSize="14">共 {tasks.Value.Count} 项</TextBlock>
    }

    <Button OnClick={() => AddTask()}>添加任务</Button>

    @foreach (var t in tasks) {
        <ListBoxItem Key={t.Id}>
            <TextBlock>{t.Title}</TextBlock>
        </ListBoxItem>
    }
</StackPanel>
```

```csharp
using System.Collections.Generic;
using EclipseUI;

namespace Examples;

public sealed class TodoItem
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
}

public partial class TodoList
{
    public readonly Signal<List<TodoItem>> tasks = new(new List<TodoItem>
    {
        new() { Id = 1, Title = "读 03 语言规范" },
        new() { Id = 2, Title = "补 EUI034" },
    });

    public readonly Signal<bool> showCount = new(false);

    private int _nextId = 3;

    private void AddTask()
    {
        var list = new List<TodoItem>(tasks.Value)
        {
            new() { Id = _nextId, Title = $"任务 {_nextId}" },
        };
        _nextId++;
        tasks.Value = list;
    }
}
```

- 该写法在 v2 生成器下编译通过（0 警告、0 错误；`net10.0`、`LangVersion latest`、`ImplicitUsings enable`）。
- 语义差异：`tasks.Value.Count` 只在整表替换时更新；item 内文本不随 `Title` 变化刷新（v2 限制）。
- S3 起 `Checked:bind` 报 `EUI043` Warning；迁移步骤见 15 §15.10.2 与附录 B。

### A.7 迁移与构建注意

| v2 写法 | v3 写法 | 说明 |
|---|---|---|
| `Signal<List<T>> tasks` | `[UiState] partial ObservableCollection<T> Tasks` | 集合增删通知（S4）；也可保持根替换 |
| `tasks.Value.Count` | `Tasks.Count` | 状态属性直接读；INCC 更新 |
| `Checked:bind={showCount}` | `@bind-Checked={ShowCount}` | `:bind` 弃用 |
| `P={sig}` | `P={State}` | `P={expr}` 从一次性变为单向反应式（15 附录 B） |
| `@foreach (var t in tasks)` | `@foreach (var t in Tasks)` | 集合路径；签名不变 |
| item 内 `Signal<T>` | item `[UiState]` 属性 | 支持 item 内更新/双向 |

> 构建注意：~~当前 `@foreach` 的生成代码使用未限定的 `List<...>`~~ 已修复（C2，2026-11-02）：
> 生成代码类型全部 `global::` 限定，不依赖项目 `ImplicitUsings`。
