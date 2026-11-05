# 01 · 模板语法规范

> 状态：v2 定稿（2026-10-29），对应 **v4 失效重算模型**。
> 本文是 `.ui` 模板语言的**规范正本**，定义词法、句法、语义、字面量与诊断触发点。
> 取代 [design/03](../design/03-template-engine.md) §3.1–§3.5 的规范地位；03 保留 v3 偏差台账（D1–D41）、
> v4 台账（§3.5.4）与历史记录。
>
> 关联：
> [03 反应式与绑定契约](./03-reactivity-and-binding.md)（失效、渲染、块刷新、双向绑定）、
> [02 生成代码契约](./02-generated-code-contract.md)（生成文本形态）、
> [design/14](../design/14-control-template-contract.md)（控件/组件元数据）、
> [design/05](../design/05-layout-rendering-input.md)（布局/渲染/输入）。
> [design/15](../design/15-state-and-reactivity.md) 的 v3 状态契约已降为历史记录，不再作为实现依据。

## 0. 文档约定

### 0.1 范围

| 本文定义 | 本文不定义（去哪看） |
|---|---|
| `.ui` 词法、EBNF、元素/属性/文本/指令句法 | 控件如何声明标签、属性、事件、内容模型（design/14） |
| 属性值形态、求值时机（Build/渲染/块刷新）、诊断触发点 | 组件失效、渲染队列、块刷新、双向绑定的运行时语义（spec/03） |
| 表达式作用域、`@if`/`@foreach`/`Key` 的可见语义 | 布局、渲染、hit test（design/05） |
| 字面量到 C# 的编译期转换 | 生成代码的精确文本与作用域（spec/02） |
| 语法项实现状态与阶段 | `StyledProperty`/`RoutedEvent` 的运行时实现（design/04） |

### 0.2 权威关系

1. **语法以本文为准**。词法、句法、诊断触发点在本文定义；design/03 只保留偏差台账与历史修订。
2. **反应式语义引用 spec/03**。本文 §4.3–§4.14 与 spec/03 §2–§10 必须逐条一致；冲突时按 spec/03 修订本文。
3. **生成代码以 spec/02 为准**。design/03 §3.6 与 design/14 §14.8 的示例不再作为实现依据。
4. **控件/组件元数据以 design/14 为准**。本文只规定模板侧如何使用标签、属性、事件与内容模型；
   其中旧的"可观察参数/`Signal<T>` 参数/`EUI044`"条款由 spec/03 取代。
5. **偏差只允许出现在台账**。实现与本文的差异必须登记在 design/03 §3.5.3（v3 历史 D1–D41）、
   §3.5.4（v4 台账）与 spec/02 §12；台账之外的差异视为实现缺陷。
   不允许"文档写一套、代码静默另一套"。

### 0.3 规范用语

| 词 | 含义 |
|---|---|
| **必须** | 违反即产生诊断，不得以"当前未实现"为由静默接受 |
| **不得** | 生成器/分析器禁止生成或接受的形态 |
| **应当** | 诊断可暂缺，但评审必须记录；不得用来掩盖错误 |
| **可以** | 实现可选择的自由度，不影响语义 |

### 0.4 状态标记

| 标记 | 含义 |
|---|---|
| ✅ | 当前生成器已实现，行为与本文一致（偏差台账列出的除外） |
| S1–S5 | 反应式语义已冻结，按 spec/03 §13 分阶段实现 |
| C1–C6 | 控件/组件元数据迁移阶段，见 design/14 §14.12 |
| G3 / G4 | 语义已冻结，排在 G3 / G4 实现 |
| ⛔ | 语法位已占用，永不按未知标签/属性回退 |
| 停用 | 该诊断码永不发出；编号保留不复用 |

规范对**目标状态**生效。未实现的语法项必须按本文指定诊断拒绝，不得落进"未知标签/未知属性"的兜底。

## 1. 语言模型

### 1.1 一个文件 = 一个类 = 一个根元素

- 一个 `.ui` 文件只包含一个文档：注释、空白与**恰好一个根元素**。
- 文件名（去扩展名）必须是合法 C# 标识符，并作为生成类名；否则 `EUI013`。
- code-behind 必须是与文件同名的**顶层 `partial` 类**：
  - 缺失 → `EUI005`；嵌套 → `EUI015`；非 `partial` → `EUI011`。
- 生成部分提供 `partial class {Name} : global::EclipseUI.Component`、
  `protected override Element Build()`，以及（模板含动态内容时的）`protected override void Render()`；
  code-behind 可以省略基类，或**原样重复** `: Component`
  （其余基类，包括 `Component` 的派生类，一律 `EUI045`，见 spec/02 §10.1）；
  code-behind 不得声明 `Build()`/`Render()` 或与生成字段同名的成员（`EUI045`）。
- 根元素必须是元素标签（控件或组件标签）；根层出现文本或 `@if`/`@foreach` → `EUI006`；根元素数量不为 1 → `EUI012`。

### 1.2 code-behind 与状态

- code-behind 是唯一状态载体：字段、自动属性、方法、辅助类型。
- 模板作者写**普通类型**：`int`、`string`、`List<T>`、数组、字典、LINQ 结果、普通类/record/struct。
  不需要 `[UiState]`/`[UiObservable]`/`[UiComputed]`，不需要 `INotifyPropertyChanged`，
  集合不需要 `INotifyCollectionChanged`/`ObservableCollection<T>`（spec/03 §1.1）。
- 模板直接读写普通成员：`P`、`Tasks.Count`、`GetName()`；没有 `.Value` 重写、没有可观察路径要求。
- `Signal<T>` 是遗留运行时类型，**不是模板反应式机制**：模板必须显式写 `Count.Value`；
  写入 `.Value` 不会自动刷新，需要显式 `Invalidate()`（spec/03 §4.6）。
- 非 UI 事件引起的状态变化（后台线程、`await` 续体、定时器、直接改集合/对象）必须显式调用
  `Invalidate()`；控件事件与组件回调的包装会自动失效所属组件（spec/03 §9）。
- 模板不声明局部变量、方法、类型参数或 using；复杂逻辑放 code-behind，模板只写 C# 表达式。

### 1.3 编译期与运行期分工

| 阶段 | 做什么 | 不做什么 |
|---|---|---|
| 编译期 | 词法/句法/语义分析、目录解析、字面量转换、生成 C# | 不执行用户程序集、不执行用户转换器、不反射运行时、不做依赖分析 |
| 运行期 | 构造保留元素树、首次求值、失效队列、块刷新、重算动态表达式、属性引擎失效、布局与绘制 | 不解析 `.ui`、不做通用元素 diff、不按字符串查成员、不观察任何对象 |

模板不是"字符串拼 UI"的外壳，而是编译单元：每个动态位置在编译期已知目标元素、目标属性与首次求值顺序；
运行期按**组件渲染**重算，不按路径/信号订阅。

### 1.4 冻结线

以下决定已冻结，后续只能增量扩展，不得改义：

1. **一个 `.ui` = 一个类 = 一个根元素**；文件名即类名；code-behind 是唯一状态载体。
2. **模板作者只写普通类型**：字段/自动属性/普通类；不要求 `[UiState]`、`Signal<T>`、INPC/INCC；
   `Signal<T>` 仅作为遗留类型使用，模板必须显式写 `.Value`。
3. **反应式单位是组件渲染**：`Invalidate()` → `Render()` 重算该组件的动态表达式；
   不存在按路径/按信号的定向订阅。
4. **`P={expr}` 每次渲染重算**：控件属性直接赋值（属性引擎相等短路）；组件参数带相等保护地推送并失效子组件；
   不做依赖分析，不因方法调用/索引器/lambda 退化。
5. **文本 `{expr}` 每次渲染重算**：`Text` 模型重设整段字符串；`TextRuns` 模型只重设动态 run 的 `Text`。
6. **`@bind-P={expr}` 是双向绑定**：源必须是可写左值（字段/属性）；控件目标走 `UiBinding`，
   组件目标要求 `{P}Changed` 回调；初始方向源 → 目标；目标变化回写源并失效。
7. **结构变化只由块处理**：`@if` 切换分支时销毁/重建该分支；`@foreach` 每次刷新重新枚举集合并按 Key 复用；
   不存在通用元素 diff。
8. **Keyed 复用条件是 Key 相等且 item 身份相等**：引用类型按引用，值类型按值；同 Key 不同 item 重建条目。
9. **事件处理器 build-once**：控件事件与组件委托参数在 Build 时注册，不在每次渲染重新生成；
   处理器完成后自动 `Invalidate()` 所属组件（组件委托参数失效父组件）。
10. **非 UI 事件的变化必须显式 `Invalidate()`**：后台线程、定时器、`await` 续体、直接修改集合/对象，均不自动刷新。
11. **大小写敏感，不做别名；表达式就是 C# 表达式；不兼容 XAML**。
    v3 的 `[UiState]`/`[UiObservable]`/`UiWatch`/`IUiObservable`/依赖分析不再是模板契约。
12. **未实现的语法必须显式保留并报诊断**（`EUI031`/`EUI029`/`EUI030`/`EUI008`；插槽错误从 G4 起用 `EUI022`），
    不得落进"未知属性/未知标签"的兜底。

任何违反冻结线的语法提案，必须先在本文登记状态与诊断，再改代码；不接受"先生效、后补文档"。

## 2. 词法

### 2.1 源文件

- 扩展名 `.ui`，以 `AdditionalFiles` 进入编译；编码按 Roslyn 文本读取（UTF-8）。
- 文件名（去扩展名）必须是合法 C# 标识符；否则 `EUI013`。
- 换行支持 LF/CRLF；`#line` 使用 1 基行号。
- 一个文件只包含一个文档；文件级只有注释、空白和一个根元素（§3.2）。

### 2.2 两种扫描模式

- **文本模式**：标签之外。识别文本、`{expr}`、`@directive`、`<`、`}`、`<!-- -->`。
- **标记模式**：`<` 之后到 `>` / `/>` 之间。识别标签名、属性名、`=`、双引号字符串、`{expr}`；
  空白（含换行）被忽略。
- 模式切换只由 `<`、`>`、`/>` 驱动；表达式与指令内部的 `<`/`>` 不切换模式
  （lambda 的 `=>`、泛型参数不会截断标签）。

### 2.3 名字

规范只承认 ASCII 标识符：

```ebnf
ident      ::= [A-Za-z_][A-Za-z0-9_]*
tag-name   ::= ident
attr-base  ::= ident ( "." ident )?
attr-name  ::= attr-base ( ":" ident )?
bind-attr  ::= "@bind-" attr-base
```

- 标签名是单段短名：`Button`、`TextBlock`。**不带命名空间、不带泛型实参**；
  泛型标签是 G4（`EUI030`）。
- 属性名支持 `Owner.Name` 形状仅为附加属性预留（G4，如 `Grid.Row`）；
  附加属性实现前报保留诊断 `EUI031`，不退化为 `EUI002`。
- 末尾 `:ident` 是旧修饰符：`bind` / `value` 均已弃用（S3 起 `EUI043`）；
  其它修饰符按未知属性处理 `EUI002`。
- `@bind-P` 是双向绑定的**独立属性形态**：只出现在标记模式的属性名位置，
  `@bind-` 是字面前缀，`P` 是 `attr-base`；它不是指令，不参与 `@if`/`@foreach` 的 `@name` 解析。
  - 大小写敏感：`@bind-Checked` 合法，`@bind-checked` 解析到未知属性（`EUI002`/`EUI040`）。
  - `@bind-P:bind` 或 `@bind- P`（带空白）是语法错误 `EUI100`。
  - 不得绑定保留属性 `Key`/`Ref`/`Class`/`Style`、事件 `On*` 或 `[UiContent]`/`[UiSlot]` 参数 → `EUI040`。
- 名字**大小写敏感**；不做命名空间优先级；标签冲突由目录报 `EUI016`。
- `Key` / `Ref` / `Class` / `Style` 是保留属性名，控件不得声明同名属性（design/14 §14.4.2）。

实现说明：当前 `Lexer` 接受比规范更宽的名字字符集（含 Unicode 字母与 `.`/`:`/`-` 开头），
这只是扫描器的宽松性，不是语言 API；最终由分析器按目录报 `EUI001`/`EUI002`。
规范不保证这些名字的行为。

### 2.4 记号

| 记号 | 含义 |
|---|---|
| `<` / `</` / `>` / `/>` | 标签开/闭/自闭合 |
| `=` | 属性赋值；两侧可有空白 |
| `"..."` | 属性字面量（仅双引号） |
| `{...}` | C# 表达式（§2.6） |
| `@bind-属性名` | 双向绑定属性（仅标记模式属性位置，§2.3） |
| `@name` / `@name(...)` | 指令（§2.7） |
| `<!-- ... -->` | 注释（§2.9） |

- 标签/属性之间允许任意空白与换行；`<StackPanel>`、`<StackPanel >`、
  `<StackPanel\n  Gap="8"\n>` 等价。
- 属性必须有 `=` 和值；`<Button disabled>` 是语法错误 `EUI100`，不是布尔属性简写。

### 2.5 属性字面量

- 仅双引号字符串；单引号不是属性定界符。
- 字面量是**原始文本**：不解析 XML 实体（`&amp;` 原样是 `&amp;`），不做空白折叠，可跨行。
- 词法上 `\` 使其后的一个字符不参与"结束引号"判定；**但值保留原始字符，不做反转义**：
  `\"` 在值中是 `\` 和 `"` 两个字符。需要精确的引号/反斜杠时，用 C# 表达式：`P={"a\"b"}`。
- 值是否为空串合法由目标属性决定；`Label=""` 合法。
- **字面量还是表达式由 token 决定，不由内容决定**：带引号的 `P="{x}"` 是字符串字面量 `{x}`，不是表达式。

### 2.6 表达式

`{ csharp-expression }`：

- 从 `{` 开始按平衡大括号捕获；`"` 和 `'` 字符串内部的大括号不计数；`\` 转义字符串内的下一个字符。
- 可以包含嵌套 `{}`、lambda 块体、`=>`、泛型 `<...>`；`>` 不会截断表达式。
- 表达式必须是 **C# 表达式**，不是语句/声明。需要复杂逻辑时调用 code-behind 方法或在 code-behind 里准备好值。
- 当前词法限制（由当前扫描器决定，C5 换 Roslyn tokenizer 后消除）：
  - 不支持 verbatim 字符串 `@"..."` 与 raw string `"""..."""`；
  - 不支持 C# 注释里出现不平衡的 `{`/`}`；
  - 不支持插值格式说明符：`{price:F2}` 会被当作表达式 `price:F2`，不是合法 C#；
    请写 `{price.ToString("F2")}`。
- 空表达式 `{}`、不完整表达式不在词法层报错，交给生成代码的 C# 编译错误（经 `#line` 指回 `.ui`）。
- 文本/指令里的表达式不做 trim；C# 自行忽略外围空白。
- 表达式**原样输出**：生成器不做 `Signal<T>` 裸名重写、不做语义模型改写；
  需要 `.Value` 时由作者显式写（spec/03 §4.6）。

### 2.7 指令

- `@name` 后可选 `(...)`；括号按平衡圆括号捕获，字符串感知；指令名只含字母/数字/`_`。
- 已实现：`@if (...)`、`@foreach (...)`。
- `@switch`：保留，`EUI008`；不解析其块体。
- `@bind-属性名` 只在标记模式属性位置识别，不是指令；文本模式写 `@bind` 仍是未知指令 `EUI009`。
- 其它 `@name`：`EUI009`。
- `@` 没有文本转义；要输出字面 `@`，写 `{"@"}`。
- 指令只允许出现在元素内容或块内容中；不能出现在文档根。

### 2.8 文本与特殊字符

文本模式下的特殊字符：

| 字符 | 行为 | 写字的办法 |
|---|---|---|
| `<` | 开始标签 | `{"<"}` 或整段用表达式 |
| `{` | 开始表达式 | `{"{"}` |
| `}` | 结束块；不在块内则 `EUI100` | `{"}"}` |
| `@` | 开始指令 | `{"@"}` |
| `>`、`&`、`=`、引号 | 普通文本 | 直接写 |

- 没有 CDATA、没有 XML 实体、没有 HTML 标签白名单；任何 `<` 都按标签解析。
- 文本节点可跨行；原始换行/缩进按 §2.10 规范化。
- 文本里不能直接写 `<` 做比较；表达式 `{a < b ? "x" : "y"}` 必须在 `{...}` 内。

### 2.9 注释

- `<!-- ... -->` 只在文本模式识别；不能写在标签内部。
- 注释不嵌套；内容丢弃，不进入 AST，不是 trivia；相邻文本会拼成同一文本节点
  （`a<!--c-->b` → `ab`）。
- 未闭合注释报 `EUI100`（当前实现静默消费到文件尾，见 design/03 D12）。
- 注释不参与 pretty-printer（当前没有 pretty-printer）。

### 2.10 空白与规范化

- 标记模式内空白忽略；属性之间、`=` 两侧、`/>` 前均可换行。
- 文本内容在**每一层**（文档、元素、`@if`、`@foreach`）规范化：
  1. 若该层的第一个节点是文本节点，且其第一个段是静态段，则 `TrimStart`；
  2. 若该层的最后一个节点是文本节点，且其最后一个段是静态段，则 `TrimEnd`；
  3. 纯空白文本节点整体丢弃（含表达式或非空白字符的文本节点不丢）。
- 中间空白（包括内部换行与缩进）保留；多行动态文本的缩进会进入字符串，
  需要精确排版时用表达式或 code-behind 方法。
- 规范化的结果是：模板的缩进不会渲染成前导/尾随空格；这与 XAML/JSX 直觉一致。
- 注释移除发生在规范化之前；`a<!--c-->b` 的两个文本段按中间内容处理，不会被首尾 trim 影响。

## 3. 句法

### 3.1 EBNF

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
- `node*` 的合法组合由内容模型进一步限制（§4.9），句法层先接受再语义拒绝。
- 没有 `else`、`else if`、`@switch`、`@code`、`@inherits`、`@using`、`@model`；未知 `@` 指令是 `EUI009`。
- 泛型标签（`DataGrid<Order>`）、附加属性（`Grid.Row`）、插槽标签（`<Slot>`）不在当前句法内；
  分别按 G4 / G4 / G4 保留（§4.15）。

### 3.2 结构规则

| 规则 | 诊断 | 状态 |
|---|---|---|
| 文档恰好一个根元素 | `EUI012` | ✅ |
| 根层不允许文本或控制流块 | `EUI006` | ✅ |
| 开始/结束标签同名且大小写一致 | `EUI100` | ✅ |
| 属性必须有 `=` 和值 | `EUI100` | ✅ |
| 同一元素重复属性 | `EUI025` | ✅ C3（纯普值同名重复；任一侧是绑定的同名重叠仍 `EUI033`，见 §3.3） |
| `@if`/`@foreach` 只能出现在 `Children` 内容模型内 | `EUI006` | ✅ |
| 块内容只允许元素与嵌套块，不允许裸文本 | `EUI006` | ✅ |
| `Key` 只能出现在 `@foreach` item 模板的第一个元素根上 | `EUI010` | ✅ |
| `@foreach` 必须写 `var 名字 in ...` | `EUI100` | ✅ |
| `@bind-P` 的值不是 `{expr}` | `EUI037` | ✅ S3 |
| `@bind-P` 目标非法（不可写/无属性字段/事件/保留/内容/组件缺 `PChanged`） | `EUI040` | ✅ S3+C4（控件 ✅ S3；组件 ✅ C4） |
| `@bind-P` 源非法（非可写左值/类型不符） | `EUI037` | ✅ S3 |
| `@bind-P` 与同名属性/`:bind`/`:value` 重复 | `EUI033` | ✅ S2（完全重复属性仍 `EUI025`，C3） |
| `Key={}` 空表达式 | `EUI010` | ✅ S2 |
| 注释不写在标签内部 | `EUI100` | ✅ |

### 3.3 内容模型的句法后果

块（`@if`/`@foreach`）的内容**永远按"元素容器"处理**，即使外层是 `Text`/`TextRuns` 模型；因此：

- `@if`/`@foreach` 里不能直接写裸文本或 `{expr}` 文本，必须先包一个 `TextBlock`；
- 在 `Text`/`TextRuns` 模型里写块直接 `EUI006`；
- "条件文本"不能用 `<TextBlock>@if ...</TextBlock>`：把 `@if` 放在容器层控制整个 `TextBlock`，
  或在 `TextBlock` 内写文本表达式（复杂逻辑放 code-behind 方法）。

## 4. 语义

### 4.1 模板类与作用域

- 生成代码的类壳、命名与顺序见 [spec/02](./02-generated-code-contract.md) §3–§5。
- 表达式作用域就是 C# 可见性；没有名字重写，也没有依赖分析：

| 位置 | 可见名字 | 求值时机 |
|---|---|---|
| 根元素/根属性/根文本 | code-behind 实例成员（`this` 隐式）、同命名空间类型、全限定类型 | Build 首次；每次组件 `Render` 重算 |
| `@if` 内容 | 同上；不能声明局部变量 | 分支构造时首次；分支存活期间每次所属作用域刷新重跑 |
| `@if` 条件 | 同上 | Build 首次；每次块刷新重算 |
| `@foreach` 集合 | 同上 | 每次块刷新重新求值并枚举 |
| `@foreach` item | item 变量 + 外层 item 变量（嵌套）+ code-behind 实例成员 | 条目构造时首次；复用条目每次刷新重跑 |
| `Key={expr}` | item 变量 + code-behind 实例成员 | 每次枚举每个 item 求值 |
| 控件事件/组件委托参数 | 同上 | Build 一次（build-once） |

- 模板不声明局部变量、方法、类型参数；复杂逻辑放 code-behind 方法/属性，模板只写表达式。
- 没有"可观察路径"概念：`Vm.Count`、`GetName()`、`Tasks.Where(...)` 都是普通 C# 表达式。
- item 变量遮蔽遵循 C#：item 变量遮蔽同名 code-behind 成员；规范建议不要制造这种遮蔽。
- 事件处理器里的读取发生在触发时（§4.13）；事件体不参与任何渲染重算。

### 4.2 标签与属性解析

解析顺序按目录项的 `Origin` 分支（design/14 §14.5.3）：

| Origin | 普通属性 | `@bind-P` |
|---|---|---|
| 控件（`Element` 派生） | 保留属性（`Key`/`Ref`/`Class`/`Style`）→ 事件（`On{Event}`）→ 属性表 → 附加属性（G4）→ 未知 | 目标必须是属性表内的 public 可写属性 + `{Name}Property`；事件/保留/只读 → `EUI040` |
| 组件（`Component` 派生） | 保留属性 → 参数表（委托参数就是事件，不做 `On*` 推断）→ 未知 | 目标必须是 `[UiParameter]` 且 public setter 的参数，并且组件声明了 `{P}Changed`（`Action<T>`）参数；否则 `EUI040` |

- 标签必须解析到目录中的已知控件/组件；未知 → `EUI001`；冲突 → `EUI016`；
  不可用 → `EUI017`/`EUI018`/`EUI030`。
- 属性名大小写敏感；未知 → `EUI002`，并给最近名建议（Levenshtein ≤ 3，最多 3 个候选）。
  若名字命中一个**存在但没有 `{Name}Property` 的公开 CLR 属性**，提示改为
  "该属性未注册 StyledProperty，不参与模板"，而不是只报拼写错误。
- 事件属性只对控件按 `On{EventName}` 解析；组件的 `OnClose` 若存在同名委托参数，按参数处理，
  不按事件字段处理。
- `@bind-P` 的 `P` 按上表解析；解析失败先报 `EUI002`（未知属性），解析成功但不可绑报 `EUI040`。
- `P:bind` / `P:value` 先按 `P` 解析目标，再按 §4.6 映射到新语义并报 `EUI043` Warning。
- 事件/属性同名的元数据冲突由 design/14 §14.4.2 的 `EUI026` 拦截。
- 保留属性优先于控件同名属性；控件不得声明 `Key`/`Ref`/`Class`/`Style` 同名属性。
- 控件的内容模型目标若是普通可写属性（如 `Button.Label`），可作为 `@bind-P` 目标；
  但同一元素不得再有文本内容（`EUI033`）。组件的 `[UiContent]`/`[UiSlot]` 参数是 build-once，
  不可作为 `@bind-P` 目标（`EUI040`）。
- 参数是否为可观察类型/partial **不参与**解析：所有合法参数都可读写（spec/03 §10.1）。

### 4.3 赋值形态总表

| 写法 | 求值 | 反应式 | 运行时机制 | 约束/诊断 | 状态 |
|---|---|---|---|---|---|
| `P="literal"` | Build 一次 | 否 | 编译期转换 + 属性赋值 | `EUI003`/`EUI023` | ✅（§5.2 全表，C3） |
| `P={expr}` | Build + 每次渲染 | 是（组件级） | 控件属性直接写（相等短路）；组件参数相等保护 + 子 `Invalidate()` | 目标可写；不做依赖分析 | S1/S4（当前一次性，D23）；组件推送形态 ✅ C4 |
| `@bind-P={expr}` | Build + 双向 | 是 | 控件 `UiBinding`；组件 `PChanged` + 参数推送 | 源可写左值；目标可写；组件需 `PChanged` | ✅ S3（控件）/ S4+C4（组件） |
| `P:bind={expr}` | 同 `@bind-P` | 是 | 同 | 旧语法，`EUI043` Warning | ✅ S3 → 弃用 |
| `P:value={expr}` | 同 `P={expr}` | 是 | 同 | 旧语法，`EUI043` Warning | ✅ S3 → 弃用 |
| `OnE={expr}` | Build 时 `AddHandler` | 否 | 处理器后自动 `Invalidate()` | `Action` / `Action<RoutedEventArgs>` | ✅（控件）/ C4 组件 |
| `Key={expr}` | 每次枚举 | 身份 | Keyed 复用 | 第一个元素根；条目身份 | ✅（基础）/ S2（v4 复用） |
| `Ref={ident}` | Build 时赋给字段 | 否 | 编译期字段赋值 | 简单标识符、字段可写（生成器探针校验） | ✅ C5（非标识符/语义非法 `EUI029`） |
| `Class`/`Style` | — | — | — | — | G4 / 保留 `EUI031` |

### 4.4 单向赋值 `P={expr}`

- `P={expr}` 是**普通赋值**，依赖组件渲染：Build 首次求值；之后每次所属组件 `Render()` 重算并重新赋值。
- 没有依赖分析：表达式可以包含方法调用、索引器、lambda、LINQ、`await` 之外的一切 C# 表达式；
  不会因为"不可观察"降级或报警。表达式必须无副作用（spec/03 §4.5）。
- 目标：
  - **控件属性**：必须可写并存在 `{Name}Property`；写入走公开 CLR setter
    （`Element.SetValue` 内部相等短路/失效，R3/R7）；不可写/缺失 → `EUI019`（已进入目录的属性）
    或 `EUI002`（未暴露）；`@bind` 目标非法 → `EUI040`。
  - **组件参数**：必须 `[UiParameter]` + public setter。每次渲染求值后：
    - 先转型到参数声明类型；
    - 用 `EqualityComparer<T>.Default` 比较当前参数值；
    - 不同才写公开 setter 并调用子组件 `Invalidate()`；相同则不赋值、不失效。
  - **静态/动态统一**：`P="literal"` 也走公开 setter，但只在 Build 一次。
- 首次推送发生在子组件 `Mount()` 之前；此时 `Invalidate()` 是 no-op，参数已就位（spec/03 §5.2）。
- 组件参数没有 diff、不重建组件；子组件 `Render` 重算自己的动态表达式。
- 参数为委托类型时按"组件事件"处理（§4.13），build-once，不参与渲染重算。
- `P={expr}` 与文本内容同时指向同一 `Text` 目标 → `EUI033`（同一目标重复赋值），
  不生成两套写入；`P=` 与 `@bind-P` 同时用于同一目标 → `EUI033`。
- 字面量转换在编译期完成（R2）；第三方转换器因 R1 不支持（§5.2）。

### 4.5 双向绑定 `@bind-P={expr}`

- 语法见 §2.3/§3.1；值必须是 `{expression}`，字面量 → `EUI037`。
- 目标 `P`：
  - 控件：public 可写属性 + `{Name}Property`；不得是事件/保留属性；
  - 组件：`[UiParameter]` + public setter + 声明 `{P}Changed`（`Action<T>`）；
  - `[UiContent]`/`[UiSlot]` 参数、委托参数、保留属性、其它 → `EUI040`。
- 源 `expr` 必须是**可写左值**（spec/03 §8.1）：
  1. 字段、可写属性（含 `private`/`internal` setter，生成代码在同一 partial 类内）；
  2. 允许：`this.Count`、`Vm.Name`、`t.Done`（`t` 为引用类型 item）、`_state.Enabled`（`_state` 是字段）；
  3. 不允许（`EUI037`）：
     - 方法调用、索引器、`?.`、`??`、强制转换、`new`、lambda；
     - 静态成员、常量、只读字段、`init` setter；
     - 类型与目标属性不一致（忽略可空注解）；
     - 中间段是**属性返回的 struct**（`Vm.State.Enabled = v` 是 CS1612）；中间段是 struct 时必须是字段；
     - `@foreach` 的**值类型 item** 的成员（迭代变量只读）。
  4. 静态无法判定的左值问题由 C# 经 `#line` 报错；能静态判定的必须报 `EUI037`。
- 初始方向：**源值胜出**（Build 时目标被源初始化）；随后每次所属作用域刷新都源 → 目标。
- 控件目标生成 `UiBinding.TwoWay`（spec/03 §8.2、spec/02 §4.4）：
  - 构造时写目标、订阅 `element.PropertyChanged`；
  - `Refresh()` 源 → 目标，相等短路；
  - 目标变化且非同步写入时回写源并 `Invalidate()` 所属组件；
  - `_syncing` 防回环；`Dispose` 解除订阅。
- 组件目标不使用 `UiBinding`，而是"父推送 + 子回调"（spec/03 §8.3）：
  - 父在 Build 赋 `cmp.PChanged = v => { if (!Equals(source, v)) { source = v; Invalidate(); } };`
  - 父调用推送方法把源写给参数（相等保护 + 子 `Invalidate()`）；每次父渲染再推送；
  - 子组件在内部动作中修改参数后调用 `PChanged?.Invoke(newValue)` 并 `Invalidate()` 自己；
    不得在 `Render()` 中无条件调用回调。
- 两侧写入都必须通过公开/可见 setter/属性引擎；**不得**用 `SetValue/GetValue` 绕过属性引擎（R3/R7）。
- 目标属性与源解析到同一成员/同一对象时视为自绑，`EUI037`；
  静态无法判定时不报，运行时 `_syncing`/相等保护防回环。
- item 模板内：源可以 rooted at item，也可以 rooted at 组件状态；条目重建时旧绑定 Dispose、新绑定重建。
- **组件内容体内不得出现 `@bind`**（`EUI040`）：内容 build-once，没有渲染动作作用域；
  需要内容随状态变化时，把组件标签放进父模板的 `@if`/`@foreach`，或让内容自身是独立组件。

### 4.6 旧语法 `:bind` / `:value`（弃用）

- `P:bind={expr}` ≡ `@bind-P={expr}`；`P:value={expr}` ≡ `P={expr}`。
- S3 起两者都报 `EUI043` Warning；至少保留一个发布周期，S5 评估改为 Error/移除。
- v4 不再把 `Signal<T>` 对象当作特殊绑定源：`:bind={sig}` 现在要求 `sig` 是可写左值，
  其类型必须与目标一致；要写信号值请写 `:bind={sig.Value}` 或改用普通属性。
- 迁移：`:bind` → `@bind-P`；`:value` → `P=`；`Signal<T>` 字段 → 普通字段/属性（§附录 B.3）。

### 4.7 `Signal<T>` 与值上下文

- 表达式**不做任何重写**：模板写 `Count` 就读 `Count`，写 `Count.Value` 就读 `.Value`。
- `Signal<T>` 仍是运行时类型，但不是模板反应式机制：
  - 读取 `Count.Value` 不建立订阅；写入 `Count.Value = x` 不自动刷新；
  - 需要刷新时在写入后调用 `Invalidate()`；
  - 新代码建议用普通字段/属性，不用 `Signal<T>`。
- item 成员同样不重写：item 的普通属性直接读，item 的 `Signal<T>` 成员必须显式 `.Value`。
- 没有字符串/注释/同名局部变量的重写问题：生成器原样输出表达式（文本插值除外，§4.9）。

### 4.8 生命周期与资源

| 位置 | 生成/归属 | 何时释放 |
|---|---|---|
| 根级 `UiBinding` | `__bind{N}` 字段 + `Track(...)` | 组件 `Dispose()` |
| 根级 `@if`/`@foreach` 块 | `__blk{N}` 字段 + `Track(...)` | 组件 `Dispose()` |
| 块内容/条目内的绑定、子块、子组件 | `BlockScope.Disposables` | 分支切换/条目重建/条目移除/块 `Dispose()` |
| 组件实例（根级） | `Track(cmp)`（= `Adopt` + 登记释放） | 组件 `Dispose()` |
| 组件实例（块/条目内） | `Adopt(cmp)` + `Disposables.Add(cmp)` | 条目/分支释放 |
| 事件处理器 | 不产生资源 | 元素生命周期（build-once） |

- 模板不写 `Subscribe`/`Dispose`；作用域由生成代码负责（R8）。
- `Build()` 构造元素、赋值、注册事件、构造绑定与块；`Render()` 只调用 `U{N}()`/`Refresh()`，
  **不得**构造新元素、注册事件、安装绑定、调用 `Mount()`。
- `@if` 分支重建会销毁旧内容（元素重新构造），控件的构造/setter 必须可重复执行且无副作用
  （design/14 §14.4.5）。
- `@foreach` 条目复用/重建规则见 §4.12：同 Key 同 item 复用；同 Key 不同 item 重建并释放旧 scope。
- `ForEachBlock.Dispose` 必须释放仍存在条目的 `BlockScope.Disposables`（修复 D18/D27）；
  `ConditionalBlock.Dispose` 释放当前分支作用域；整块卸载不得泄漏条目资源。
- 生成代码不得把资源存静态字段/单例/`ConditionalWeakTable`（R6/R8）；
  每个绑定/块/组件可追踪到唯一所有者。
- 没有订阅表：v4 的资源是绑定、块、组件与事件处理器，不存在 `UiWatch`/`UiSubscription`。

### 4.9 文本与内容模型

内容模型由控件/组件声明（design/14 §14.4.4）：

| 模型 | 允许的子内容 | 生成目标 | 违规 |
|---|---|---|---|
| `None`（默认） | 无 | — | 任意子内容 `EUI006` |
| `Children` | 元素、嵌套块；纯空白文本忽略 | 目标 `.Add(Element)` | 非空白文本 / 非法结构 `EUI006` |
| `Text` | 文本段与 `{expr}` | 目标 `string` 属性（如 `Button.Label`） | 元素/块 `EUI006` |
| `TextRuns` | 文本段与 `{expr}` | `ITextContentHost.AddRun(string)`（`TextBlock`） | 元素/块 `EUI006` |

文本语义：

- 静态文本在 Build 时赋值：`Text` 模型合并所有静态段为一条字符串；
  `TextRuns` 模型把**连续静态段**合并为一个 `TextRun`（空段不生成），与 spec/02 §4.8.1 一致。
- 含 `{expr}` 的文本是**动态文本**，随所属作用域渲染重算：
  - `Text`：一个文本节点生成**一个完整插值串**，每次渲染重设整个目标值；
  - `TextRuns`：每个动态段 `AddRun("")` 创建一个空 `TextRun`，每次渲染只重设该 run 的 `Text`；
    静态段不受影响，不重新 `AddRun`。
- 集合内容变化不会自动触发：`{Tasks.Count}`、`@foreach` 都在组件 `Invalidate()` 后的渲染/刷新中
  重新求值；UI 事件内自动失效，非 UI 事件需显式调用（spec/03 §9）。
- 文本插值使用 C# `$"..."`：当前区域文化、`ToString()` 语义；不支持格式说明符
  （写 `.ToString("F2")`，§2.6）。
- 文本里不能嵌套元素或块；`TextRuns` 也只接受文本段，需要多段不同样式等富文本能力时另设控件（G4）。
- **同一目标重复赋值是错误**：`Text` 模型下显式 `Label="..."` 和文本内容同时存在，
  或 `P={expr}` 与同一目标绑定同时存在，报 `EUI033`（文本 + 显式目标已实现，S2）。
- `Children` 模型中的纯空白文本在规范化后消失，不报 `EUI006`；非空白裸文本报错，必须包元素。
- 组件的默认内容参数（C4）按组件声明的 `[UiContent]` 模型处理；具名插槽 G4。

### 4.10 `@if`

- 形式：`@if (condition) { ... }`；条件必须是 C# `bool` 表达式；没有 `else`/`else if`
  （用两个 `@if` 或取反）。非 `bool` 由 C# 通过 `#line` 报类型错误，没有专门的模板诊断。
- 首次求值在 Build 时；之后每次所属作用域渲染通过 `ConditionalBlock.Refresh()` 重新求值。
  条件可包含方法调用/索引器/LINQ；没有"条件依赖"分析，也不需要。
- 条件在两次渲染间不变：分支内容保留，重跑分支的渲染动作（`BlockScope.RenderActions`）。
- 条件翻转：销毁旧分支（释放其作用域），构造新分支；新分支构造时完成首次求值。
- 空分支合法：不生成 `SetContent`，块保持空内容。
- 生成：`new ConditionalBlock()` → 挂父级 → `SetCondition(Func<bool>)` →
  `SetContent(Action<BlockScope>)` → 作用域登记 → 显式 `Refresh()`（spec/02 §4.9、spec/03 §6.2）。
  模板用 `AddRoot`/`AddRenderAction`/`AddDisposable` 登记，块创建并传入 `BlockScope`。
- 分支内容只允许元素与嵌套块；可以有多个根元素；分支切换丢失子树局部状态（焦点、滚动）。
- 在 item 模板内：条件可引用 item 与组件状态；条目复用/重建规则同 `@foreach`（§4.12）。
- 当前布局：`ConditionalBlock` 继承 `StackPanelBase`，仍参与布局（作为一层容器）；
  G3 改为布局透明（不引入额外间距/尺寸约束，design/03 D16）。

### 4.11 `@foreach`

- 形式：`@foreach (var item in collection) { ... }`；**`var` 必写**，
  不支持 `T item in ...`、解构、`await foreach`。
  三种违规（类型化条目 / 解构 / 缺 `in` 或缺集合）给出针对性 `EUI100` 消息。
  头解析按**深度/字符串感知扫描**（tokenizer，2026-11-05）：集合内带引号的 `" in "`、
  括号内的 `in`（方法实参修饰符）不再被认为分隔符（旧正则实现的两处已知误切分）。
- `collection` 必须是 `IEnumerable<T>`（含 `T[]`、`List<T>`、`Dictionary<K,V>`、`HashSet<T>`、
  LINQ 结果、自定义实现、`ObservableCollection<T>`）；`T` 必须可推断。
- 值为 `null` 视为空集合，不构造条目，不报错；`null` 字面量因无法推断 `T` 报 `EUI007`。
- `IAsyncEnumerable<T>`、`IEnumerator<T>`、非泛型 `IEnumerable`、`dynamic` → `EUI007`。
- 集合表达式每次块刷新**重新求值并重新枚举**；不要求 `INotifyCollectionChanged`。
  `ObservableCollection<T>` 的变更事件不会自动触发渲染，仍需 `Invalidate()`。
- item 类型由 `IEnumerable<TItem>` 推断，生成 `ForEachBlock<TItem>`；item 变量只在块内可见。
- item 模板：
  - 可引用 item 成员、外层 item 变量（嵌套）、code-behind 实例成员；
    **不再禁止引用组件级状态**（v3 的 `EUI014` 移除）；
  - `Key` 可引用任意可读表达式（包括组件状态）；没有 v3 的 `EUI039`；Key 稳定性由作者负责；
  - `@bind-P` 源可以 rooted at item，也可以 rooted at 组件状态；没有 v3 的 `EUI042`；
  - `@bind-P={item.X}` 合法（item 可写属性双向）；
  - 嵌套 `@foreach`（S2）：内层集合可以是 item 成员、组件状态或任意 `IEnumerable<T>`；
    内层块登记在条目作用域，条目移除/重建时释放。
- item 模板可有多个根元素；`Key` 只允许出现在第一个元素根上，标识整个条目。
- 空集合/单元素不特殊；`Key` 缺失时用 item 自身作为 key。
- 不建议在 `@foreach` 表达式里做重 LINQ；在 code-behind 缓存投影结果并显式失效（spec/03 §12.2）。

### 4.12 `Key` 与条目复用

- `Key={expr}` 只用于 `@foreach` 条目身份；非 item 模板根报 `EUI010`；字面量/空表达式报 `EUI010`。
- Key 可引用任意可读表达式（item 成员、组件状态、方法调用）；**没有** v3 的可观察性限制（`EUI039` 停用）。
  Key 变化会导致条目重建，稳定性由作者负责。
- 无 `Key` 时默认 key = item 自身（`ForEachBlock` 生成 `item => (object?)item`）。
- `Key` 表达式返回 `null` 时归一到一个共享空键；应避免 null，否则多个条目会互相复用。
- 重复 key 不报错；同 key 的条目按出现顺序复用（`Queue<Entry>`）；新出现的重复 key 新建条目。
- key 比较发生在对象装箱后；字典身份按 `Equals`/`GetHashCode`。
- **复用条件 = key 相等且 item 身份相等**（spec/03 §7.3）：
  - 值类型：`EqualityComparer<T>.Default.Equals(oldItem, newItem)`；
  - 引用类型：`ReferenceEquals(oldItem, newItem)`。
- key 相同 + item 相同：复用结构，重跑 `BlockScope.RenderActions`；条目内绑定保持。
- key 相同 + item 不同：释放旧 `BlockScope.Disposables`，**重建条目**；禁止闭包捕获旧 item。
- key 不同/新增：新建条目；旧条目未复用则释放。
- 条目复用时什么会重跑：

| 模板内容 | key 同 + item 同 | key 同 + item 不同 | key 不同/新增 |
|---|---|---|---|
| 元素结构 | 不重建 | **重建** | 重建 |
| 根层文本/`P={item.X}`（渲染动作） | 重跑渲染动作 | 重建时求值 | 新建时求值 |
| 根层 `@bind`（`UiBinding`/`PChanged`） | 保持原绑定，`Refresh()` 源 → 目标 | 重建并绑定新 item | 新建 |
| 事件处理器 | 捕获当前 item（同引用） | 重建并绑定新 item | 重建 |
| `@if (item.X)` | 每次刷新重算 | 重建 | 重建 |
| 嵌套 `@foreach` | 每次刷新重新枚举 | 重建 | 新建 |
| 组件参数推送 | 每次刷新推送 | 重建组件 | 新建 |

- 条目重建会丢失元素局部状态（焦点、滚动、动画）；Key 的职责是**重排身份**，不是"数据版本"。
  需要保留局部状态时，让 item 对象稳定、修改其属性后 `Invalidate()`，而不是替换 item 对象。
- item 为值类型时，同 Key 且值相等才复用；值变化会重建条目并丢失局部状态。
- `IEnumerable<object>` 中的值类型 item 每次枚举都会产生新装箱，按引用比较会每次重建；建议用具体 `T`。

### 4.13 事件

- **控件事件**：属性名 `On{EventName}`，对应 `{Name}Event` 的
  `public static readonly RoutedEvent` 字段（design/14 §14.4.3）。
  - 只接受 `{expression}`；字面量或 `:bind`/`@bind-` → `EUI004`/`EUI040`。
  - 处理器必须能转换为 `Action` 或 `Action<RoutedEventArgs>`；签名不匹配由 C# 报错
    （`EUI024` 尚未实现；两个委托类型的选择已按表达式**语法形态**判定——tokenizer 切片
    （2026-11-05）用 Roslyn 解析替代正则，签名匹配另片）。
  - **build-once**：Build 时 `el.AddHandler({Event}, 包装);`，不在渲染时重新注册。
  - 处理器表达式先转为委托局部 `h{N}`（类型为匹配到的 `Action`/`Action<RoutedEventArgs>`），
    包装在用户处理器**同步返回后**调用所属组件 `Invalidate()`：
    `var h0 = (Action)(() => AddTask()); el.AddHandler(Button.ClickEvent, () => { h0(); Invalidate(); });`。
  - 处理器抛异常时不执行自动 `Invalidate()`（异常向上传播）；组件保持当前 UI，直到下一次失效。
  - 事件字段必须声明在元素/组件类型上；静态宿主类不参与扫描（design/14 D11）。
    C1/C2 后控件事件表面共 9 个事件名：`OnClick`、`OnCheckedChanged` + 7 个输入/焦点事件
    （`OnPointerPressed/Released/Moved`、`OnKeyDown/Up`、`OnGotFocus/LostFocus`，C2 起声明在 `Element` 上）。
- **组件事件**（C4）：组件没有路由事件；委托类型参数即事件，属性名就是参数名
  （如 `OnClose`），不做 `On*` 前缀推断；同样只接受 `{expression}`。
  - build-once；处理器表达式先写入委托局部 `h{N}`，包装调用它后再 `Invalidate()` **父组件**（提供委托的一方）。
  - 委托必须返回 `void`，参数不得含 `ref`/`out`/`in`；否则 `EUI028`。
- 事件处理器里的读取在事件触发时发生；不建立订阅，不参与渲染重算。
- 事件处理器表达式**不参与任何分析**（没有依赖分析）；语法/类型错误由 C# 报。
- 事件处理器捕获 item：同 Key 同 item 复用安全；item 引用变化时条目重建。
- **异步处理器**：`async void` 在第一个 `await` 前返回，包装只失效一次；`await` 之后的状态变化
  必须显式 `Invalidate()`（spec/03 §9.3）。
- 事件名区分大小写；`Onclick` 是未知属性。

### 4.14 组件

组件是 `.ui` + code-behind 生成的 `Component` 子类；标签规则与控件相同（design/14 §14.4.1），
但属性来自参数而非 StyledProperty。

- 参数必须标 `[UiParameter]`，`public`、有 `public` setter（`init` 不允许）；
  必填写 `[UiParameter(Required = true)]`。缺失必填 → `EUI027`；
  `required`/`init`/无 setter → `EUI028`。
- 参数类型任意，但必须是合法泛型实参：不得是 `ref struct`/指针/`Span<T>`
  （生成代码用 `EqualityComparer<T>` 做相等保护）；否则 `EUI028`。
- 参数是**普通属性**：不要求 `[UiState]`、不要求 partial、不要求可观察；
  父渲染推送 + 子组件 `Invalidate()`，子组件重算自己的动态表达式（§4.4）。
- 双向参数：组件为目标参数 `P` 声明 `[UiParameter] public Action<T>? PChanged { get; set; }`；
  `@bind-P` 依赖该回调（§4.5）；缺失/类型不符 → `EUI040`。
- 构造顺序（冻结；精确文本见 spec/02 §4.12）：

```text
new cmp0
→ 作用域登记（Track / Adopt + Disposables.Add / receiver.TrackContent）
→ 参数（源码顺序：静态赋值、动态推送 U{N}()、事件/@bind 回调混合）
→ 默认内容（若有）
→ cmp0.Mount()
→ parent.Children.Add(cmp0.Root)
```

- 作用域登记在参数/内容之前：参数 setter、内容构建或 `Mount()` 失败时，父作用域仍可释放组件。
- 参数/回调必须在 `Mount()` 之前完成：子组件首次 `Build()` 读取参数。
- `Mount()` 幂等；`Dispose()` 幂等；组件实例由调用方作用域释放，组件内资源随实例释放。
- 参数没有 diff：Build 只跑一次；参数推送只改属性并失效子组件，不重建组件模板。
- 子组件内部修改参数不自动通知父；需要向上同步时使用 `PChanged`（§4.5）。
  父未使用 `@bind` 时，子对参数的修改只在子内部生效；下次父渲染会推送父源值覆盖。
- 默认内容（C4 已落地，2026-11-05）按 `[UiContent]` 参数处理；具名插槽 G4（`<Slot>`/`<ContentSlot>` 保留 `EUI031`，
  G4 后插槽错误 `EUI022`）。内容/插槽 build-once，不可观察，不随父渲染重算。
- 组件内容（`[UiContent]`）的额外规则（spec/02 §4.12.3）：
  - 内容参数类型只接受 `IReadOnlyList<Element>`（多根）或 `Element?`（单根）；其他类型 → `EUI021`；
  - 内容中的 `@if`/`@foreach` 生成普通 C# `if`/`foreach`，在 Build 时按当前值展开一次，不参与失效重算；
  - 内容中的 `{expr}`/`P={expr}` 是 Build 时快照，不生成 `U`/`R`；
  - 内容**体内**不得出现 `@bind` → `EUI040`；`Key` → `EUI010`；裸文本 → `EUI006`；
  - 内容中的子组件由接收方 `TrackContent` 登记，随接收方释放；不得 `Track` 到模板父组件；
  - 需要内容随父状态变化时，把组件标签放进父模板的 `@if`/`@foreach`，或让内容自身是独立组件。
- 组件标签不继承控件的 StyledProperty 表面；`Padding` 等控件属性不会自动成为组件参数。
- 组件自身状态只对子模板可见；父不能通过标签访问。
- 组件不做路由事件（design/14 §14.6.2）；需要跨组件冒泡时，组件把内部根元素的
  `RoutedEvent` 转成参数回调。
- 组件为文档根时：`Track(cmp)` → `Mount()` → `return cmp.Root;`，不挂父级。

### 4.15 保留属性与保留语法

| 写法 | 语义 | 状态 | 现在的诊断 |
|---|---|---|---|
| `Key={expr}` | `@foreach` 条目身份 | ✅ | `EUI010`（位置/形式/空表达式） |
| `Ref={ident}` | 元素/组件实例赋给 code-behind 字段 | ✅ C5（生成形态 spec/02 §4.14） | `EUI029`（非简单标识符=语法级；字段缺失/不可写/类型不符=生成器探针语义级） |
| `Class="..."` | 样式类 | G4 | ✅ `EUI031`（C3，D2 关闭） |
| `Style="..."` | 内联样式 | G4 | ✅ `EUI031`（C3，D2 关闭） |
| `P:bind={expr}` | 旧双向绑定（≡ `@bind-P`） | ✅ → 弃用 | `EUI043` Warning（S3 起） |
| `P:value={expr}` | 旧单向别名（≡ `P={expr}`） | ✅ → 弃用 | `EUI043` Warning（S3 起） |
| `<Slot Name="...">` | 具名插槽（消费侧） | G4 | ✅ `EUI031`（C3，D3 关闭） |
| `<ContentSlot Name="..." />` | 插槽宿主（组件模板侧） | G4 | ✅ `EUI031`（C3，D3 关闭） |
| `<DataGrid<Order>>` | 泛型标签 | G4 | `EUI030`（当前 `EUI100`，D5） |
| `Grid.Row="1"` | 附加属性 | G4 | ✅ `EUI031`（C3，D19 关闭） |

保留语法的统一规则：

- 不得把保留名用作普通控件/组件属性（控件作者侧 `EUI026`）；
- 使用保留语法时**必须**报"已保留、未实现"类诊断（`EUI031`/`EUI029`/`EUI030`；
  插槽错误从 G4 起用 `EUI022`），不得退化成 `EUI001`/`EUI002` 的拼写建议；
- `@bind-` 不是保留语法：它是双向绑定的语法形态；目标/源非法分别报 `EUI040`/`EUI037`，
  不得退化成 `EUI001`/`EUI002`；
- 实现保留语法时只增加状态，不改变已有语法的含义。

## 5. 字面量

### 5.1 判定与转换时机

- 属性值 token 是双引号字符串 → 字面量；是不带引号的 `{...}` → 表达式。引号内的 `{x}` 是字符串，不是表达式。
- 字面量在**编译期**按目标类型转换；生成代码直接写 C# 字面量或常量构造，运行时不解析字符串、不反射（R2）。
- 数字/布尔/枚举/TimeSpan 转换前 `Trim()`；`string` 保留原样（包括前后空格与换行）。
- 使用 `InvariantCulture`；小数点固定 `.`，不使用千分位；`EUI003` 消息给出期望格式示例。
- 无转换器的类型：字面量报 `EUI023`；第三方自定义转换器 v1 不支持（编译期执行用户代码违反 R1）。

### 5.2 内置转换器

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
- 转换器查找键固定为目录的 `UiTypeShape.CanonicalKey`（内置类型 = C# 关键字名，
  其余 = 命名空间限定全名），**不得**用自带 `global::` 的显示类型名当键。

### 5.3 字面量与表达式的边界

- `P="{x}"` → 字符串 `{x}`；`P={x}` → 表达式 `x`。
- `P="true"` 对 `bool` 属性是字面量；`P={true}` 是表达式；两者生成结果相同。
- 字符串里需要引号/反斜杠且要求精确字符时，用表达式：`P={"a\"b"}`。
- 字面量不能被当作事件处理器（`OnClick="Foo"` → `EUI004`）；事件必须表达式。
- 字面量不能作为 `@bind-P` 的值（`@bind-Checked="true"` → `EUI037`）；双向绑定必须给可写表达式。

## 6. 诊断

### 6.1 责任与 span

- 每个诊断带稳定代码、精确 span、英文消息；文档/编辑器可本地化消息，代码不变。
- 标签诊断指向标签名；属性诊断指向属性名/值；表达式错误由 C# 编译通过 `#line` 指回 `.ui` 行。
- 未知名给最近建议（Levenshtein ≤ 3，最多 3 个）；建议不改变解析结果。
- 模板侧错误（用错）由分析器报；元数据侧错误（控件声明错）在源项目声明处报 `EUI026`，
  引用程序集只能在用点上暴露（`EUI019`/`EUI020`/`EUI021`）。
- 所有诊断消息为英文（与现有代码一致）；代码/消息保持稳定。

### 6.2 诊断表

| 代码 | 触发 | 位置 | 级别 | 阶段 |
|---|---|---|---|---|
| `EUI001` | 未知标签 | 标签 | Error | ✅ |
| `EUI002` | 未知属性/事件/参数；若同名 CLR 属性存在但未注册 `StyledProperty`，提示补注册 | 属性名 | Error | ✅ |
| `EUI003` | 字面量格式错/范围错/枚举成员不存在 | 属性值 | Error | ✅（§5.2 全表，C3） |
| `EUI004` | 事件缺 `{expression}` / 事件用 `:bind` 或 `@bind-` | 属性 | Error | ✅ |
| `EUI005` | 缺少与 `.ui` 同名的 code-behind | 文件 | Error | ✅ |
| `EUI006` | 内容模型不允许（文本/元素/控制流/根层文本或块） | 子内容/根 | Error | ✅ |
| `EUI007` | `@foreach` 集合不是 `IEnumerable<T>`、`T` 无法推断、`IAsyncEnumerable`/`IEnumerator`/非泛型/`dynamic` | 集合表达式 | Error | ✅ S2 |
| `EUI008` | `@switch` 保留 | 指令 | Error | ✅ |
| `EUI009` | 未知指令 | 指令 | Error | ✅ |
| `EUI010` | `Key` 位置错（非条目第一个元素根）、字面量、空表达式 | 属性 | Error | ✅ S2 |
| `EUI011` | code-behind 非 `partial` | 类声明 | Error | ✅ |
| `EUI012` | 根元素数量不为 1 | 文档 | Error | ✅ |
| `EUI013` | 文件名不是合法 C# 标识符 | 文件 | Error | ✅ |
| `EUI014` | **停用**：v3 item 模板禁止组件级状态；v4 允许条目闭包读取 `this` | — | — | — |
| `EUI015` | code-behind 嵌套 | 类声明 | Error | ✅ |
| `EUI016` | 标签冲突（≥2 类型） | 标签 | Error | C3 |
| `EUI017` | 标签类型不可暴露（public 但抽象/嵌套） | 标签 | Error | C3 |
| `EUI018` | 缺少 public 无参构造 | 标签 | Error | C3 |
| `EUI019` | 属性不可写 / `{Name}Property` 类型不符——**仅触发于引用程序集控件的使用点**（元数据问题经 `def.Problems` 暴露，消息带完整类型名；design/14 §14.5.4）；源项目控件的同类问题用点落 `EUI002`，声明处归 `EUI026` | 属性名 | Error | ✅ C5（使用点） |
| `EUI020` | 事件字段缺失或类型不是 `RoutedEvent`——使用点口径同 `EUI019`（`On{x}` ↔ `{x}Event` 字段匹配，仅引用程序集） | 属性名 | Error | ✅ C5（使用点） |
| `EUI021` | 内容目标缺失/类型不匹配：控件 `Text` 目标、组件 `[UiContent]` 参数类型非法、组件内容参数经 `P=` 赋值、单根内容参数接多根；**引用程序集控件的破损内容目标在使用点报**（内容被降级为 None 时接管 `EUI006` 的 children 诊断，design/14 §14.5.4） | 子内容 | Error | ✅ C3（控件）/ ✅ C4（组件）/ ✅ C5（使用点） |
| `EUI022` | 插槽未知/重复/位置非法 | 标签/属性 | Error | G4/C6 |
| `EUI023` | 属性类型没有字面量转换器 | 属性值 | Error | ✅ C3 |
| `EUI024` | 事件处理器签名不匹配（可静态判定时） | 属性值 | Error | 后续切片（Roslyn 层；委托类型已按语法形态判定，tokenizer） |
| `EUI025` | 同一标签重复属性（任一侧是绑定的同名重叠 → `EUI033`） | 属性名 | Error | ✅ C3 |
| `EUI026` | 控件/组件元数据声明非法（源项目） | 声明处 | Error/Warning | C3 |
| `EUI027` | 组件必填参数缺失 | 标签 | Error | ✅ C4 |
| `EUI028` | 组件参数/委托声明非法：非 public / 无 setter / `init` / C# `required` / 非法泛型实参 / 委托含 `ref`/`out`/`in` | 声明处/属性名 | Error | ✅ C4 |
| `EUI029` | `Ref` 目标非法：非简单标识符（分析器，语法级）/ 字段缺失、只读、静态、类型不符（生成器探针，语义级；引用 C# 诊断） | 属性值 | Error | ✅ C5 |
| `EUI030` | 泛型标签暂不支持 | 标签 | Error | C6/G4 |
| `EUI031` | 语法已保留、尚未实现（`Class`/`Style`/`<Slot>`/`<ContentSlot>`/附加属性） | 标签/属性 | Error | ✅ C3（`<Slot>`/`<ContentSlot>` 仅当目录无同名真实控件时拦截） |
| `EUI032` | **停用**：原"非 Signal 组件参数"，后继 EUI044 亦停用 | — | — | — |
| `EUI033` | 同一目标重复赋值（文本内容 + 显式目标属性；`P=` 与 `@bind`/`:bind`/`:value` 冲突） | 属性值/文本 | Error | ✅ S2 |
| `EUI034` | **停用**：v3 非法 `[UiState]`/`[UiObservable]` 声明；状态生成器移除 | — | — | — |
| `EUI035` | **停用**：v3 非法 `[UiComputed]`；`[UiComputed]` 移除 | — | — | — |
| `EUI036` | **停用**：v3 "反应式位置读取不可观察成员 → 一次性"；v4 无依赖分析 | — | — | — |
| `EUI037` | `@bind` 源非法（非可写左值/静态/const/readonly/init/方法/索引器/`?.`/struct 属性中间段/值类型 item 成员/类型不符/自绑） | 表达式 | Error | ✅ S3（控件源；自绑静态不可判定，按 §4.5 不报） |
| `EUI038` | **停用**：v3 "不可分析结构 → 一次性"；v4 任意表达式合法 | — | — | — |
| `EUI039` | **停用**：v3 `Key` 不得依赖可观察状态；v4 Key 可引用任意可读表达式 | — | — | — |
| `EUI040` | `@bind` 目标非法（控件不可写/无 `{Name}Property`；组件参数无 public setter/委托/内容/保留；组件缺少 `{P}Changed` 或类型不符；组件内容体内出现 `@bind`） | 属性名/内容 | Error | ✅ S3+C4（控件 ✅ S3；组件 ✅ C4；`{P}Changed` 类型匹配静态不可判，记 D73） |
| `EUI041` | **停用**：v3 集合未实现 INCC；v4 每次刷新重新枚举 | — | — | — |
| `EUI042` | **停用**：v3 item `@bind` 禁止组件级源；v4 允许 | — | — | — |
| `EUI043` | 旧语法 `:bind`/`:value` 弃用 | 属性名 | Warning | ✅ S3 |
| `EUI044` | **停用**：v3 非可观察组件参数不刷新；v4 所有参数推送 + `Invalidate()` | — | — | — |
| `EUI045` | code-behind 与生成代码冲突（声明 `Build()`/`Render()`、基类不是省略/原样 `Component`、与生成/基类成员同名造成隐藏/歧义，包括隐藏 `Invalidate`） | code-behind 声明 | Error | C3 |
| `EUI100` | 纯语法错误（标签不匹配、属性缺值、未闭合注释、非法 `@bind-` 形态等） | 语法位置 | Error | ✅ |

停用码的纪律：

- 分析器/生成器不得再发出 `EUI014`/`EUI032`/`EUI034`–`EUI036`/`EUI038`–`EUI039`/`EUI041`–`EUI042`/`EUI044`；
  测试必须断言零出现。
- 停用码保持"停用"，不复用编号；不得用它们表达新语义。
- 新增诊断从 `EUI046` 起。

### 6.3 建议算法

- 未知标签/属性/事件/参数时，在**全部命名空间**里找最近名（Levenshtein ≤ 3）；
  候选超过 3 个时只列前 3。
- 候选集包括可用与不可用标签（不可用标签给出原因，避免把拼错的名字指向不可用类型）。
- 跨类别也提示（"did you mean event 'OnClick'?"）。
- 名字命中公开 CLR 属性但没有 `{Name}Property` 时，提示"未注册 StyledProperty，不参与模板"
  （`EUI002`），而不是建议改写拼写。
- 建议只做提示，不改变解析结果。

### 6.4 保留语法的诊断纪律

- 保留语法必须报 `EUI031`/`EUI029`/`EUI030`（插槽错误 G4 起用 `EUI022`），
  不得退化成 `EUI001`/`EUI002`。
- `@bind-` 不是保留语法：目标/源非法报 `EUI040`/`EUI037`，不得退化成未知属性。
- 未实现阶段写 `P:value` 必须报 `EUI002` + 弃用说明，而不是静默当普通属性名。
- 任何"看似可用、运行时才炸"的降级都不允许；诊断缺失视为实现缺陷。

## 7. 实现状态与阶段

### 7.1 阶段摘要

| 阶段 | 语法/语义内容 |
|---|---|
| ✅ 当前可用 | 元素/自闭合；属性字面量（string/bool/double/int）；控件 `On*` 事件；`Text`/`TextRuns`/`Children` 文本与内容；`@if`/`@foreach` 与 `Key`；控件 `@bind-P`（含 item 源）与旧 `:bind`/`:value` 弃用告警；注释；空白规范化；code-behind 诊断 |
| **S1** ✅ | `Component.Invalidate/ProcessInvalidations/Render/ShouldRender`；失效队列与合并；生成字段与 `U{N}`；根级 `P=`/文本重算；控件事件自动失效（2026-10-30 通过） |
| **S2** ✅ | 块 v4：`BlockScope.RenderActions`；`@if` 分支刷新；`@foreach` 任意 `IEnumerable<T>`、null=空、Keyed 复用；嵌套块；`EUI007`/`EUI010`/`EUI033`（文本 + 显式目标）（2026-10-30 通过） |
| **S3** 🟡 | `UiBinding` v4；控件 `@bind`；item 绑定；`EUI033`（`P=` 与绑定冲突）/`EUI037`/`EUI040`/`EUI043` —— 控件侧已落地（2026-11-02）；组件 `PChanged` 依赖 C4 组件标签，记 D54 —— 组件绑定目标/回调已随 C4 落地（2026-11-05） |
| **S4** | 组件所有权/深度排序；参数推送与相等保护；异步/后台失效；`ShouldRender` |
| **S5** | 基准/预算；AOT/裁剪；死循环上限；旧语法清理；诊断清理与最终审计 |
| **C1–C6** | 符号派生目录、控件事件迁移、字面量扩展、组件标签、`Ref`、插槽/附加属性/泛型标签 |
| **G3** | 块布局透明、`Ref`、逐文件增量、`#line` 列映射 |
| **G4** | 样式/主题、具名插槽、附加属性、泛型标签、组件参数 diff 评估 |

逐项阶段见上表；v3 语法项状态表（design/03 §3.5.2）仅作历史。
v3 代码差距见 design/03 §3.5.3（D1–D41，历史）；v4 差距见 design/03 §3.5.4 与 spec/02 §12。
本文不复制那张台账。

### 7.2 未实现语法的行为

- 未实现但已冻结的语法项，必须按 §6.2 报对应诊断（或 `EUI031` 保留诊断）；
  不得静默接受、不得降级为"未知"。
- 诊断暂时缺失的阶段，行为按 design/03 §3.5.4 的"当前实现"列执行；
  该列是临时状态，不是规范。
- S1–S5 的出口标准见 spec/03 §13；C1–C6 见 design/14 §14.12。

## 8. 变更规则

1. 新增语法/修饰符/指令：先在本文件登记状态与诊断，再改 Parser/Analyzer/Emitter；不得先实现后补文档。
2. 已实现语义不得改义；必须改时走新语法 + 弃用期，并同步 design/03（§3.5.4）、design/14、
   spec/02 与 spec/03。
3. 语法项从 G3/G4/S1–S5 转 ✅ 时，必须同时更新 §6.2 的阶段列、§7.1 与 design/03 §3.5.4。
4. 诊断码不复用；停用码保持停用；新增从 `EUI046` 起。
5. 每次变更跑：golden 生成文本、诊断快照、样本项目（CounterApp/TaskList）编译与运行。
6. 本文与 spec/02 的生成形态、与 spec/03 的求值时机必须一一对应；
   改语法但不改生成/反应式契约的提案不完整。

## 附录 A：完整示例

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
namespace Examples;

public sealed class TodoItem
{
    public int Id { get; init; }          // 普通属性：用于 Key
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}
```

### A.3 `TodoList.cs`（code-behind）

```csharp
using System.Collections.Generic;
using EclipseUI;

namespace Examples;

public partial class TodoList : Component
{
    public List<TodoItem> Tasks { get; } = new()
    {
        new() { Id = 1, Title = "读 01 语法规范" },
        new() { Id = 2, Title = "补测试用例" },
    };

    public bool ShowCount { get; set; }

    private int _nextId = 3;

    private void AddTask()
    {
        var id = _nextId++;
        Tasks.Add(new TodoItem { Id = id, Title = $"任务 {id}" });
    }
}
```

### A.4 语义对照

| 写法 | 语义 | 依据 |
|---|---|---|
| `Gap="8"` / `FontSize="24"` | 编译期字面量转换，一次性赋值 | §5 |
| `@bind-Checked={ShowCount}` | 双向绑定；源值胜出；目标变化回写源并 `Invalidate()` | §4.5 |
| `@if (ShowCount) { ... }` | 每次渲染重算条件；分支切换销毁/重建 | §4.10 |
| `共 {Tasks.Count} 项` | 动态文本；每次渲染重设整段字符串 | §4.9 |
| `@foreach (var t in Tasks)` | 任意 `IEnumerable<T>`；每次刷新重新枚举 | §4.11 |
| `Key={t.Id}` | 条目身份；Key 相等且 item 引用相等才复用 | §4.12 |
| `@bind-Checked={t.Done}` | item 可写属性双向；回写 item 并失效组件 | §4.5 / §4.11 |
| `{t.Title}` | item 文本；复用条目每次刷新重跑 | §4.12 |
| `OnClick={() => AddTask()}` | Build 时 `AddHandler`；处理器后自动 `Invalidate()` | §4.13 |

`Tasks.Add` 发生在 `OnClick` 处理器内，处理器返回后自动失效，因此列表与计数会在同一帧更新；
若在后台线程或 `await` 之后添加任务，必须显式调用 `Invalidate()`。

### A.5 生成代码

精确生成文本、变量编号、字段提升与 `#line` 见
[spec/02 附录 A](./02-generated-code-contract.md)。
本文件只规定输入语义；不在两处维护同一份生成文本。

## 附录 B：语法速查

### B.1 属性形态

| 写法 | 含义 |
|---|---|
| `P="literal"` | 编译期字面量 |
| `P={expr}` | 每次组件渲染重算的赋值 |
| `@bind-P={expr}` | 双向绑定（源必须是可写左值） |
| `P:bind={expr}` | 旧双向（弃用） |
| `P:value={expr}` | 旧单向（弃用） |
| `OnE={expr}` | 控件事件（build-once，自动失效） |
| `Key={expr}` | `@foreach` 条目身份 |
| `Ref={ident}` | 元素/组件引用（✅ C5） |
| `Class` / `Style` | 样式（G4） |

### B.2 内容模型

| 模型 | 写什么 | 生成目标 |
|---|---|---|
| `None` | 无子内容 | — |
| `Children` | 元素、`@if`、`@foreach` | `.Add(Element)` |
| `Text` | 文本段、`{expr}` | `string` 属性 |
| `TextRuns` | 文本段、`{expr}` | `ITextContentHost.AddRun(string)` |

### B.3 旧写法迁移

| 旧写法 | 新写法 | 说明 |
|---|---|---|
| `[UiState] partial class` + partial 属性 | 普通类 + 普通属性 | 去掉状态生成器 |
| `Signal<T>` 裸名 | 显式 `.Value`，写入后 `Invalidate()` | 不再自动重写/订阅 |
| `ObservableCollection<T>` | `List<T>`/数组/任意 `IEnumerable<T>` | 每次刷新重新枚举 |
| 集合 `Add/Remove` 自动刷新 | UI 事件内自动；其它位置显式 `Invalidate()` | 写契约 |
| item 属性变化自动刷新 | 修改后 `Invalidate()` | 写契约 |
| `P={method()}` 一次性 / `EUI038` | 每次渲染求值 | 无依赖分析 |
| `@bind` 源必须是可观察路径 | 任意可写左值 | `EUI037` 收窄 |
| 组件可观察 partial 参数 | 普通参数 + `{P}Changed` 回调 | `EUI040` |
| `UiBinding` v3 订阅签名 | v4 `UiBinding.TwoWay(..., invalidate)` | spec/03 §8.2 |
| `Bind.TwoWay(...)` 手写 | `UiBinding.TwoWay(...)` | 旧重载保留但标 Obsolete |
| `EUI036`/`EUI038`/`EUI041`/`EUI044` Warning | 无 | 停用 |
| `EUI014`/`EUI039`/`EUI042` Error | 无 | 停用 |
