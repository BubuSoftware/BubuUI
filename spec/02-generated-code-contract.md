# 02 · 生成代码契约

> 状态：v2 定稿（2026-10-29），对应模板语义 **v4 失效重算**；S1–S3 与 C1–C6 的生成形态已落地
> （S1/S2 2026-10-30，S3 控件侧 2026-11-02、组件侧随 C4 2026-11-05，C6 插槽 2026-11-05），其余见 §12。
> 本文是 `.ui` 模板**生成代码**的规范正本：定义生成器必须遵守的不变量、文件级契约、
> 命名与编号、每种构造的精确生成形态、语句顺序、首次求值与失效、确定性要求与契约测试。
> 取代 spec/02 v1（依赖订阅模型）；v1 中 `UiWatch`/`Signal<T>`/`[UiState]` 相关的生成形态全部作废。
>
> 关联：[01 模板语法](./01-template-syntax.md)（输入语义）、
> [03 反应式与绑定](./03-reactivity-and-binding.md)（失效、块刷新、双向绑定）、
> [design/14](../design/14-control-template-contract.md)（控件/组件元数据）、
> [design/04](../design/04-runtime-and-elements.md)（元素树、属性引擎、事件）。

## 0. 文档约定

### 0.1 范围

| 本文定义 | 本文不定义（去哪看） |
|---|---|
| 生成文件的结构、命名、`#line`、禁止项 | `.ui` 词法/句法/语义（[spec/01](./01-template-syntax.md)） |
| 每种模板构造的精确 C# 形态与语句顺序 | 失效队列、`Render()` 调度、块刷新算法（[spec/03](./03-reactivity-and-binding.md)） |
| 生成代码可调用的冻结运行时 API 面 | 控件如何声明属性/事件/内容模型（design/14） |
| 首次求值、动态重算、资源登记与释放的生成侧边界 | 布局/渲染/输入（design/05） |
| 确定性、增量、契约测试、版本兼容 | 实现偏差的完整台账（design/03 §3.5.4） |
| 生成器与诊断的责任边界 | 诊断码的完整语义表（spec/01 §6、spec/03 §11） |

### 0.2 权威关系

1. **生成形态以本文为准**。其他文档中的生成示例不再作为实现依据；冲突时按本文执行。
2. **输入语义以 spec/01 为准**。本文不重新解释语法；每个生成形态对应 spec/01 的一个已冻结语义。
3. **反应式语义以 spec/03 为准**。本文只冻结"生成代码在何时求值、写哪个属性、登记到哪个作用域"；
   `Invalidate()` 的队列、`ProcessInvalidations()` 的顺序、块刷新与 Keyed 复用算法由 spec/03 定义。
4. **控件/组件元数据以 design/14 为准**。本文只消费目录（`UiElementDef`/`UiPropDef`/`UiEventDef`/
   `UiParameterDef`）的解析结果，不重新定义标签暴露规则。
5. **模板状态是普通 CLR 成员**。`[UiState]`/`[UiObservable]`/`[UiComputed]`/`Signal<T>`/`UiWatch`/
   `INotifyPropertyChanged` 不再是模板契约；生成代码不得依赖它们。
6. **偏差只允许出现在台账**。实现与本文的差异必须登记在 design/03 §3.5.4 与本文 §12；
   台账之外的差异视为实现缺陷。
7. **两份规范一一对应**。spec/01 的每个构造必须在本文 §4 有生成形态；本文不得引入
   spec/01 未定义的语义。改一边不改另一边视为提案不完整。

### 0.3 规范用语

| 词 | 含义 |
|---|---|
| **必须** | 违反即视为生成器缺陷；契约测试必须覆盖 |
| **不得** | 生成器禁止生成或接受的形态 |
| **应当** | 允许暂缺，但评审必须记录原因与阶段 |
| **可以** | 实现自由度，不影响语义与可观察行为 |

### 0.4 状态标记

| 标记 | 含义 |
|---|---|
| ✅ | 当前实现已具备该结构，行为与本文一致（§12 列出的偏差除外） |
| S1–S5 | 依赖 v4 失效重算运行时，按 spec/03 §13 分阶段实现 |
| C1–C6 | 依赖控件/组件元数据迁移，见 design/14 §14.12 |
| G3 / G4 | 语义已冻结，排在 G3 / G4 实现 |
| ⛔ | 该形态永不允许生成；出现即测试失败 |

### 0.5 构造索引

| spec/01 构造 | 本文生成形态 | 当前状态 |
|---|---|---|
| 元素/自闭合标签 | §4.1 | ✅ |
| 字面量属性 | §4.2 | ✅（C3 全表） |
| `P={expr}` 单向（控件） | §4.3.1/§4.3.2 | ✅ S1 |
| `P={expr}` 单向（组件参数） | §4.3.3 | ✅ C4 |
| `@bind-P={expr}`（控件） | §4.4.1/§4.4.2 | ✅ S3 |
| `@bind-P={expr}`（组件） | §4.4.3 | ✅ C4 |
| `P:bind` / `P:value` | §4.4.5/§4.5 | ⛔ 已移除（K27）：`EUI046`，不生成代码 |
| `OnE={expr}` 控件事件 | §4.6.1 | ✅ S1 |
| 组件委托参数事件 | §4.6.2 | ✅ C4 |
| `Text` 模型文本 | §4.7 | ✅ S1 |
| `TextRuns` 模型文本 | §4.8 | ✅ S1 |
| `@if` | §4.9 | ✅ S2（多分支 `else`/`else if` 见 §4.9.3，✅ S6） |
| `@switch` | §4.17 | ✅ S6 |
| `@foreach` | §4.10 | ✅ S2 |
| 组件标签 | §4.12 | ✅ C4 |
| `Key` | §4.13 | ✅ S2 |
| `Ref` | §4.14 | ✅ C5（Class/Style 仍 G4；附加属性 ✅ G4 → §4.19） |
| 具名插槽（`<Slot>` + `<ContentSlot>`） | §4.12.5 | ✅ C6 |

### 0.6 示例约定

本文代码块用于说明**形态与顺序**，为可读性做了两处省略：

- 省略 `#line`/`#line hidden`；实际生成必须按 §2.4 映射。示例中的 `//` 注释**不属于生成文本**
  （生成代码不含注释，§2.5）。
- 示例省略与形态无关的缩进/空行；空行限制按 §2.5，编号与顺序按 §3.2/§8.1。
  示例与规则冲突时规则优先。

## 1. 生成器不变量 R1–R10

以下不变量与 spec/03 §1 同源。违反任一条的实现不得合入。

| # | 不变量 | 在生成文本中的体现 |
|---|---|---|
| **R1** | 只调用公开 API，编译期只读符号，不执行用户程序集 | 生成文本只使用 §7 的冻结 API 与 BCL；没有 `Activator`、`Type.GetType`、反射、`dynamic` |
| **R2** | 字面量在编译期转换，不做运行时解析 | 生成 C# 字面量；没有 `Convert`/`Parse`/区域性依赖 |
| **R3** | 属性写入经过公开 CLR setter → `StyledProperty` → 失效 | 一律 `elN.P = ...` / `v => elN.P = v`；**禁止** `SetValue`/`GetValue`；`@bind` 必须用 `UiBinding` |
| **R4** | `Build()` 是第 0 次渲染，块与绑定在 Build 完成首次求值 | 每个动态绑定在 Build 调用一次 `U{N}()`/`R{N}()`；根级块显式 `Refresh()`；`Render()` 只重算，不构造 |
| **R5** | 生成器不改变异常语义，资源先登记后使用 | 无 `try`/`catch`；Build 首次求值异常向上传播；组件/绑定/块先登记作用域 |
| **R6** | 无静态状态、无隐式所有权 | 没有 `static` 字段/缓存/`ConditionalWeakTable`；作用域登记显式可见 |
| **R7** | 同一输入生成确定性输出 | 编号、成员顺序、字面量格式、`#line` 形式固定；hint name 稳定；无时间戳/GUID |
| **R8** | 每个资源有唯一作用域所有者，释放幂等 | `Track` / `scope.AddDisposable` / `receiver.TrackContent` 三选一；事件处理器不登记 |
| **R9** | 每个用户表达式有固定数量的求值位置，不重复生成 | 一个绑定点的表达式只出现一次（`@bind`/`PChanged` 的读写位置除外，§3.5） |
| **R10** | 一个绑定点的所有写入共用一个 `U`/`R`，不拆分 | 同一文本节点的多个表达式、同一点的读写共用一个函数；按源码顺序调用 |

补充约束：

- **R3 的边界**：`Element.SetValue` 仍是属性引擎内部实现；生成代码不得调用它，
  也不得通过旧的 `Bind.TwoWay` 绕过。`@bind` 必须走 `UiBinding`。
- **R4 的边界**：块的 `Refresh()` 内部可以构造/销毁内容与条目；这是块协调器的职责，
  不是 `Render()` 直接构造。生成代码不直接调用 `Children.Clear()` 做 diff。
- **R5 的边界**：`BlockScope` 由块创建并传入模板；模板失败时块负责释放已登记的部分资源；
  生成代码不写补偿逻辑。
- **R8 的边界**：事件处理器不是 `IDisposable`，随元素生命周期释放；不得把事件登记进 `Track`。
- **R9 的边界**：`@bind` 源天然需要读、写两个表达式位置；组件 `@bind` 需要推送读、回调读、回调写三个位置。
  这属于同一绑定点的固定形态，不是重复生成；UI 表达式（`P=`、文本、条件、集合、Key、事件）只出现一次。

## 2. 编译单元与文件契约

### 2.1 输入与输出

```text
{Name}.ui  (AdditionalFiles) ─┐
{Name}.cs  (code-behind)      ├→ 模板生成器 → {Name}.{stableHash}.g.cs（partial class）
UiCatalog（符号/手写目录）     ─┘
```

- 一个 `.ui` 文件生成**恰好一个** `partial class {Name}`，类名 = 文件名（去扩展名）。
- 输出文件必须包含且只包含该类的生成成员；**不得**生成事件处理器、属性、构造函数、
  `Dispose()` 或其他成员。code-behind 是唯一状态载体（spec/01 §1.2）。
- hint name 固定为 `{ClassName}.{stableHash}.g.cs`：
  - `stableHash` = 对 `AdditionalText.Path` 的 UTF-8 字节做 SHA-256，取 16 个小写十六进制字符；
  - 同一路径跨编译稳定；不得使用时间戳、随机数、GUID、编译序号；
  - hint name 不是语义；契约测试只断言唯一性、稳定性与确定性，不断言具体哈希值。
- 生成器**不得**读取另一个源生成器的输出（包括其他模板生成器）。生成决策只依据 code-behind 的公开声明
  （`Build`/`Render` 冲突等）与目录元数据。
- 生成器必须支持同一编译内多个 `.ui`；每个文件的输出互不影响，编号在每个文件内独立从 0 开始。
- 增量：对同一文件内容的重复生成必须字节级一致；一个 `.ui` 的修改不得改变其他文件的输出。

### 2.2 文件头、命名空间与 using

生成文件必须以以下内容开始（顺序固定；`namespace` 行**总是**生成，取值即模板的 `@namespace` 声明）。
**K22（✅ S6 已实现，spec/01 §1.5 / D85 已关闭）**：模板 `@using` 对应的普通 using 组插在
`#nullable enable` 与 `namespace` 之间：

```csharp
// <auto-generated> 由 EclipseUI 模板编译器生成；请勿手动修改。 </auto-generated>
#nullable enable
using {Ns1};
using {Alias} = global::{TargetType};
namespace {Namespace};
```

- `{Ns*}`：模板根层 `@using {Ns}` 的命名空间，**按模板出现顺序、去重**；无 `@using` 时整块省略。
- `{Alias} = global::{TargetType}`：K31 的 `@using {Alias} = {TargetType}`，同样按模板顺序、去重。
  目标**一律补 `global::`**——别名行是为了给 `@code`/表达式提供短名，若写成非全限定，
  它会被生成类所在命名空间的同名类型劫持，与 §2.2 上文的短名风险同源。
- `{Namespace}`：模板 `@namespace {Dotted.Name}` 的声明值。**声明是必填契约**（2026-11-11）：
  - 缺失 → `EUI055`（Error，短路产物，不给生成代码）；
  - 与同名 code-behind 类的命名空间不一致 → `EUI056`（Error）；
  - **没有保留值**（2026-11-11 拍板）：`global` 只是普通名字，不表示「无命名空间」。C# 写不出
    global namespace 声明，所以处于全局命名空间的 code-behind **无法与任何声明相等**——按
    `EUI056` 报告并提示把该类搬进命名空间。生成器因此永远只产出具名命名空间。
  生成器统一使用**文件作用域命名空间**，不生成块式 `namespace { }`。
  声明与 code-behind 一致时，`partial` 合并成立；此前命名空间由「code-behind 有则跟随、无则
  取项目 RootNamespace」隐式推导，同一份模板在不同工程里会落到不同空间。
- **K22 / K31**：允许且只允许输出模板 `@using` 对应的两类 using 子句（普通命名空间导入、类型别名）；
  **不得**生成 `global using`、`using static` 或模板未声明的 using。
  生成代码仍一律 `global::` 全限定，这些 using **只为 `@code` 与模板表达式提供短名**，
  不用于生成代码的类型引用。
- 所有具名类型引用必须带 `global::` 前缀，包括：
  - `new` 表达式、类型实参、泛型参数、数组/列表元素类型、`EqualityComparer<T>`；
  - `Action`/`Action<T>`/`Func<T>`、`IEnumerable<T>`、`IReadOnlyList<T>`、`List<T>`、
    `IDisposable`、`String`/`Int32` 等 BCL 类型。
- 允许不带的：关键字、`var`、局部变量名、lambda 参数名、字符串字面量、`this`、数字字面量。
- **为什么不靠 `@using` 省掉 `global::`**（K22 落地后常被误读为「双重保险、可以简化」）：
  - `@using` 的职责是**标签解析**：只有已导入命名空间的候选可见，同 tag 多个可见候选 → `EUI016`，
    该文件不输出任何生成代码。它决定「`<TextBlock>` 解析成哪一个类型」，并不负责
    「生成代码里这个名字怎么绑定」；两件事不能合并成一件。
  - 生成类与 code-behind 同命名空间。若写短名 `new TextBlock()`，C# 名称查找**优先命中该命名空间内的
    同名用户类型**，`using` 不参与竞争：组件命名空间里只要有一个普通类 `TextBlock`，短名就会绑到它
    （实测 CS0029；若那个类恰好也派生自 `Element`，则更糟——静默构建出错误控件）。
  - 两个 `@using` 命名空间都有同名类型时，短名直接 CS0104（实测）。
  - `global::` **不是在重名里挑一个**：它写的永远是目录中**唯一解析成功**的那个符号
    （`ClrTypeDisplayName` = Roslyn `FullyQualifiedFormat`）；重名在到达发射前已被 `EUI016` 拦下，
    生成器不会猜。因此「重名时加限定反而错」不成立——加限定正是重名场景下唯一正确的写法。
- 生成代码必须能在以下项目设置下编译：
  - `<ImplicitUsings>disable</ImplicitUsings>`
  - `<Nullable>enable</Nullable>`
  - 不引用 `EclipseUI.Runtime` 之外的框架命名空间也可编译（只依赖显式 `global::` 引用）。
- 生成代码不得产生可空警告；字段用 `= null!;` 初始化，引用类型转换按目标类型写可空注解。

### 2.3 类壳与成员白名单

```csharp
{accessibility} partial class {Name} : global::EclipseUI.Component
{
    // 字段（仅跨方法引用）
    // Build()
    // Render()（仅动态模板）
    // private void U{N}()（仅根级动态绑定）
}
```

- `{accessibility}` 与 code-behind 声明一致（`public`/`internal`）；code-behind 未写时用 `internal`。
- 生成部分重复 `: global::EclipseUI.Component` 是合法的 partial 声明；
  code-behind 省略基类或原样重复 `: Component` 都合法，其他基类（含 `Component` 的派生类）→ `EUI045`。
- 生成成员白名单，**S6 起五类**（K23 ④，spec/01 §1.5 / D86 ✅）：
  1. 字段：`private {Type} __{prefix}{N} = null!;`
  2. `protected override global::EclipseUI.Element Build()`
  3. `protected override void Render()`（模板含根级动态构造时）
  4. `private void U{N}()`（根级动态赋值，方法体只含一个绑定点的求值/写入）
  5. `@code` 原文透传的类成员（字段/属性/方法）：整段带 span 型 `#line` 映射回 `.ui`，**不改写、不注入 using**；
     与生成保留名冲突由 `EUI045` 扩展守护；`@code` 是白名单例外，不违反下方「不得生成属性/辅助方法」。
- `Build()` 不得加 `async`、`virtual`、`sealed`、`unsafe` 或特性；必须有 `return {root};`。
- `Render()` 不得构造元素、注册事件、安装绑定、调用 `Mount()` 或 `Refresh()` 之外的块操作；
  只调用 `U{N}()`、`__bind{N}.Refresh()`、`__blk{N}.Refresh()`（按源码顺序）。
- 不得生成嵌套类型、属性、事件、构造函数、`Dispose()`、辅助方法、`#region`、注释（文件头除外）；
  **`@code` 透传成员是唯一例外（K23 ④）**，其内容是用户原文，不由生成器构造。
- 局部函数（`R{N}`）只允许出现在 `SetContent`/`SetTemplate` 的 lambda 内，
  签名固定 `void R{N}()`，不得返回值、`async`、泛型、参数。
- 类名不是合法 C# 标识符 → `EUI013`；code-behind 嵌套/非 partial 按 spec/01 §6 报错，不生成
  （code-behind 缺失不再是错误，见 §2.7）。

### 2.4 `#line` 映射

每个**包含用户表达式或字面量**的生成语句必须映射回 `.ui` 源位置：

```csharp
#line (12, 9) - (12, 24) "C:\Workspace\EclipseUI\samples\TodoList.ui"
        el0.Label = $"共 {(Tasks.Count)} 项";
#line hidden
```

规则：

1. `#line` 的行/列都是 `.ui` 的 1 基坐标；文件名是 Roslyn `AdditionalText.Path` 原样写入——
   `#line` 的文件名不按 C# 字符串字面量处理转义，反斜杠保持单个，写成 `\\` 会让映射路径变成双反斜杠；
   不规范化、不转相对路径。
2. `#line` 与 `#line hidden` 都从第 0 列开始，不跟随当前缩进。
3. **语句级映射**：一个包含用户表达式/字面量的语句（含跨多行的调用/赋值）在**首行前**放
   `#line`，在**分号行之后**放 `#line hidden`。语句内的所有用户 token 都映射到该 `.ui` 行。
4. 必须映射的语句：
   - 字面量/表达式属性赋值；
   - `U{N}` 方法体内的赋值语句；`R{N}` 局部函数体内的赋值语句；
   - `SetCondition`/`SetItems`/`SetKeySelector` 调用（含用户表达式）；
   - 组件内容中的快照赋值、`if` 条件、`foreach` 集合；
   - `UiBinding.TwoWay(...)` 整条调用语句（含 `readSource`/`writeSource`）；
   - 组件 `PChanged` 回调赋值整条语句；
   - 事件处理器委托局部 `h{N}` 的初始化语句；
   - 静态文本 `AddRun("...")` 与文本目标赋值。
5. **不映射**：元素/块/组件构造、字段声明、`Children.Add`、`AddRoot`/`AddRenderAction`/
   `AddDisposable`、`Track`、`Mount`、`Refresh`、`U{N}();`/`R{N}();` 调用、包装 lambda、
   `return`、`#line` 自身。
6. `SetContent`/`SetTemplate` 的调用行不映射（lambda 是生成脚手架）；lambda 体内的用户语句按规则 3/4 映射。
7. 每个映射语句后**必须**紧跟 `#line hidden`；不允许连续多个映射语句共用一个 `#line`。
8. 生成器不得重复报告 C# 类型错误；类型错误由 C# 编译器经 `#line` 落到 `.ui`。
9. **列映射（G3 已收敛，2026-11-08，台账 D79）**：指令统一用增强型 span 形态
   `#line (sl, sc) - (el, ec) "path"`，不再发射 `#line N "path"`：
   - `(sl, sc)` 由用户表达式/字面量在 `.ui` 里的 span 起点换算（跳过前导空白），
     `(el, ec)` 取该 span 的终点；不含 `{…}` / 引号定界符（`Lexer.InnerSpan` 口径）。
   - `sc` 按生成行前缀左移：`sc = max(1, 源列 − (生成缩进列数 + 用户 token 在生成行内的偏移))`，
     让表达式首字符尽量落回 `.ui` 的起始列。生成前缀长于源列时钳制为 1，此时列退化为生成列、
     **行号仍精确**（与 v1 行为一致）；表达式靠右时列精确（契约测试
     `TypeError_MapsBackToExactColumn_WhenExpressionStartsFarRight`）。
   - 列平移只作用于 `#line` 之后紧接的一行；多行映射语句的后续行行号递增、列按生成列。
   - `end` 元组不参与单行诊断位置换算，但必须**严格晚于 start**（`el > sl`，或
     `el == sl && ec > sc`）；否则 Roslyn 报 `CS8939` 并让整段映射失效，错误落回 `.g.cs`。
     空 span 或列钳制后由发射器补 1 列宽度（`Emitter.Mapped`）。
   - `#line` 现携带源 span，因此旧语法别名（`:bind` / `:value`）与规范语法的 `#line` 指令
     不再字节相同；「生成文本相同」的等价口径改为**剥离 `#line` 行后**比较（§4.12.3 规则 8 之后）。
10. 规则 9 的语义按 Roslyn 4.9.2 实测固化在 `LineSpanMappingTests`；Roslyn 升级时该测试先行。
    本章后续「生成文本」示例沿用 `#line N "path"` 简写（只用于说明 C# 语句结构），
    实际发射一律按规则 9 的 span 形态，示例中的行号不表示真实 span 坐标。
11. **表达式语义预检（✅ G4 最小闭环，2026-11-09，`EUI049` Warning）**：属性 `{expr}`、
    `@if`/`else if` 条件与插值文本段经 `TemplateProbe.ProbeExpressionErrors` 探针编译——
    带 PropDef/ParamDef/AttachedDef 目标类型时按生成同形态（`T __value = default!; __value = (expr);`）
    捕获「拼错成员（CS0103）」与「类型不匹配（CS0029/CS0266）」；`@bind`/事件/`Ref` 各有专属探针
    （EUI037/EUI024/EUI029）不重复覆盖。**级别刻意为 Warning**：Error 会短路生成（EUI024 口径），
    而类型错误的规则 9 `#line` 映射契约要求生成代码带着类型错误走出去由 C# 报权威错误——
    规则 8 的「不重复报」在此退让为「`.ui` 预检警告 + C# 权威错误」双可见。`@foreach` 集合与
    `@switch` 值维持原静默推断口径（EUI007 / C# 兜底）。

### 2.5 生成文本禁止项

生成代码**不得**包含：

| 禁止项 | 原因 |
|---|---|
| 模板 `@using` 之外的 using、`global using`、`using static` | 依赖项目设置，违反 R7；K22 只放行模板声明的普通 using，K31 只放行模板声明的类型别名 |
| `SetValue` / `GetValue` | 绕过属性引擎，违反 R3 |
| `Bind.TwoWay` | 旧实现违反 R3；v4 只用 `UiBinding` |
| 反射、`dynamic`、`Activator`、`Type.GetType` | 违反 R1 |
| 字符串按成员名查找 | 违反 R1 |
| `static` 字段/属性/缓存 | 违反 R6/R8；跨组件泄漏 |
| `ConditionalWeakTable`/弱引用表 | 隐式所有权，违反 R8 |
| `try`/`catch`/`finally`/`lock` | 生成器不改变异常语义；线程模型是 UI 线程 |
| `async`/`await`/`Task` | 模板是同步 Build；异步放 code-behind |
| `#pragma warning disable` | 不得压掉用户警告 |
| `DateTime.Now`/`Guid.NewGuid`/随机数 | 违反 R7 |
| `nameof` 之外的"软引用"（字符串成员名） | 违反 R1；`nameof` 允许 |
| `Children.Clear()` / 手工 diff | 块协调器职责；生成代码不得绕过块 |
| 对用户类型的 `partial` 扩展、`new` 成员 | 只生成白名单成员 |

其他约束：

- 不生成 `checked`/`unchecked`；字面量转换在编译期完成（R2）。
- 不生成对 `Element.Measure/Arrange` 的直接调用；布局由宿主驱动。
- 不生成 `GetHashCode`/`Equals` 调用（相等保护由 `EqualityComparer<T>.Default` 承担）。
- 不生成注释、`#region`、`TODO`、空行标记；空行允许，但不得超过连续两行。

### 2.6 非法输入与失败语义

- 解析/分析阶段存在 **Error** 诊断时，生成器的输出不作为契约；但生成器**不得抛异常**（R5）。
- 若文档没有可用根元素，生成器应当输出最小 `Build()`：

```csharp
protected override global::EclipseUI.Element Build()
    => new global::EclipseUI.Elements.StackPanel();
```

  该输出只为避免级联编译错误；诊断仍然阻止构建。存在错误诊断时，生成器可以只输出该最小成员。
- 单个属性/元素解析失败时，生成器可以跳过该属性/子节点，但不得生成部分初始化后继续产生
  新的模板诊断；诊断由分析器负责。
- `Build()` 内首次求值抛异常时，异常向上传播；生成代码不捕获、不吞掉、不转换为诊断。
  已登记的资源（`Track`）由宿主在 `Dispose()` 时释放。
- 组件 `Mount()` 失败时，组件实例由调用方作用域持有（登记在 `Mount()` 之前）；
  `BlockScope` 内的组件由条目作用域释放。生成代码不写补偿逻辑。
- 块内容构造失败时，`BlockScope` 的已登记资源由块释放；生成代码不捕获。

### 2.7 第二组件来源：`.ui` 独立成组件

`.ui` 是组件的**一种来源**，不是 code-behind 的附属物。**code-behind 不是必需的**，三种形态
产生等价的组件：

| 形态 | 组件身份来自 | 可作 `<Tag />` | 对外参数 |
|---|---|---|---|
| `Counter.ui`（无 `.cs`） | `.ui` 文件本身 | 是 | 无 |
| `Prefs.ui` + `Prefs.cs`（不声明基类） | `.ui` 文件本身 | 是 | `.cs` 里的 `[UiParameter]` |
| `Card.ui` + `Card.cs : Component` | code-behind 的基类 | 是 | 同上 |

- 省略 code-behind 时生成器仍产出 `partial class {Name} : Component`（R1），组件可实例化、
  可被其他模板引用。**`EUI005` 已停用**：没有同名 `.cs`、也没有 `@code` 的纯展示组件合法
  （此前 K23 ④ 的「`@code` 或同名 partial 至少其一」在本节放宽为两者皆可省略）。
- **判定顺序**（目录侧，design/14 §14.10.2）：基类链命中 `Component` / `Element` → 按基类；
  否则若存在**同名且同命名空间**的 `.ui` 声明 → 组件；否则不进目录。
  只放宽「没写基类」这一条：显式派生了其他类的类型仍按继承链判定，避免同名无关类型被误认。
- **参数与对外 API 必须写在 `.cs`**。目录在输入编译上构建（生成器读不到自己的产物），
  所以 `@code` 里的 `[UiParameter]` / `[UiSlot]` / `[UiContent]` 只在该模板内部可见；
  其他模板使用该参数时按 `EUI002`（unknown parameter）报出。这是「想要参数就建 `.cs`」的
  唯一原因，不是风格要求。在 `@code` 里写这三个特性 → `EUI057`（Warning，不短路产物），
  把这件事在声明处说清楚。
- 声明清单的来源两侧**同源同一份实现**：`UiComponentScanner.FromFile` 读「标签 = 文件名，
  命名空间 = `@namespace`」，生成器从 `AdditionalTextsProvider` 取、IDE 项目系统从
  `AdditionalFiles` 取，都是「项目里的 `.ui` 文件」。因此编译器与 IDE 的目录不再分叉——
  此前 IDE 的 Tier 2 目录建在生成后编译上（能看见 `.g.cs` 的基类），编译器看不见，
  表现为「编辑器里 `<Prefs />` 正常、`dotnet build` 报 `EUI001`」。清单只有 `(Tag, @namespace)`
  两项，改模板正文不会让目录值变化，增量缓存仍按值命中（design/14 §14.10.3）。
- `@namespace` 缺失时（`EUI055` 已报 Error、短路产物），声明清单按标签名认领，不重复报。
- 有 code-behind 时的 `EUI055` / `EUI056` 契约（§2.2）不变；本节不放松命名空间一致性。
- **实现状态（2026-11-11）**：生成器 `TemplateComponents` 管道、目录侧 `UiComponentDeclaration` /
  `UiComponentScanner` / `AppendTemplateOnlyComponents` / `OriginOf` 放宽、IDE 的
  「输入编译 + 清单」目录全部落地。回归钉在 `SecondComponentSourceTests`（12 例，含「生成产物 +
  code-behind 合成一个编译单元零错误」）、`ProjectSystemTests.Fixture9`（输入目录 ≡ 生成后目录，
  真实仓库）、`TemplateOnlyComponentTests`（headless：`@code` 字段就是 `Ref` 目标、对外可达）。
  Gallery 的 `Counter` / `Prefs` / `Showcase` / `TaskList` 四个组件都没有同名 `.cs`，是活样例。

## 3. 命名与编号

### 3.1 前缀

| 前缀 | 用途 | 示例 |
|---|---|---|
| `el{N}` | 控件元素局部变量 | `el0` |
| `cmp{N}` | 组件局部变量 | `cmp0` |
| `blk{N}` | `ConditionalBlock`/`ForEachBlock<T>` 局部变量 | `blk0` |
| `run{N}` | 动态文本 run 局部变量 | `run0` |
| `bind{N}` | `UiBinding` 局部变量 | `bind0` |
| `scope{N}` | `SetContent`/`SetTemplate` 的 `BlockScope` 参数 | `scope0` |
| `h{N}` | 事件处理器委托局部 | `h0` |
| `content{N}` | 组件内容元素列表局部 | `content0` |
| `__el{N}` / `__cmp{N}` / `__blk{N}` / `__run{N}` / `__bind{N}` | 同前缀的生成字段 | `__el3` |
| `U{N}` | 根级动态赋值实例方法 | `U0` |
| `R{N}` | 块模板内的渲染动作局部函数 | `R0` |
| `{itemVar}` | item lambda 参数（用户声明的名字） | `t` |
| `v` | 绑定/推送的局部值 | `v` |
| `a0..aN` | 组件委托包装 lambda 参数 | `a0` |

- 前缀是契约的一部分：golden 文本按此前缀比对。不得改成 `element0`、`e0`、`u0` 等。
- `N` 的分配见 §3.2；前缀与 N 组合后必须唯一。
- 字段名是 `__` + 被提升实体的局部名；提升不产生新的 `N`（`__bind3` 对应 `N=3`）。

### 3.2 计数器规则

生成器维护两个计数器，均在每个文件内从 0 开始：

| 计数器 | 递增时机 | 作用域 |
|---|---|---|
| `idCounter` | 每声明一个带 `{N}` 的局部变量、字段或 lambda 参数时递增 | 整个文件（跨 `Build`/块/条目共享） |
| `fnCounter` | 每声明一个 `U{N}` 或 `R{N}` 时递增 | 整个文件（`U` 与 `R` 共享） |

- `idCounter` 覆盖 `el`/`cmp`/`blk`/`run`/`bind`/`scope`/`h`/`content` 全部声明。
- **不**为静态文本 run、不产生变量的属性赋值、内联 lambda 参数递增。
- 编号按**源码遍历顺序**：元素先于其属性/内容；属性按源码顺序；子节点按源码顺序；
  组件先于其参数/内容；`@if` 的 `scope{N}` 在块变量之后、内容变量之前分配；
  `@foreach` 的 `scope{N}` 在块变量之后、模板内容变量之前分配。
- `fnCounter` 按绑定点的**首次出现源码顺序**分配；同一个绑定点的多个写入共享一个函数。
- 编号在 `@if`/`@foreach` 的 lambda 内继续使用同一个 `idCounter`，不重置。
- 允许编号不连续（例如静态 run 不占号）；不得复用已分配编号。
- 字段提升不改变编号；字段与局部共享同一个 N 空间。

### 3.3 名称冲突

- 生成变量是 `Build()` 的局部变量；与 code-behind 字段/属性同名时按 C# 局部遮蔽规则处理，
  不影响语义。生成器不因用户存在 `el0` 字段而改名。
- `U{N}`/`R{N}` 是方法/局部函数；与 code-behind 方法同名时按 C# 规则处理；
  用户不应声明 `U0`/`R0` 这类名字（规范建议，不强制）。
- `scope{N}`/`h{N}`/`content{N}` 是 lambda 参数/局部变量；同一作用域内唯一。
- 生成器不得生成 `_`、`__`、`value`、`this` 等保留/上下文关键字作为局部名。
- item 变量名使用 `.ui` 中声明的名字（spec/01 §4.11）；包装 lambda 参数固定 `a0..aN`；
  `v` 是绑定/推送值的固定局部名，若与 item 变量同名，C# lambda 参数遮蔽合法。

### 3.4 显式类型

- 局部声明使用 `var`（`el`/`cmp`/`blk`/`run`/`bind`/`content`/`h`）。
- 泛型块必须显式写出类型实参：`new global::EclipseUI.ForEachBlock<global::Examples.TodoItem>()`。
- lambda 参数必须显式写类型的场合：
  - `SetTemplate((global::{T} {itemVar}, global::EclipseUI.BlockScope scope{N}) => ...)`；
  - `SetKeySelector((global::{T} {itemVar}) => (object?)({expr}))`。
  其余回调参数由目标委托推断类型：`v`（写值）、`a0..aN`（事件包装）。
- 数组/列表必须显式写元素类型：
  - `new global::System.Collections.Generic.List<global::EclipseUI.Element>()`
  - 禁止 `new[] { ... }` 依赖类型推断。
- 组件内容列表固定为 `List<Element>`；单根内容取 `content{N}[0]`，不生成 `Element[]`。

### 3.5 表达式出现次数（R9）

| 位置 | 表达式出现位置 | 次数 |
|---|---|---|
| `P={expr}` 控件 | `U{N}`/`R{N}` 内赋值 | 1 |
| `P={expr}` 组件 | 推送 `U{N}`/`R{N}` 内 `var v = ({T})({expr});` | 1 |
| 文本 `{expr}` | `U{N}`/`R{N}` 内插值串 | 每个表达式 1 次（同一文本节点共用一个串） |
| `@if (expr)` | `SetCondition(() => expr)` | 1 |
| `@foreach (expr)` | `SetItems(() => expr)` | 1 |
| `Key={expr}` | `SetKeySelector(... => (object?)(expr))` | 1 |
| 控件事件 `{expr}` | `h{N}` 初始化 | 1 |
| 组件事件 `{expr}` | `h{N}` 初始化 | 1 |
| `@bind-P={expr}` 控件 | `readSource` + `writeSource` | 2（读写各一） |
| `@bind-P={expr}` 组件 | 推送读 + 回调读 + 回调写 | 3（§4.4.3） |
| 组件内容快照 `{expr}` | 快照赋值 | 1 |

- 除上表列出的固定位置外，不得为了"保险"再生成一次求值。
- 事件处理器表达式不被 `Render()` 重算。
- `Ref={ident}` 是标识符而非表达式，生成形态见 §4.14（✅ C5）。

## 4. 构造的生成形态

### 4.1 元素（控件）

```csharp
var el0 = new global::EclipseUI.Elements.StackPanel();
el0.Gap = 8d;
// ... 属性、内容、子元素 ...
parentN.Children.Add(el0);   // 非根时在元素块末尾
```

规则：

1. `new` 使用目录解析出的 `ClrType`，全限定 `global::`；必须有无参构造（`EUI018`）。
2. 属性/事件/绑定按 `.ui` 源码顺序生成；`Key` 不生成赋值（§4.13）。
3. 内容在属性之后构建；子元素/块按源码顺序。
4. 元素**先完成自身构造与内容，再加入父级**；挂载点固定为 `parent.Children.Add(elN);`，
   由该元素自己的生成块末尾发出（根元素不挂载）。
5. `Children` 只能用于 `Children` 内容模型；`Text`/`TextRuns`/`None` 的违规由分析器报 `EUI006`。
6. 每个模板元素只构造一次；块重建时按块协调器重新构造条目内容，不重新执行组件级 `Build()`。

#### 4.1.1 字段提升

只有**跨方法引用**才提升为字段；其余保持局部变量（spec/03 §3.2）。

| 情况 | 提升 | 原因 |
|---|---|---|
| 根级 `P={expr}`/文本写该元素 | `__el{N}` | `Render()` 调用 `U{N}()`，U 方法不能捕获局部变量 |
| 根级 `TextRuns` 动态 run | `__run{N}` | `U{N}()` 写 `run.Text` |
| 根级组件动态参数/组件 `@bind` 推送 | `__cmp{N}` | `U{N}()` 推送参数 |
| 根级 `@bind` 控件 | `__bind{N}` | `Render()` 调用 `Refresh()` |
| 根级 `@if`/`@foreach` | `__blk{N}` | `Render()` 调用 `Refresh()` |
| 块模板内的任何实体 | 否 | `R{N}` 是局部函数，可捕获局部变量 |
| 仅静态属性/事件/内容的元素 | 否 | 只在 Build 使用 |

- 提升后的元素不再声明 `var el{N}`；构造写成 `__el{N} = new ...;`。
- 字段按 N 升序声明在类顶部；`= null!;`。
- 一个元素即使被提升，其子元素仍可以是局部变量；父级挂载写 `parent.Children.Add(__el{N});`。
- 根元素若被提升，`Build()` 返回 `__el{N}`；否则返回 `el0`。

#### 4.1.2 根元素

- 根必须是元素/组件标签；根层文本或块 → `EUI006`；根数量不为 1 → `EUI012`。
- 根为控件时 `Build()` 返回 `el{N}`/`__el{N}`。
- 根为组件时返回 `cmp{N}.Root`；组件仍按 §4.12.1 登记与 `Mount()`。

### 4.2 字面量属性

```csharp
el0.Gap = 8d;
el0.FontSize = 24d;
el0.Label = "OK";
el0.IsEnabled = true;
```

- 字面量在编译期按目标类型转换（spec/01 §5）；生成 C# 字面量，不生成 `Convert`/`Parse`。
- `string`：C# 转义字面量；空串 `""`；不 Trim，保留原始空格/换行；
  控制字符用 `\uXXXX`，CR/LF 用 `\r`/`\n`，制表 `\t`，引号 `\"`，反斜杠 `\\`。
- `bool`：`true`/`false`。
- `double`：`"R"` 格式 + `d` 后缀（`8d`、`1.5d`、`1E-05d`）。
- `int`：十进制，负号直接写（`-1`）；不使用 `+` 前缀或千分位。
- `TimeSpan`（C3）：`new global::System.TimeSpan({ticks}L)`；编译期按不变文化换算，
  不得生成 `TimeSpan.Parse` 或依赖运行时区域性。
- 无转换器 → `EUI023`；格式/范围错 → `EUI003`；两者都不生成赋值。
- 字面量赋值与表达式赋值走**同一个**公开 setter；不得因字面量而用 `SetValue`。
- 映射到属性值起始行（§2.4）。

### 4.3 单向 `P={expr}`

#### 4.3.1 控件属性（组件级）

```csharp
private global::EclipseUI.Elements.TextBlock __el3 = null!;

protected override global::EclipseUI.Element Build()
{
    __el3 = new global::EclipseUI.Elements.TextBlock();
    U0();
    // ...
    return __el3;
}

protected override void Render()
{
    U0();
}

private void U0()
{
#line 12 "C:\Workspace\EclipseUI\samples\TodoList.ui"
    __el3.Width = Vm.Width;
#line hidden
}
```

规则：

1. `Build()` 在属性源码位置调用 `U{N}()` 完成首次求值；之后每次组件 `Render()` 再调用。
2. `U{N}` 方法体只含该绑定点的写入；不得有参数、返回值、泛型、`async`、局部函数。
3. 目标属性必须可写；不可写/缺 `{Name}Property` → `EUI019`/`EUI002`（C3），不生成赋值。
4. 表达式原样输出（§4.15）；不做依赖分析；所有 `{expr}` 都是动态的。
5. 写入走公开 CLR setter；不得 `SetValue`/`GetValue`。
6. 同一元素同一目标不得再被 `@bind`/文本内容写入（`EUI033`）。

#### 4.3.2 控件属性（块模板内）

```csharp
void R0()
{
#line 14 "C:\Workspace\EclipseUI\samples\TodoList.ui"
    el6.Width = Vm.Width;
#line hidden
}
R0();
scope5.AddRenderAction(R0);
```

规则：

1. `R{N}` 是 `SetContent`/`SetTemplate` lambda 内的局部函数；定义后立即调用一次，再登记为渲染动作。
2. 每次块刷新重跑 `R{N}()`；条目重建时重新构造并首次求值。
3. 表达式只出现一次（在 `R` 内）。
4. 不得把块内元素提升为字段；`R` 闭包捕获局部变量，条目/分支释放后闭包随之丢弃。

#### 4.3.3 组件参数

```csharp
private void U0()
{
#line 9 "C:\Workspace\EclipseUI\samples\TodoList.ui"
    var v = (global::System.String?)(OwnerName);
#line hidden
    if (!global::System.Collections.Generic.EqualityComparer<global::System.String?>.Default.Equals(
        __cmp0.Name, v))
    {
        __cmp0.Name = v;
        __cmp0.Invalidate();
    }
}
```

规则：

1. `var v` 的转换类型是**参数声明类型**（含可空注解、全限定）；转换保证数值字面量等按目标类型求值。
2. 相等保护使用 `EqualityComparer<T>.Default`；相等时不写、不失效。
3. 写参数后调用子组件 `Invalidate()`；不重建组件、不重跑子组件 `Build()`。
4. 组件参数必须是 `[UiParameter]`、public setter、非 `init`；否则 `EUI028`/`EUI040`。
5. `P={expr}` 不写 `[UiContent]`/`[UiSlot]` 参数 → `EUI021`（spec/03 §10.4）。
6. 组件级推送用 `U{N}`；块模板内用 `R{N}`（同上，但目标为 `cmp{N}`）。
7. 组件参数类型不得是 `ref struct`/指针/`Span<T>`（`EUI028`）；否则无法做 `EqualityComparer<T>`。

#### 4.3.4 目标为 `Text` 内容属性

`Text` 模型的目标是控件的字符串属性（如 `Button.Label`）；生成形态与 §4.3.1/§4.3.2 相同，
目标写成 `elN.{Target}`。若同一元素还有文本内容，分析器必须报 `EUI033`，生成器只保留一个目标写入。

#### 4.3.5 目标为控件 `TextRuns` 的字符串内容

标题/段落等 `TextRuns` 内容不经过 `P={expr}`；按 §4.8 生成 run 更新，不写宿主属性。

### 4.4 双向 `@bind-P={expr}`

#### 4.4.1 控件目标（组件级）

```csharp
__bind3 = global::EclipseUI.UiBinding.TwoWay(
    el2,
    global::EclipseUI.Elements.CheckBox.CheckedProperty,
    readTarget: () => el2.Checked,
    writeTarget: v => el2.Checked = v,
    readSource: () => ShowCount,
    writeSource: v => ShowCount = v,
    invalidate: Invalidate);
Track(__bind3);
```

规则：

1. 第三参必须是 `{OwnerType}.{Property}Property`，全限定；不得用 `elN.GetValue(...)`。
2. `readTarget`/`writeTarget` 使用**公开 CLR 属性** `elN.P`；不得 `SetValue/GetValue`。
3. `readSource`/`writeSource` 使用源表达式原样；源必须是可写左值（spec/03 §8.1）。
4. `invalidate: Invalidate` 是所在组件的实例方法组；块/条目内也是同一个组件，不是子组件。
5. 初始方向由 `UiBinding` 构造：**源 → 目标**；生成器不额外写一次初始赋值。
6. 绑定整体是一个 `IDisposable`，按 §5.3 登记；组件级用字段 `__bind{N}` + `Track`。
7. 同一目标不得同时有 `P={expr}` 或文本内容（`EUI033`）。
8. 目标非法 → `EUI040`；源非法 → `EUI037`；两者都不生成绑定。
9. `readSource`/`writeSource` 的表达式可以引用 item 与组件状态；没有 v3 的 `EUI042`。

#### 4.4.2 控件目标（块模板内）

```csharp
var bind14 = global::EclipseUI.UiBinding.TwoWay(
    el13,
    global::EclipseUI.Elements.CheckBox.CheckedProperty,
    readTarget: () => el13.Checked,
    writeTarget: v => el13.Checked = v,
    readSource: () => t.Done,
    writeSource: v => t.Done = v,
    invalidate: Invalidate);
scope11.AddDisposable(bind14);

void R1()
{
    bind14.Refresh();
}
R1();
scope11.AddRenderAction(R1);
```

规则：

1. 绑定局部变量加入条目/分支作用域：`scope{N}.AddDisposable(bind{N})`；不得 `Track`。
2. 同一作用域内必须生成一个 `R{N}` 调用 `Refresh()`；块刷新时源 → 目标。
3. 条目重建时旧绑定由旧作用域释放；同 Key 同 item 复用时保留原绑定与闭包。
4. 绑定自身不登记为"更新函数"以外的资源；不重复 Dispose。

#### 4.4.3 组件目标

组件参数双向不使用 `UiBinding`，而是"父推送 + 子 `PChanged` 回调"：

```csharp
#line 31 "C:\Workspace\EclipseUI\samples\ThemePicker.ui"
__cmp5.ColorChanged = v =>
{
    if (!global::System.Collections.Generic.EqualityComparer<global::System.String>.Default.Equals(
        SelectedColor, v))
    {
        SelectedColor = v;
        Invalidate();
    }
};
#line hidden
U0();
```

规则：

1. 目标参数 `P` 必须声明 `[UiParameter] public Action<T>? PChanged { get; set; }`；
   回调名固定 `{P}Changed`，类型 `Action<T>`（可空注解忽略），否则 `EUI040`。
   - **形状由扫描期判定**（D110）：恰好一个参数，且参数类型与 `P` 是同一类型（身份比较，可空标注忽略）。
     `Action`（零参）、`Action<string>` 配 `int` 等一律视为「没有回调」——分析器放行、`dotnet build` 才报
     CS1593/CS1503 的洞已堵上。
   - `P` 在组件上是公开成员却不是参数时同样 `EUI040`（消息说明「没有 `[UiParameter]`」），
     而不是 `EUI002`——名字存在，错的是它不支持绑定（spec/01 §4.5）。
2. 回调赋值是 build-once；位置在参数的源码位置，先赋回调，再调用推送 `U{N}()`。
3. 回调内的源写入受 `EqualityComparer<T>.Default` 保护；写后调用本组件 `Invalidate()`。
4. 推送 `U{N}` 与 §4.3.3 相同：相等保护 + 子组件 `Invalidate()`；`Render()` 再次调用。
5. 组件级用 `__cmp{N}` 字段；块模板内用 `cmp{N}` 局部 + `R{N}`。
6. 子组件内部修改参数后调用 `PChanged?.Invoke(next)` 并 `Invalidate()`；
   不得在子组件 `Render()` 中无条件调用（避免回环）。
7. 父未使用 `@bind` 时子参数修改只在子内部生效；父下次推送会覆盖。
8. 回调与推送的表达式可以 rooted at item/组件状态；无 `EUI042`。
9. 内容参数、槽参数、保留属性不得作为 `@bind` 目标 → `EUI040`。

#### 4.4.4 组件内容内的 `@bind`

组件内容（`[UiContent]`）是 build-once 静态展开，没有渲染动作作用域，**不得**包含 `@bind`；
分析器报 `EUI040`，生成器跳过该绑定。块模板（`@if`/`@foreach`）内的 `@bind` 合法，按 §4.4.1/§4.4.2 生成。

#### 4.4.5 旧语法 `P:bind`（已移除，K27）

`P:bind={expr}` 在 S6/K27 起**不生成任何代码**：解析器识别到 `:` 后缀属性名即报
`EUI046`（Error，「old binding syntax removed, use `@bind-P`」），分析器/生成器跳过该属性。
`EUI043` 停用并保持停用（§12、D1）。历史形态（S3–S5）曾是「生成与 `@bind-P` 完全相同的文本 + `EUI043` Warning」，
已随 K27 删除，不再有兼容期。

### 4.5 旧语法 `P:value`（已移除，K27）

`P:value={expr}` 同理：报 `EUI046`（Error，「use `P={expr}`」），不生成任何代码。
本节保留编号以维持契约编号稳定（`§4.6` 起不重排，K20）。

### 4.6 事件

#### 4.6.1 控件事件

```csharp
var h9 = (global::System.Action)(() => AddTask());
el8.AddHandler(global::EclipseUI.Elements.Button.ClickEvent, () => { h9(); Invalidate(); });
```

带参数的处理器：

```csharp
var hN = (global::System.Action<global::EclipseUI.RoutedEventArgs>)(
    e => OnSelectionChanged(e));
el.AddHandler(global::EclipseUI.Elements.ListBox.SelectionChangedEvent,
    e => { hN(e); Invalidate(); });
```

规则：

1. 事件名按 `On{EventName}` 解析为 `{Owner}.{EventName}Event`（`RoutedEvent` 静态字段），全限定。
2. 处理器表达式只出现一次，初始化 `h{N}`；`h{N}` 的类型是匹配到的处理器委托
   （`global::System.Action` 或 `global::System.Action<global::EclipseUI.RoutedEventArgs>`）。
3. `AddHandler` 的包装 lambda 只调用 `h{N}`，然后调用所属组件的 `Invalidate()`；
   包装不包含用户表达式。
4. 事件在属性阶段按源码顺序注册；不产生订阅、不写 `Track`、不参与 `Render()`。
5. 处理器抛异常时包装不执行 `Invalidate()`（异常向上传播）；`async void` 处理器在 `await` 后的
   状态变化必须由用户显式 `Invalidate()`（spec/03 §9.3）。
6. 字面量 / `:bind` / `@bind-` 作为事件值 → `EUI004`；不生成 `AddHandler`。
7. 处理器签名**可静态判定**时由分析器在 `.ui` 属性值报 `EUI024`（Error，✅ 2026-11-08/D82，
   按既有口径短路生成输出，与 `EUI004` 一致）；无法静态判定时静默，交回生成代码经 `#line`
   由 C# 报错（§2.4 规则 8 不重复报告）。判定口径：借 Roslyn 把处理器表达式放进 code-behind
   符号上下文，仅当「表达式自身语义干净 + 对目标委托的转换确定失败」时报 `EUI024`；符号未解析、
   重载失败、`dynamic`、表达式内部错误一律不报。控件事件目标委托复用
   `Emitter.EventHandlerType`（`internal static`）、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`，
   保证诊断与生成代码的类型判定一致；目标委托二选一（`Action` / `Action<RoutedEventArgs>`）仍按
   tokenizer 的表达式语法形态判定（2026-11-05，Roslyn 解析 lambda 参数个数，解析失败/非 lambda
   回落 `Action`）。实现载体 `TemplateGenerator.ValidateEventHandlerSignature` +
   `TemplateProbe.IsHandlerCompatible`（复用 `ProbeTree` 合成 partial 方法体），
   `EventHandlerSignatureTests` 16 例。

#### 4.6.2 组件事件（委托参数）

```csharp
var hN = (global::System.Action)(() => Close());
__cmp0.OnClose = () => { hN(); Invalidate(); };
```

带参数：

```csharp
var hN = (global::System.Action<global::System.Int32>)(x => Select(x));
__cmp0.OnSelect = a0 => { hN(a0); Invalidate(); };
```

规则：

1. 组件没有路由事件；委托类型参数就是事件（design/14 §14.6.2）。
2. 按参数名赋值，不做 `On*` 前缀推断；必须表达式（字面量 → `EUI004`）。
3. 包装 lambda 参数固定 `a0..aN`，类型由目标委托推断；调用 `h{N}` 后失效**父组件**（提供委托的一方）。
4. 不生成 `AddHandler`；不产生订阅；build-once。
5. 组件重建时事件重新赋值；同 Key 同 item 复用时保留原委托。
6. 委托必须返回 `void`，参数不得含 `ref`/`out`/`in`；否则 `EUI028`。

### 4.7 文本：`Text` 模型

#### 4.7.1 全静态

```csharp
el0.Label = "共 0 项";
```

- 多个静态段先合并为一个字符串（词法阶段注释删除后的相邻段合并）。
- 目标属性由内容模型元数据给出；不得硬编码 `Label`。
- 静态赋值只在 Build 执行一次，不进入 `Render()`。

#### 4.7.2 含表达式

```csharp
private void U0()
{
#line 12 "C:\Workspace\EclipseUI\samples\TodoList.ui"
    __el3.Label = $"共 {(Tasks.Count)} 项";
#line hidden
}
```

规则：

1. 只要文本含至少一个表达式，就生成**一个完整插值串**的 updater；静态段也进入插值串。
2. 每个表达式用 `{(expr)}` 包裹，避免 `:` 被解析为格式说明符；不支持格式说明符（spec/01 §2.6）。
3. 静态文本中的 `{`/`}` 写成 `{{`/`}}`；`\`/`"` 按 C# 字符串转义。
4. 同一文本节点的多个表达式共享一个 `U{N}`/`R{N}`；表达式只出现一次。
5. 组件级用 `U{N}` + `__el{N}` 字段；块模板内用 `R{N}` + 局部元素。
6. `Text` 模型只接受文本段；元素/块 → `EUI006`；显式属性与文本同时写同一目标 → `EUI033`。
7. 文本更新不重建元素；只写目标字符串属性。

### 4.8 文本：`TextRuns` 模型

#### 4.8.1 静态段

```csharp
((global::EclipseUI.ITextContentHost)el0).AddRun("Hi ");
```

- 每个连续静态段一个 run；不创建局部变量（无需更新）。
- 相邻静态段已合并；空静态段不生成。

#### 4.8.2 动态段

```csharp
var run1 = ((global::EclipseUI.ITextContentHost)el0).AddRun("");
void R0()
{
#line 12 "C:\Workspace\EclipseUI\samples\Greeting.ui"
    run1.Text = $"{(Name)}";
#line hidden
}
R0();
scopeN.AddRenderAction(R0);
```

规则：

1. 动态 run 以空串创建，随后立即调用 updater 写入首次值；不得在 `AddRun` 实参里求值
   （否则表达式出现两次）。
2. 组件级动态 run 提升为 `__run{N}` 字段，`U{N}` 写 `__run{N}.Text`；块模板内为 `run{N}` 局部。
3. 只重设动态 run 的 `Text`；静态 run 不重算。
4. `AddRun` 通过 `ITextContentHost` 显式接口调用；返回 `TextRun`。
5. `TextRuns` 的宿主必须实现 `ITextContentHost`；否则 `EUI021`（design/14）。
6. 不得对 `TextBlock` 直接操作 `Children` 添加 run；内容模型决定唯一路径。

### 4.9 `@if`

#### 4.9.1 组件级

```csharp
__blk4 = new global::EclipseUI.ConditionalBlock();
el0.Children.Add(__blk4);
__blk4.SetCondition(() => ShowCount);
__blk4.SetContent(scope5 =>
{
    var el6 = new global::EclipseUI.Elements.TextBlock();
    ((global::EclipseUI.ITextContentHost)el6).AddRun("内容");
    scope5.AddRoot(el6);
});
Track(__blk4);
__blk4.Refresh();
```

规则：

1. **顺序固定**：`new` → 挂到父级 → `SetCondition` → `SetContent` → `Track`/作用域登记 → `Refresh()`。
2. `SetCondition` 只保存条件，不求值；条件表达式只出现一次。
3. `SetContent` 只保存模板，不执行；模板接收块创建的 `BlockScope` 参数 `scope{N}`。
4. 首次求值由显式 `Refresh()` 完成；`Track` 在 `Refresh` 之前，内容构造失败时块可被释放。
5. `Refresh()` 在条件为真时重跑内容 `RenderActions`；条件翻转时释放旧作用域、重建内容。
6. 空分支合法：不生成 `SetContent` 调用；块保持空内容。
7. 内容根按源码顺序 `scope{N}.AddRoot(...)`；嵌套块也是根。
8. 内容内的绑定/子组件/子块用 `scope{N}.AddDisposable(...)` 登记；块卸载时释放。
9. 嵌套 `@if`：内层块是外层内容作用域的资源（规则 8），内层刷新由外层 `R` 调用。
10. 内容中的动态表达式用 `R{N}` 承载；内容重建时重新构造并首次求值。

#### 4.9.2 块模板内（嵌套）

```csharp
__blk4.SetContent(scope5 =>
{
    var blk6 = new global::EclipseUI.ConditionalBlock();
    blk6.SetCondition(() => item.Enabled);
    blk6.SetContent(scope7 =>
    {
        // ...
    });
    scope5.AddDisposable(blk6);

    void R1()
    {
        blk6.Refresh();
    }
    R1();
    scope5.AddRenderAction(R1);
    scope5.AddRoot(blk6);
});
```

规则：

1. 嵌套块**不**提升为字段；它是外层内容作用域的局部变量 + `AddDisposable`。
2. 嵌套块的首次 `Refresh()` 在当前 `R` 内调用；外层刷新时再次调用。
3. 顺序：`new` → `SetCondition` → `SetContent` → `AddDisposable` → 定义 `R` → 调用 `R` →
   `AddRenderAction` → `AddRoot`。
4. 嵌套块的内容根由它自己的 `BlockScope` 管理；不得添加到外层 scope。
5. 同一作用域内 `R{N}` 与绑定/文本的 updater 共享 `fnCounter`。

#### 4.9.3 多分支（`else if` / `else`，✅ S6）

`@if` 带 `else if` 或 `else` 时，`SetCondition`/`SetContent` 换成 `AddBranch`/`SetDefault`
（[spec/01](./01-template-syntax.md) §4.10、K23 ③）：

```csharp
__blk4 = new global::EclipseUI.ConditionalBlock();
el0.Children.Add(__blk4);
__blk4.AddBranch(() => Count > 0, scope5 =>
{
    var el6 = new global::EclipseUI.Elements.TextBlock();
    ((global::EclipseUI.ITextContentHost)el6).AddRun("有");
    scope5.AddRoot(el6);
});
__blk4.AddBranch(() => Count == 0, scope7 =>
{
    // ...
});
__blk4.SetDefault(scope8 =>
{
    // ...
});
Track(__blk4);
__blk4.Refresh();
```

规则：

1. **无 `else`/`else if` 的 `@if` 仍走 §4.9.1 形态**（v2 形态冻结，不因多分支能力而无谓改动）。
2. 分支按源码顺序登记；`Refresh()` 顺序求值，**首个为真者胜**，其后的条件不再求值，`else` 在所有分支为假时激活。
3. `AddBranch` 行映射到该分支条件 span；`SetDefault` 行不含用户表达式，不映射（§2.4）。
4. 分支条件各出现一次；`Refresh()` 不变时只重跑当前分支的 `RenderActions`，切换时释放旧 `BlockScope` 并重建。
5. 空分支仍生成 lambda 空体（分支完整性），等价于"该分支激活且内容为空"。
6. 嵌套形态同 §4.9.2 的三条顺序规则，只是把 `SetCondition`/`SetContent` 两行并成一条 `AddBranch`。

### 4.10 `@foreach`

#### 4.10.1 组件级

```csharp
__blk10 = new global::EclipseUI.ForEachBlock<global::Examples.TodoItem>();
el0.Children.Add(__blk10);
__blk10.SetItems(() => Tasks);
__blk10.SetKeySelector((global::Examples.TodoItem t) => (object?)(t.Id));
__blk10.SetTemplate((global::Examples.TodoItem t, global::EclipseUI.BlockScope scope11) =>
{
    var el12 = new global::EclipseUI.Elements.StackPanel();
    // ... 条目内容 ...
    scope11.AddRoot(el12);
});
Track(__blk10);
__blk10.Refresh();
```

规则：

1. **顺序固定**：`new` → 挂到父级 → `SetItems` → `SetKeySelector`（有 `Key` 时）→ `SetTemplate` →
   `Track`/作用域登记 → `Refresh()`。
2. `SetItems` 只保存集合 getter；`SetKeySelector` 只保存选择器；`SetTemplate` 只保存模板。
   三者都不自动刷新。
3. 首次枚举由显式 `Refresh()` 完成；`Track` 在 `Refresh` 之前，模板失败时可释放。
4. `SetItems` 的 lambda 返回 `IEnumerable<T>?`；`null` 视为空集合（spec/03 §7.1）。
5. `SetKeySelector` 的 item 参数与 `SetTemplate` 的 item 参数类型必须是集合元素类型 `T`（含可空注解），
   名称使用 `.ui` 中声明的 item 变量名。
6. `SetKeySelector` 的返回转换为 `(object?)`；无 `Key` 时**不生成** `SetKeySelector`，块以 item 自身为键。
7. `SetTemplate` 的 `BlockScope` 参数名 `scope{N}`；模板必须至少有一个 `AddRoot`（空条目由分析器拦截）。
8. 条目内容中的绑定/子组件/子块用 `scope{N}.AddDisposable` 登记；组件实例用 `Adopt` + `AddDisposable`。
9. `Refresh()` 负责 Keyed 对齐、复用/重建/释放与 `Children` 顺序；生成器不实现 diff。
10. 条目顺序按本次枚举顺序；同 Key 同 item 复用，同 Key 不同 item 重建（spec/03 §7.3）。

#### 4.10.2 块模板内（嵌套）

```csharp
__blk10.SetTemplate((global::Examples.TodoItem t, global::EclipseUI.BlockScope scope11) =>
{
    var blk12 = new global::EclipseUI.ForEachBlock<global::Examples.Tag>();
    blk12.SetItems(() => t.Tags);
    blk12.SetKeySelector((global::Examples.Tag tag) => (object?)(tag.Id));
    blk12.SetTemplate((global::Examples.Tag tag, global::EclipseUI.BlockScope scope13) =>
    {
        var el14 = new global::EclipseUI.Elements.TextBlock();
        ((global::EclipseUI.ITextContentHost)el14).AddRun("标签");
        scope13.AddRoot(el14);
    });
    scope11.AddDisposable(blk12);

    void R1()
    {
        blk12.Refresh();
    }
    R1();
    scope11.AddRenderAction(R1);
    scope11.AddRoot(blk12);
});
```

规则同 §4.9.2：嵌套块是条目作用域的局部变量 + `AddDisposable`，首次刷新由 `R` 调用，
内容根归内层 `BlockScope`。内层集合可以引用外层 item 成员与组件状态；无 `EUI014`/`EUI042`。

### 4.11 item 模板与 `BlockScope`

- item 模板是 `SetTemplate` 的 lambda；参数为 `(T {itemVar}, BlockScope scope{N})`。
- 模板由块在**每次需要新建条目**时调用；块创建 `BlockScope` 并传入，模板只能通过以下方法登记：
  - `scope{N}.AddRoot(Element)`：条目根（至少一个；多个按源码顺序）；
  - `scope{N}.AddRenderAction(Action)`：`R{N}` 渲染动作（在首次调用之后登记）；
  - `scope{N}.AddDisposable(IDisposable)`：绑定、子块、子组件。
- 模板失败时（首次求值抛异常），块释放该 `BlockScope` 已登记的部分；生成代码不捕获。
- 内容顺序：元素构造 → 属性（含 `R` 定义/调用/登记）→ 子内容 → `AddRoot`。
- 绑定必须在首次 `R` 调用之前构造并 `AddDisposable`，这样首次 `Refresh()` 能读到最新源。
- 事件处理器在模板构造时生成并赋值；不登记为 disposable。
- 同 Key 同 item 复用：块保留元素、绑定、事件、闭包，重跑 `RenderActions`。
- 同 Key 不同 item：块释放旧 `BlockScope`（ Dispose 其 `Disposables`），调用模板重建新闭包；
  旧闭包不得复用（R8）。
- 模板内不得出现 `static` 捕获或把 item 存到字段；item 只存在于 lambda 参数与闭包。

### 4.12 组件

#### 4.12.1 顺序与作用域

```csharp
var cmp0 = new global::App.UserCard();
Track(cmp0);                          // 根级；块内为 Adopt + scopeN.AddDisposable
cmp0.Title = "Hi";                    // 静态参数，源码顺序
U0();                                 // 动态参数推送（源码位置）
cmp0.Body = content5;                 // 组件内容（若有）
cmp0.Mount();
cmp0.ApplyClasses(new global::System.String[] { "wide" });   // 元素级属性（§4.12.6）
global::EclipseUI.Elements.Grid.SetRow(cmp0.Root, 1);
parentN.Children.Add(cmp0.Root);
```

规则：

1. **顺序固定**：`new` → 作用域登记 → 参数（源码顺序，静态/动态/事件混合）→ 组件内容 →
   `Mount()` → 元素级属性（Class/Style/附加属性，源码顺序，作用于 `cmpN.Root`，§4.12.6）→
   挂到父级（根组件返回 `cmpN.Root`）。
2. 作用域登记在参数之前：
   - 组件级：`Track(cmp{N})`；
   - 块模板内：`Adopt(cmp{N}); scope{N}.AddDisposable(cmp{N});`
   - 组件内容内：`receiver.TrackContent(cmp{N});`
3. `Track(cmp)` 等价于 `Adopt` + 登记释放；块/内容内必须拆开，以便随条目/接收方释放。
4. 参数/回调必须在 `Mount()` 前完成；子组件首次 `Build()` 读取参数。
5. `cmp.Root` 在 `Mount()` 后才非空；生成器不得提前访问——元素级属性因此排在 `Mount()` 之后。
6. 组件为文档根时：登记 → 参数 → 内容 → `Mount()` → 元素级属性 → `return cmp{N}.Root;`，不挂父级。
7. 组件参数没有 diff；父渲染只推送动态参数并失效子组件，不重建子组件。

#### 4.12.2 参数与事件

- 静态字面量参数按 §4.2；动态参数按 §4.3.3；组件 `@bind` 按 §4.4.3；组件事件按 §4.6.2。
- 参数按 `.ui` 源码顺序赋值；同一参数重复 → `EUI025`；未知参数 → `EUI002`。
- 委托参数是事件，不是属性；按参数名赋值，不做 `On*` 推断。
- 必填参数缺失 → `EUI027`；`required`/`init`/无 setter → `EUI028`。

#### 4.12.3 组件内容（`[UiContent]`）

组件内容是 **build-once 静态展开**，不是保留树的动态部分：

```csharp
var content5 = new global::System.Collections.Generic.List<global::EclipseUI.Element>();
var el6 = new global::EclipseUI.Elements.TextBlock();
((global::EclipseUI.ITextContentHost)el6).AddRun("标题");
content5.Add(el6);
var cmp7 = new global::App.Badge();
cmp0.TrackContent(cmp7);
cmp7.Text = "新";
cmp7.Mount();
content5.Add(cmp7.Root);
cmp0.Header = content5;
```

规则：

1. 内容参数类型只接受 `IReadOnlyList<Element>`（多根）或 `Element?`（单根）；其他类型 → `EUI021`。
2. 多根参数：分配 `List<Element>`，按源码顺序 `Add`；最后赋给参数。
3. 单根参数：同样累加到列表，赋值为 `content{N}.Count == 0 ? null : content{N}[0]`；
   静态可证明多根 → `EUI021`。
4. 内容中的元素按普通元素构造；不需要 `Track`/作用域登记（元素由父元素树持有）。
5. 内容中的子组件：`receiver.TrackContent(cmp{N})` 登记到**接收方**，再 `Mount()`，根加入列表。
   不得 `Track` 到模板组件，否则内容子组件会随模板组件释放而不是随接收方释放。
6. 内容中的事件按 §4.6 生成；包装 `Invalidate()` 仍失效**模板父组件**（内容构建者），
   因为处理器表达式属于父模板。
7. 内容中的 `@if`/`@foreach` 生成普通 C# `if`/`foreach`，在 Build 时按当前值展开一次；
   不是 `ConditionalBlock`/`ForEachBlock`。父组件后续渲染不重算内容。
8. 内容中的 `{expr}`/`P={expr}` 是 Build 时快照；不生成 `U`/`R`。
9. 内容中的 `@bind` → `EUI040`；`Key` → `EUI010`；文本节点 → `EUI006`。
10. 内容参数不得作为 `@bind` 目标（`EUI040`）；用 `P={expr}` 写内容参数 → `EUI021`。
11. 父状态变化需要内容更新时，把组件标签放进父模板的 `@if`/`@foreach`，由块重建；
    或让内容自身是独立组件。
12. 内容构建失败时，`receiver` 已在父作用域登记，接收方负责释放已 `TrackContent` 的子组件。

#### 4.12.4 组件在块/内容中的根

- 块模板内的组件根通过 `scope{N}.AddRoot(cmp{N}.Root)` 加入；组件实例同时 `AddDisposable`。
- 组件内容内的组件根通过 `content{N}.Add(cmp{N}.Root)` 加入；实例 `receiver.TrackContent`。
- 组件不能直接作为 `Children` 的元素；生成器只添加 `cmpN.Root`。

#### 4.12.5 具名插槽（`[UiSlot]`，✅ C6）

具名插槽镜像 §4.12.3 的内容规则，分两侧：

**消费侧**（父模板中 `<Slot Name="...">...</Slot>`，design/14 §14.6.3）：

```csharp
var slot5 = new global::System.Collections.Generic.List<global::EclipseUI.Element>();
var el6 = new global::EclipseUI.Elements.TextBlock();
((global::EclipseUI.ITextContentHost)el6).AddRun("头部");
slot5.Add(el6);
var cmp0 = new global::App.ProbeCard();
cmp0.Header = slot5.Count == 0 ? null : slot5[0];   // 单根插槽
cmp0.Rows = slot7;                                  // 多根插槽（整表赋值）
cmp0.Mount();
```

规则（编号与 §4.12.3 对齐的部分同口径）：

1. 填充生成完全镜像 §4.12.3 规则 2–9：`List<Element>` 累加、内容内子组件
   `receiver.TrackContent` 归**接收方**、`@if`/`@foreach` 展开为普通 C# 语句、
   `{expr}` 为 Build 快照、`@bind` → `EUI040`、`Key` → `EUI010`、裸文本 → `EUI006`。
2. 单根插槽（`Element?`）赋值 `slot{N}.Count == 0 ? null : slot{N}[0]`；静态多根填充已由
   分析器拒绝（`EUI022`，但解析仍取首根——与 §4.12.3 规则 3 同口径）。
3. 多根插槽（`IReadOnlyList<Element>`）整表赋值（同一引用，不拷贝）；**允许同一父下多次
   `<Slot Name="同名">` 填充**，按源码顺序全部拼进**同一条列表**，只赋值一次。
4. 赋值顺序在参数之后、默认内容之后、`Mount()` 之前（紧贴 §4.12.1 规则 1 的固定顺序；
   子组件首次 `Build()` 在 `Mount()` 内读取插槽属性）。无任何填充时**不生成任何插槽代码**。
5. 填充只能出现在组件标签直属子级（控件、块、文档根、自身 `<ContentSlot>` 内均 `EUI022`）；
   `<Slot>` 属性只接受字面量 `Name`，槽名解析父组件 def 的 `Slots`（未知含建议 → `EUI022`）。

**宿主侧**（组件自身模板中 `<ContentSlot Name="..." />`）：

不引入新的运行时宿主元素——拼接发生在组件自身 `Build()` 内，按 build-once 语义
`<ContentSlot>` 所在位置内联展开：

```csharp
// Single：属性非空则加入父容器
if (Header is { } slot8)
{
    parent.Children.Add(slot8);
}
// Multiple：读取一次属性，逐项加入
foreach (var slot8 in Rows)
{
    parent.Children.Add(slot8);
}
```

规则：

6. `<ContentSlot>` 只能出现在元素容器（`Children` 模型）子级；**不进 `@if`/`@foreach` 块**
   （块模板会随刷新重复重建，拼接结果是 build-once 快照，重复拼装语义不成立）、
   不可作文档根、`Text`/`TextRuns`/`Label` 模型内非法；以上均 `EUI022`。
7. 不出现在使用其他组件的填充里（`<ContentSlot>` 属于组件自身模板）→ `EUI022`。
8. 自身插槽按 code-behind 类（含基类链）的 `[UiSlot]` 属性解析（生成器传符号权威表；
   静态前端按目录显示名与 `.ui` 文件名的短名匹配，偏差 D75）；未知插槽 → `EUI022`。
9. 单根拼接对 null 属性**跳过**（无占位元素）；多根拼接对空列表**为零次循环**（不产生空容器）。
10. 属性只接受字面量 `Name`；带内容、多余属性、同名重复放置 → `EUI022`
    （重复放置违反「一个元素只有一个父」：拼接会把同一实例加进两个容器）。

#### 4.12.6 组件标签上的元素级属性（`Class` / `Style` / 附加属性）

组件不是 `Element`（spec/03 §5.3：`Component` 只持有 `Element Root`），所以标签上的**元素级**
属性只能落到 `cmpN.Root` 上；写在组件实例上会得到 CS1061/CS1503（分析器放行、生成代码编译不过）。
发射点在 `Mount()` 之后、挂父级之前（§4.12.1 规则 1/5）。

| 写法 | 生成 | 语义 |
|---|---|---|
| `Class="a b"` / `Class={expr}` | `cmpN.ApplyClasses(new [] { "a", "b" });` | 与组件自身在根上声明的类**合并**：自身类在前、外部类在后 |
| `Style="P: v"` | `cmpN.Root.SetValue({P}Property, v);` | local 层写根元素（压过类层，与控件同口径） |
| `P.Attached="v"` / `{expr}` | `global::Ns.Owner.SetXxx(cmpN.Root, v);` | 附加属性落在根元素上（`<Card Grid.Row="1"/>` = 卡片占据第 1 行） |
| `Ref={ident}` | `{ident} = cmpN;` | 与上面三类不同：`Ref` 拿的是**组件实例**（§4.14） |

- `ApplyClasses` 的合并口径：组件自身类**只在第一次调用时快照**，之后每次调用整体替换「外部类」
  部分，因此根级 `Class={expr}` 每轮 `Render` 重算不会累积；传空集 = 撤销外部类、只留自身类。
  外部类排在末尾与「`Class` 在控件上后写者胜」的应用序一致（同属性冲突时外部覆盖内部）。
- 塌缩注入（E4）命中组件标签且自身没写 `Class` 时走同一个入口（`ApplyClasses`），派生类同样进根。
- 附加属性只接受字面量与表达式；`@bind-` 附加属性仍是 `EUI040`。
- 组件的**参数**仍走 §4.12.2（`[UiParameter]`）；`Class`/`Style`/附加属性不进参数表，与控件同名
  属性的语义完全一致——区别只在目标节点是根元素。


### 4.13 `Key`

- `Key={expr}` 只出现在 `@foreach` item 模板的**第一个元素根**上；其他位置 → `EUI010`。
- `Key` 不生成属性赋值；它只进入 `SetKeySelector`（§4.10）。
- `Key` 可引用 item 成员、组件状态、方法调用；没有 v3 的可观察性限制；稳定性由作者负责。
- 组件内容中的 `Key` → `EUI010`（内容不参与 Keyed 复用）。
- 无 `Key` 时 key = item 自身；生成器不生成 `SetKeySelector`。

### 4.14 保留语法

| 写法 | 语义 | 状态 | 诊断 |
|---|---|---|---|
| `Ref={ident}` | Build 时把元素/组件赋给 code-behind 字段 | ✅ C5 | `EUI029`（保留） |
| `Class`/`Style` | 样式/主题 | G4 | `EUI031` |
| `<Slot Name="...">` / `<ContentSlot>` | 具名插槽 | ✅ C6 | `EUI022`（生成形态见 §4.12.5） |
| 附加属性 `P.Attached` | 附加属性 | ✅ G4（2026-11-09，见 §4.19） | 已注册走正常解析；未注册点号名报 `EUI002`（不再占 `EUI031` 保留位） |
| 泛型标签 `<C<T>>` | 泛型组件/控件 | 暂缓（K19，不排期） | `EUI030`（保留位；当前写法落 `EUI100`） |

- 保留语法在实现前**不生成任何代码**；分析器报对应诊断，生成器跳过该节点/属性。
- 保留语法不得退化为 `SetValue`/字符串查找；实现后按 G3/G4 单独修订本文。
- **K23 ③（✅ S6 已实现（2026-11-08），D87）**：`@switch` 从 `EUI008` 保留位转为目标语法，`EUI008` 停用且保持停用；
  生成侧按多分支 `ConditionalBlock` / `SwitchBlock<T>` 处理（草案见 design/16 附录 B），仍不做元素 diff。

**`Ref={ident}`（✅ C5）**：`ident` 必须是**简单标识符**（code-behind 的公开/私有实例字段名）；
生成形态为准 Build 语句：

```csharp
// 控件：元素实例（v 是该元素的局部/提升变量）
savePanel = el3;
// 组件：组件实例（v 是 cmpN；design/14 §14.6.4——Ref 绑定组件实例，不是 Root）
card = cmp0;
```

规则：

1. 无 `U{N}`/`R{N}`、不进 `Render()`：Ref 是 Build 一次的赋值，无重算语义（spec/01 §4.3 表）。
   块内条目重建时该元素重新构造，字段随构造赋值 → 始终指向最新实例。
2. 赋值在元素自己的属性循环内按**源码顺序**发出（组件在作用域登记之后），行按 `#line`
   映射到 `Ref` 的属性值位置。
3. 不参与元素字段提升：Ref 目标是 code-behind 实例字段，生成赋值对 `this` 成员可见，
   根级/块内都不需要提升 `__el{N}`。
4. 诊断分层（`EUI029`，位置 = `Ref` 属性值 span）：
   - **语法级（分析器）**：值不是 `{...}` 表达式、或表达式不是简单标识符（成员访问/调用/字面量）；
   - **语义级（生成器探针）**：字段缺失（CS0103）、类型不兼容（CS0029）、只读（CS0191，
     生成代码赋值在实例方法内不做构造期赋值）、静态/const 字段；探针把 `{target} = __refValue`
     （`__refValue` 声明为元素/组件类型）放进与 code-behind 同上下文的合成方法体，由 C# 回答
     （与 §4.4 绑定源校验同法）。目标命中 `@foreach` item 名直接拒绝（真实代码里 item 是迭代变量）。
5. `Ref` 仍受保留名纪律（§4.15 之外）：`@bind-Ref`/`Ref:bind` → `EUI040`；同一元素重复 `Ref` →
   `EUI025`；控件/组件自身把 `Ref` 暴露为普通属性是元数据问题（`EUI026`），使用点仍按保留语法解析。

### 4.15 表达式输出

- 不重写用户表达式；除插值 `$"..."`、必要括号、目标类型转换外，表达式 token 原样输出
  （spec/03 §4.4）。
- 不引入 `using`；简单类型名按生成类所在命名空间解析；用户表达式中的类型名按原命名空间解析。
- `@if` 条件、集合、Key 不额外加括号；lambda 目标类型转换由 C# 完成。
- 组件推送的 `(T)(expr)` 是唯一生成的目标类型转换；文本插值 `{(expr)}` 的括号是唯一生成的分组。
- 表达式中的 `?.`/`??`/方法调用/索引器全部合法（v4 无依赖分析）；
  但 `@bind` 源仍受 spec/03 §8.1 的可写左值限制。
- 表达式不得包含 `await`（模板是同步 Build）；由 C# 报错。

### 4.16 空内容与错误回退

- `@if` 内容为空：不生成 `SetContent`，只生成条件/登记/首次 `Refresh`。
- `@foreach` 条目模板为空：分析器报 `EUI006`；不生成模板。
- 组件内容为空：不分配 `content{N}`，不赋内容参数。
- 文档根数量不为 1 → `EUI012`；生成器输出最小 `Build()`（§2.6）。
- 解析失败时生成器可以跳过节点；不得抛出、不得生成部分初始化后的新诊断。

### 4.17 `@switch`（✅ S6）

```csharp
__blk4 = new global::EclipseUI.SwitchBlock<global::System.Int32>();
el0.Children.Add(__blk4);
__blk4.SetSelector(() => Mode);
__blk4.AddCase(1, scope5 =>
{
    // case 1 内容
});
__blk4.AddCase(2, scope6 =>
{
    // case 2 内容
});
__blk4.SetDefault(scope7 =>
{
    // default 内容
});
Track(__blk4);
__blk4.Refresh();
```

规则：

1. **`T` 由生成器探针推断**（selector 表达式类型）；推断失败退化为
   `global::EclipseUI.SwitchBlock<global::System.Object>`，类型错误交 C# 经 `#line` 报出。
2. `SetSelector` 行映射到 selector 表达式 span；`AddCase` 行映射到该 section 的**标签 span**；
   `SetDefault` 行不含用户表达式，不映射（§2.4）。
3. `AddCase` 的 key 是标签**原文**（编译期常量表达式，spec/01 §4.16）；Build 时求值一次，生成器不做常量折叠。
4. section 按源码顺序登记；`Refresh()` 求值 selector，按 `EqualityComparer<T>.Default` 顺序比较，
   **首个相等者胜**，无贯穿（每个 section 是独立分支）。
5. 无匹配且无 `default` → 块内容为空；**不生成** `SetDefault`。`default` 至多一个（重复由分析器拦截）。
6. `break;` 不产生任何生成物——它只是分节的语法边界；空 section 必须在源码里显式写 `break;`（K30）。
7. 顺序：`new` → 挂父 → `SetSelector` → `AddCase`*/`SetDefault` → `Track` → `Refresh()`；
   嵌套形态同 §4.9.2（局部变量 + `AddDisposable` + `R{N}`，`Refresh()` 由外层 `R` 调用）。
8. `SwitchBlock<T>` 与 `ConditionalBlock` 同为布局透明协调器（spec/03 §6.2）。

### 4.18 `@using Alias = Ns.Type`（K31）

模板：

```xml
@using EclipseUI.Elements
@using Btn = ThirdParty.Controls.Button

<StackPanel><Btn /></StackPanel>
```

生成：

```csharp
// <auto-generated> … </auto-generated>
#nullable enable
using EclipseUI.Elements;
using Btn = global::ThirdParty.Controls.Button;
namespace {Namespace};

public partial class {Class} : global::EclipseUI.Component
{
    protected override global::EclipseUI.Element Build()
    {
        var el0 = new global::EclipseUI.Elements.StackPanel();
        var el1 = new global::ThirdParty.Controls.Button();
        el0.Children.Add(el1);
        return el0;
    }
}
```

规则：

1. **标签解析在分析期完成**：`<Btn/>` 由别名表直接命中目标类型，`ResolvedDef` 即该类型的目录项；
   发射端照常打印 `ResolvedDef.ClrTypeDisplayName`，**不做名字重写**。
2. 别名**优先于目录 tag**：先查别名表，未命中才走「`@using` 命名空间过滤 + 目录」的既有路径。
   因此别名可以覆盖内置同名 tag（`@using Button = ThirdParty.Controls.Button`）。
3. 别名**绕过命名空间可见性**：目标命名空间不必出现在 `@using` 里——这正是解决 `EUI016` 的手段。
4. 别名**不进** `CollectImports`（只收命名空间形态）。混进去会让 `NamespaceVisible` 把别名当命名空间，
   导致全部候选静默不可见、每个标签变 `EUI001`。
5. 探针树（`@code` 成员合成树、表达式探针）必须同样写入别名行——否则别名类型在
   `EUI024`（事件签名）/ `EUI047`（case 常量）/ `foreach` 项类型推断里退化为「不可判定」。
   `CollectUsingLines` 是生成文件头与两棵探针树的唯一来源。
6. 目标不是目录里的已知元素/组件 → `EUI048`（Error），整份生成代码缺失（与其它 Error 口径一致）。
7. 别名行按模板顺序去重；命名空间导入与别名**各自**去重，互不影响。

### 4.19 附加属性 `Owner.Prop`（✅ G4 首批，2026-11-09）

- 声明（design/14 §14.5.5）：宿主类提供 `public static readonly AttachedProperty<T> XxxProperty` 与
  `public static T GetXxx(Element)` / `public static void SetXxx(Element, T)`；扫描器要求配对存在且值类型一致，
  否则报 `EUI019` 家族问题、不进目录。目录项 `UiAttachedPropDef(OwnerTag, Name, OwnerClrName, SetterMethod, ClrTypeDisplayName, TypeShape)`。
- 字面量：按 `TypeShape` 走 `UiLiterals` 同一管道（无转换器 → `EUI023`，格式错 → `EUI003`），生成**直调静态 setter**：

  ```csharp
  global::EclipseUI.Elements.Grid.SetRow(el1, 1);
  ```

- 表达式 `Grid.Row={expr}`：动态形态不是赋值左值而是调用语句——根级提升为 `U{N}`、块内 `R{N}`，方法体即
  `global::Ns.Owner.SetXxx(elN, expr);`（`#line` 列映射对准 `{expr}`）。内容上下文仍是 build-once 快照（§4.12.3 规则 8）。
- 组件标签上的附加属性作用于**根元素**（`global::Ns.Owner.SetXxx(cmpN.Root, …)`）——组件实例不是
  `Element`（spec/03 §5.3），`Grid.Row` 的含义即「组件占据的那个格子」；发射点在 `Mount()` 之后
  按源码顺序执行（§4.12.6）。
- 值存储由 `AttachedProperty<T>` 的 `ConditionalWeakTable` 弱表承担：元素卸载随 GC 回收，Unmount 无需清理（K33 口径）；
  读取 `Owner.GetXxx(el)` 无本地值时回落声明时的默认值。
- `@bind-` 附加属性不支持（`EUI040`，写入来自模板一次性赋值）；未知点号名报 `EUI002` 未知附加属性（带全目录最近名建议）。

## 5. 顺序、资源与释放

### 5.1 Build 语句顺序（冻结）

| 构造 | 顺序 |
|---|---|
| 元素/控件 | `new` → 属性（源码顺序，含静态赋值/`U`/`R`/绑定/事件）→ 内容 → `parent.Children.Add` |
| 组件 | `new` → 作用域登记 → 参数（源码顺序）→ 内容 → `Mount` → 元素级属性（Class/Style/附加属性，作用于 `cmp.Root`）→ `parent.Children.Add(cmp.Root)` |
| `@if` | `new` → `parent.Children.Add` → `SetCondition` → `SetContent` → 作用域登记 → `Refresh()`（多分支见 §4.9.3） |
| `@switch` | `new` → `parent.Children.Add` → `SetSelector` → `AddCase`*/`SetDefault` → 作用域登记 → `Refresh()` |
| `@foreach` | `new` → `parent.Children.Add` → `SetItems` → `SetKeySelector` → `SetTemplate` → 作用域登记 → `Refresh()` |
| item 模板 | 元素 → 属性/`R` → 子内容 → `AddRoot` |
| 组件内容 | 列表分配 → 子元素/子组件 → 参数赋值 |
| 根组件 | 登记 → 参数 → 内容 → `Mount` → `return cmp.Root` |

- 先挂父级后设置块：块的 `Children` 独立于父级；父级挂载不依赖内容。
- 组件先登记后参数：参数 setter/内容构建失败时，调用方仍持有并可释放组件。
- `Track` 必须紧跟在资源构造之后、任何可能抛异常的用户代码之前。

### 5.2 首次求值与重算

- `Build()` 是第 0 次渲染：所有 `U{N}()`/`R{N}()` 在源码位置调用一次；
  块在登记后显式 `Refresh()`；`UiBinding` 构造时完成源 → 目标。
- `Render()` 是第 1..N 次：按源码顺序调用根级 `U{N}()`、`__bind{N}.Refresh()`、`__blk{N}.Refresh()`。
- 块 `Refresh()` 内部重跑分支/条目的 `RenderActions`；条目复用/重建由块决定。
- 事件处理器与组件内容是 build-once，不进入首次求值/重算。
- `Build()` 之后、`Mount()` 之前不得调用 `Render()`；宿主通过 `ProcessInvalidations()` 调度。

### 5.3 资源登记

| 资源 | 组件级 | 块模板内 | 组件内容内 |
|---|---|---|---|
| `UiBinding` | `Track(__bind{N})` | `scope{N}.AddDisposable(bind{N})` | ⛔ 不允许 |
| `ConditionalBlock`/`ForEachBlock`/`SwitchBlock<T>` | `Track(__blk{N})` | `scope{N}.AddDisposable(blk{N})` | ⛔ |
| 子组件 | `Track(cmp{N})` | `Adopt(cmp{N}); scope{N}.AddDisposable(cmp{N})` | `receiver.TrackContent(cmp{N})` |
| 事件处理器 | 不登记 | 不登记 | 不登记 |
| 元素/文本 run | 不登记（树持有） | 不登记（块持有） | 不登记（内容树持有） |

- 每项资源恰好登记一次；不得重复 `Track`/`AddDisposable`。
- `Track(IDisposable)`/`Track(Component)` 是 `protected`，只能在生成类内部调用。
- `TrackContent` 是接收方组件的方法；生成代码以 `receiver.TrackContent(...)` 调用。
- 释放是幂等的；生成代码不手工 `Dispose` 已登记资源（块协调器/组件负责）。

### 5.4 释放

- 组件级资源随所属组件 `Dispose()` 释放（逆序）。
- 块内容/条目资源随分支切换、条目移除或块 `Dispose()` 释放。
- 组件内容资源随接收方组件 `Dispose()` 释放。
- 事件处理器随元素树释放；不单独 Dispose。
- 释放后 `Invalidate()`/`Refresh()` no-op（spec/03 §2.6/§7.2）。
- 生成代码不生成 `Dispose()` 重写；释放全由基类/块协调器处理。

### 5.5 线程

- `Build()`/`Render()`/事件处理器均在 UI 线程执行；生成代码不做线程同步。
- 后台/异步状态变化必须由 code-behind 显式 `Invalidate()`；生成代码不自动观察任何对象。
- `UiBinding` 的 `invalidate` 回调只入队；不执行用户代码（spec/03 §2.6）。

## 6. 求值与失效映射

| 构造 | Build（第 0 次） | `Render()`/块刷新 | 结果 |
|---|---|---|---|
| 字面量/静态文本 | 赋值一次 | 不重算 | 不变 |
| 控件 `P={expr}` | `U`/`R` 首次调用 | `U`/`R` 重算 | 公开 setter，相等短路 |
| 组件参数 `P={expr}` | 推送 `U`/`R` | 推送 `U`/`R` | 相等保护 + 子 `Invalidate` |
| 文本 `{expr}` | `U`/`R` 首次 | 重设整段 | 字符串属性/`run.Text` |
| `@if` 条件 | `SetCondition` + 首次 `Refresh` | `Refresh()` 重算 | 分支切换重建 |
| `@if` 多分支 | `AddBranch` + 首次 `Refresh` | `Refresh()` 顺序求值，首个为真者胜 | 分支切换重建 |
| `@switch` selector | `SetSelector` + 首次 `Refresh` | `Refresh()` 重算 | `EqualityComparer<T>` 匹配，分支切换重建 |
| `@if` 内容绑定 | 首次 `R` | 存活分支重跑 `R` | 元素保留 |
| `@foreach` 集合 | `SetItems` + 首次 `Refresh` | `Refresh()` 重新枚举 | 任意 `IEnumerable<T>` |
| `Key` | 首次枚举 | 每次枚举 | 身份对齐、复用/重建 |
| item 内容绑定 | 首次 `R` | 复用重跑 `R`；重建首次 | 见 spec/03 §7.3 |
| 控件 `@bind` | `UiBinding` 源 → 目标 | `Refresh()` 源 → 目标 | 目标变化回写源 + `Invalidate` |
| 组件 `@bind` | 回调赋值 + 首次推送 | 推送 `U`/`R` | `PChanged` 回写源 |
| 控件事件 | `AddHandler` 一次 | 不重算 | 包装后 `Invalidate` |
| 组件事件 | 委托赋值一次 | 不重算 | 包装后失效父组件 |
| 组件内容 | 静态展开一次 | 不重算 | 快照 |
| 组件参数静态值 | 赋值一次 | 不重算 | 不变 |

- `Render()` 不直接写元素属性；它调用 `U`/`Refresh`，由它们写。
- 根级动态构造按源码顺序调用；块内 `R` 按源码顺序执行。
- 一个组件失效只重算自己的根级动态作用域；子组件独立失效（spec/03 §2.2）。
- 事件/绑定/`PChanged` 是唯一自动失效入口；没有对象观察、没有依赖分析。

## 7. 冻结运行时 API

生成代码只允许调用以下 API（以及 BCL 的 `System`/`System.Collections.Generic` 类型）：

```csharp
namespace EclipseUI;

public abstract class Component : global::System.IDisposable
{
    public Element Root { get; }
    public bool IsMounted { get; }
    public void Mount();
    protected abstract Element Build();
    public void Invalidate();
    protected virtual void Render();
    protected virtual bool ShouldRender();
    public bool ProcessInvalidations();
    public void Adopt(Component child);
    protected void Track(Component child);
    protected void Track(global::System.IDisposable disposable);
    public void TrackContent(Component child);
    public void TrackContent(global::System.IDisposable disposable);
    public void Dispose();
}

public sealed class UiBinding : global::System.IDisposable
{
    public static UiBinding TwoWay<T>(
        Element element,
        StyledProperty<T> property,
        global::System.Func<T> readTarget,
        global::System.Action<T> writeTarget,
        global::System.Func<T> readSource,
        global::System.Action<T> writeSource,
        global::System.Action invalidate);

    public void Refresh();
    public void Dispose();
}

public sealed class ConditionalBlock : StackPanelBase, global::System.IDisposable
{
    public void SetCondition(global::System.Func<bool> condition);
    public void SetContent(global::System.Action<BlockScope> contentBuilder);
    public void AddBranch(global::System.Func<bool> condition, global::System.Action<BlockScope> contentBuilder);
    public void SetDefault(global::System.Action<BlockScope> contentBuilder);
    public void Refresh();
    public void Dispose();
}

public sealed class SwitchBlock<T> : StackPanelBase, global::System.IDisposable
{
    public void SetSelector(global::System.Func<T> selector);
    public void AddCase(T key, global::System.Action<BlockScope> contentBuilder);
    public void SetDefault(global::System.Action<BlockScope> contentBuilder);
    public void Refresh();
    public void Dispose();
}

public sealed class ForEachBlock<T> : StackPanelBase, global::System.IDisposable
{
    public void SetItems(global::System.Func<global::System.Collections.Generic.IEnumerable<T>?> itemsGetter);
    public void SetKeySelector(global::System.Func<T, object?> keySelector);
    public void SetTemplate(global::System.Action<T, BlockScope> template);
    public void Refresh();
    public void Dispose();
}

public sealed class BlockScope
{
    public void AddRoot(Element root);
    public void AddRenderAction(global::System.Action action);
    public void AddDisposable(global::System.IDisposable disposable);
    public global::System.Collections.Generic.IReadOnlyList<Element> Roots { get; }
    public global::System.Collections.Generic.IReadOnlyList<global::System.Action> RenderActions { get; }
    public global::System.Collections.Generic.IReadOnlyList<global::System.IDisposable> Disposables { get; }
}

public interface ITextContentHost
{
    TextRun AddRun(string text);
}
```

约束：

- `BlockScope` 的构造函数不是公开 API；生成代码不得 `new BlockScope(...)`。
- `BlockScope` 由 `ConditionalBlock`/`ForEachBlock<T>`/`SwitchBlock<T>` 创建并传入模板；模板只调用
  `AddRoot`/`AddRenderAction`/`AddDisposable`。
- `ConditionalBlock`/`ForEachBlock<T>`/`SwitchBlock<T>` 的 setter 只保存，不自动刷新；生成代码必须显式 `Refresh()`。
- `BlockScope` 的只读列表只供块协调器使用；生成代码不得读取/修改。
- `UiBinding` 构造时完成首次同步；`Refresh()` 幂等；`Dispose()` 解除目标属性监听。
- `Track`/`Adopt`/`TrackContent` 的所有权语义见 spec/03 §2.7/§10.4。
- `ITextContentHost.AddRun(string)` 返回 `TextRun`；不要求生成代码引用 `TextRun` 类型（用 `var`）。
- 上述 API 之外，生成代码只可使用：`System` 基元、`System.Collections.Generic.List<T>`/
  `EqualityComparer<T>.Default`、`System.Action`/`Func`、`System.IDisposable`、`System.String` 插值。

## 8. 确定性、增量与版本

### 8.1 确定性

- 同一输入（`.ui` 文本、code-behind 符号、目录元数据、编译器版本）必须生成**字节级相同**的输出。
- 编号按 §3.2；成员顺序固定为字段（N 升序）→ `Build` → `Render` → `U`（N 升序）。
- 属性/参数/子节点按 `.ui` 源码顺序；块内按源码顺序；不按字典序、不按符号表顺序。
- 字面量格式固定（§4.2）；`#line` 路径按原样；不写时间戳/GUID/随机数/绝对路径规范化。
- hint name 按 §2.1 的稳定哈希；不得因编译顺序或增量重建改变。

### 8.2 增量

- 每个 `.ui` 文件独立生成；一个文件的修改不得改变其他文件的输出。
- 生成器不得读取另一个源生成器的输出；不得依赖编译顺序。
- 增量缓存必须以输入文本/符号为键；不得缓存跨编译的可变状态。
- 同一编译内重复生成同一文件必须幂等；不得累积编号或字段。

### 8.3 版本

- 本文为生成契约 v2；与 spec/03 v1（失效重算）、spec/01 v2 配套。
- 生成器/运行时 API 版本不匹配时，编译器报 `EUI100`（内部契约错误）并停止生成；
  不得生成半套 API 的文本。
- 新增冻结 API 是向后兼容；改变生成形态/编号/顺序是破坏性变更，必须提升契约大版本并更新 §12。
- 生成文件头不写版本号（保持确定性）；版本由工具链清单与本文状态行承担。

## 9. 契约测试 T1–T20

| # | 测试 | 通过标准 |
|---|---|---|
| T1 | 文件头/命名空间/无 `using` | 前 3 行固定；所有具名类型带 `global::`；`ImplicitUsings=disable` 可编译 |
| T2 | 成员白名单 | 只有字段/`Build`/`Render`/`U`；无属性/构造/`Dispose`/嵌套类型 |
| T3 | 字面量格式 | string 转义、double `R`+`d`、int 十进制、bool 小写、`TimeSpan` ticks |
| T4 | 组件级 `P=` | `__el` 字段 + `U` 方法 + `Build` 首调 + `Render` 调用；表达式只出现一次 |
| T5 | 块级 `P=`/文本 | `R` 局部函数 + 首次调用 + `AddRenderAction`；无字段提升 |
| T6 | `@if` 形态 | `new` → 挂父 → `SetCondition` → `SetContent` → `Track` → `Refresh`；空分支 |
| T7 | `@foreach` 形态 | `SetItems` → `SetKeySelector` → `SetTemplate` → `Track` → `Refresh`；item 显式类型 |
| T8 | 组件顺序 | 登记在参数/内容前；`Mount` 在最后；块/内容作用域正确 |
| T9 | 控件 `@bind` | `UiBinding.TwoWay` + 字段/局部 + `Track`/`AddDisposable` + `R.Refresh` |
| T10 | 组件 `@bind` | `PChanged` 回调 + 相等保护 + 推送 `U` + 子 `Invalidate` |
| T11 | 事件 | `h{N}` 初始化 + 包装 `Invalidate`；无订阅/`Track` |
| T12 | 组件内容 | `List<Element>`/`content[0]`；`TrackContent`；`@bind` → `EUI040`；无 `U` |
| T13 | 确定性 | 同一输入两次生成字节相同；hint 稳定；编号不漂移 |
| T14 | `#line` | 每个用户语句有 span 型 `#line`/`#line hidden`；行号正确、start 严格早于 end（零 CS8939）、靠右表达式列精确；不映射脚手架 |
| T15 | 禁止项 | 生成文本不含 §2.5 的禁止 token（`SetValue`/`GetValue`/`using`/`static`/`try`/...） |
| T16 | 失败回退 | 非法输入不抛异常；输出最小 `Build`；诊断由分析器给出 |
| T17 | 可空/编译 | `Nullable=enable` 下无新增警告；`warnings-as-errors` 项目可编译 |
| T18 | 运行时语义 | Build 首评一次；Render 重算；块刷新；Keyed 复用；双向绑定；事件失效 |
| T19 | 诊断出口 | 源项目元数据 problem 报在 C# 声明处（码/路径/行列精确）、引用程序集仍在用点、多 `.ui` 不重复；标签冲突 → EUI016、不可用按 ReasonCode → EUI017/EUI018/EUI030、未知仍落 EUI001；`MetadataOutletTests` |
| T20 | EUI024 事件处理器签名契约 | 可静态判定（表达式自身语义干净 + 对目标委托的转换确定失败）时在 `.ui` 属性值报 EUI024 并短路生成输出；符号未解析/重载失败/`dynamic`/表达式内部错误静默，交回生成代码经 `#line` 由 C# 报错（§2.4 规则 8 不重复报告）；控件事件与组件委托参数的目标类型判定与生成代码一致；`EventHandlerSignatureTests` 16 例 |

- T1–T17、T19 与 T20 是编译器/golden 测试；T18 是宿主/运行时集成测试（S1–S3）。
- golden 文本按本章的形态比对，不比对空白行；`#line` 路径可按测试临时目录参数化。

## 10. `EUI045` 与 code-behind 边界

`EUI045` 守护生成成员与 code-behind 的冲突；原计划 C3，C3 收敛时未落地，2026-11-05 重排 G3（D57），
**2026-11-08 已落地（D57/D80，`CodeBehindConflictTests` 23 例）**。

触发条件：

1. code-behind 声明 `Build()`（任何签名）→ `EUI045`；生成器仍生成 `Build`，C# 报重复。
2. code-behind 声明 `Render()` → `EUI045`；生成器只在需要时生成 `Render`。
3. code-behind 声明与生成成员同名的成员：`__el{N}`/`__cmp{N}`/`__blk{N}`/`__run{N}`/`__bind{N}`/
   `U{N}`/`R{N}` → `EUI045`。
4. code-behind 声明的基类既不是 `Component` 也不是省略（含 `Component` 派生类）→ `EUI045`。
5. code-behind 隐藏 `Invalidate`/`Mount`/`Track` 等生成代码依赖的公开/受保护成员 → `EUI045`。

- 诊断位置在 code-behind 声明；生成器不因冲突改名（R7）。
- C3 之前由 C# 重复成员/隐藏警告暴露；生成器仍按本文生成。
- `EUI045` 不阻止生成器输出；错误诊断已阻断构建。
- 生成器必须容忍 code-behind 的 `partial` 方法、普通字段/属性/方法，只要不冲突。

实现口径（D80）：诊断位置在 code-behind 的**源声明**——逐文件事实只携带纯数据
`UiSourceLocation`（路径 + span + 0 基行列），`Report` 用 `Location.Create(path, span, lineSpan)`
重建外部文件位置，不把 `SyntaxTree`/`Location` 带进增量缓存。生成成员保留名判定为
`__el`/`__cmp`/`__blk`/`__run`/`__bind`/`U`/`R` + 纯数字后缀；生成代码依赖成员白名单为
`Invalidate`/`Mount`/`Track`/`TrackContent`/`AddDisposable`；基类白名单为「省略」或
`Component`/`EclipseUI.Component`/`global::EclipseUI.Component` 原样重复。
EUI045 是 Error 但不计入生成短路，生成器仍按本文输出；其它错误诊断维持既有短路口径。

## 11. 与旧契约的差异入口

- v1 的 `UiWatch`/`Subscribe`/`Signal<T>`/`IINCC`/依赖分析/`subs`/`itemSubs`/`upd` 全部作废；
  仅保留在 design/03 §3.5.3 与 design/15 §15.13.4 的历史台账中。
- v1 的 `SetValue`/`GetValue`/`Bind.TwoWay` 生成形态 ⛔；`@bind` 必须用 `UiBinding`。
- v1 的"首次赋值 + 订阅"顺序被"Build 首评 + Render/Refresh 重算"取代。
- v1 的 `subs{N}.Add(...)`/`itemSubs{N}.Add(...)` 被 `scope{N}.AddDisposable(...)` 取代。
- v1 的 `SetContent(Func<List<IDisposable>, Element[]>)` 被
  `SetContent(Action<BlockScope>)` 取代；`SetTemplate(Func<T, BlockScope>)` 被
  `SetTemplate(Action<T, BlockScope>)` 取代。
- 旧语法（`P:bind`/`P:value`）的等价口径已随 **K27** 废止：v1/\~S5 时代它「生成文本与新语法相同，差异只在
  分析器 `EUI043`」；S6 起旧语法直接报 `EUI046`、**不生成代码**，因此不再存在需要比对的旧形态文本。

## 12. 实现偏差台账 D42–D88

**S1 已收敛（2026-10-30）、S2 已收敛（2026-10-30）、S3 已收敛（控件侧 2026-11-02、组件侧随 C4 2026-11-05）、C1 已收敛（2026-11-02，目录派生）、C3 已收敛（2026-11-03，字面量转换器/使用点诊断/保留语法）、C4 已收敛（2026-11-05，组件标签）、C5 已收敛（2026-11-05，Ref/使用点诊断）、C6 已收敛（2026-11-05，具名插槽 §4.12.5）、G3 七个小切片全部收敛（块布局透明 2026-11-05/D77、失效迁移 2026-11-07/D78、按文件增量 2026-11-08、`#line` 列映射 2026-11-08/D79、EUI045 code-behind 冲突诊断 2026-11-08/D57/D80、诊断出口补全 2026-11-08/D74 ②/D81、EUI024 事件处理器签名匹配 2026-11-08/D82）、S4/S5 运行时硬化部分收敛（2026-11-08/D83）、G2 CPU 侧部分收敛（2026-11-08/D84）**，
下表中标 ✅ S1/✅ S2/✅ S3/✅ C1/✅ C4/✅ C5/✅ C6/✅ G3 的条目已关闭；D82/D83/D84 记录本轮新落地口径与边界。其余偏差按下列余项收敛：S4/S5（AOT/裁剪、最终审计、帧预算/分配预算门槛、CI；Element.Unmount 已随 D89/K33 收敛）、G2（GPU retained layer/真正局部重绘、目标机稳定 60fps 结论、像素探针 DSL、CPU/GPU 交叉一致性门禁）、G4（样式/主题；附加属性首批 ✅ 2026-11-09；泛型标签暂缓 K19、不排期）、IDE/DevTools。台账之外的差异视为实现缺陷。

**2026-11-08 补充（K23）**：D85–D88 记录「决策已进规范正文、代码仍是 v2」的规范先行偏差，目标阶段统一为 **S6 语法对齐**（design/09 §9.5/§9.6），不并入 S4/S5/G2/G4 余项；收敛一条即在对应行标 ✅ S6 并同步 design/03 §3.5.4。Q3/Q4/Q6/Q7 等未拍板细项不登记偏差，待拍板后按 K24 起登记。
D77 是运行时排布口径，**不改本文的生成形态**（块仍按 §4.9–§4.10 生成），规范条文在 spec/01 §4.10/§4.11。
D79 改的是 `#line` 指令形态（§2.4），不改 C# 语句本身的生成规则。
**C1/C2 改元数据来源，不改本文的生成形态**：生成器已改读派生目录（C2，2026-11-02），
生成代码形态与本文规则一致；以下台账条目随之关闭——D62/D65/D69/D72 ✅ C2、
D64/D68 ✅ C1。

| # | 偏差 | 目标（本文） | 阶段 |
|---|---|---|---|
| D42 | ✅ S2：块模板内动态写入生成 `void R{N}()` 局部函数 + `scope{N}.AddRenderAction(R{N})`；根级 `U{N}`/`Render()` ✅ S1 | §4.3.2/§4.9–§4.10 | ✅ S2 |
| D43 | ✅ S1：`Invalidate`/`Render`/`ProcessInvalidations`/`ShouldRender` 已实现 | §7 | ✅ S1 |
| D44 | ✅ S1：`Adopt`/`Track(Component)`/`TrackContent` 已实现 | §7/§5.3 | ✅ S1 |
| D45 | ✅ S1：根级动态绑定提升 `__el{N}`/`__run{N}` 字段 | §4.1.1/§4.3.1 | ✅ S1 |
| D46 | ✅ S1：失效队列、深度排序、迭代上限已实现 | spec/03 §2.4 | ✅ S1 |
| D47 | ✅ S2：`BlockScope` 提供 `AddRoot`/`AddRenderAction`/`AddDisposable`，块创建并传入模板 | §7 | ✅ S2 |
| D48 | ✅ S2：`ConditionalBlock.SetCondition(Func<bool>)`；`SetContent(Action<BlockScope>)`；公开 `Refresh`；setter 只保存 | §4.9/§7 | ✅ S2 |
| D49 | ✅ S2：`ForEachBlock.SetItems(Func<IEnumerable<T>?>)`；`SetTemplate(Action<T, BlockScope>)`；公开 `Refresh`；setter 只保存 | §4.10/§7 | ✅ S2 |
| D50 | ✅ S2：条目复用要求 Key 相同 **且** item 身份相同（值类型按相等） | spec/03 §7.3（Key + item 身份） | ✅ S2 |
| D51 | ✅ S2：条目移除/块 `Dispose` 均逆序释放条目作用域资源 | §5.4/spec/03 §2.8 | ✅ S2 |
| D52 | ✅ C4：组件参数推送生成 `var v = (T)(expr)` + `EqualityComparer<T>.Default` 相等保护 + 差异才写 setter 并 `Invalidate()` 子组件（根级 `U{N}`、块内局部 `R{N}`） | §4.3.3 | ✅ C4 |
| D53 | ✅ S3：控件 `@bind-P` 按 §4.4.1/§4.4.2 生成 `UiBinding.TwoWay`（根级字段 + `Track`，块内局部 + `AddDisposable` + `R{N}` 刷新）；`readSource`/`writeSource` 表达式原样输出、不加括号；`P:bind`/`P:value` 兼容并报 EUI043 | §4.4/§7 | ✅ S3 |
| D54 | ✅ C4：组件 `@bind-P` 生成「`{P}Changed` 回调赋值（build-once）+ 推送 `U{N}`/`R{N}`」；组件目标判定/内容体内 `@bind` 禁用已落地（EUI040）；内层推送形态与 §4.3.3 一致。回调 `{P}Changed` 的类型/arity 与参数严格匹配原记「静态不可判、由 C# 编译错误兜底」（D73），2026-11-11 已在扫描期按符号校验（D110） | §4.4.3/§4.4.4 | ✅ C4 |
| D55 | ✅ S1：事件生成 `h{N}` 委托局部 + 包装内 `Invalidate()` | §4.6 | ✅ S1 |
| D56 | ✅ C4：组件默认内容 build-once 落地——`List<Element>` 累加、单根 `content{N}.Count == 0 ? null : content{N}[0]`、内容子组件 `receiver.TrackContent`、内容内 `@if`/`@foreach` 展开为普通 C# 语句、内容内动态属性为 Build 快照、内容内 `@bind`/`Key`/裸文本分别报 EUI040/EUI010/EUI006 | §4.12.3 | ✅ C4 |
| D57 | ✅ G3（2026-11-08）：`EUI045`（code-behind 与生成成员冲突）落地——常量分配、`TemplateGenerator.ProbeCodeBehindConflicts` 覆盖 §10 五类触发、诊断落在 code-behind 源声明、生成器不改名也不跳过输出；`CodeBehindConflictTests` 23 例（含位置/停用码零出现/仍生成源码）。实现载体见 D80 | §10、§12 D80 | ✅ G3 |
| D58 | ✅ S1：hint name 为 `{ClassName}.{stableHash}.g.cs`（SHA-256 前 16 hex） | §2.1 | ✅ S1 |
| D59 | ✅ S2：`ITextContentHost.AddRun(string)` 已实现；`TextBlock` 实现该接口，运行时不再需要手建 `TextRun` | §4.8/§7 | ✅ S2 |
| D60 | ✅ S2：`ForEachBlock.UpdateChildren` 仅在根序列实际变化时改 `Children` | spec/03 §7.3 | ✅ S2 |
| D61 | ✅ S2：样例与 README 已迁移到普通 CLR 状态；`Signal<T>` 仅留作运行时遗留原语与历史章节说明，不参与模板生成 | 本文/spec/03 | ✅ S2 |
| D62 | ✅ C2：`EUI040` 的目标可绑性此前只来自手写目录 `UiPropDef.Bindable`；C1 派生目录已对每个模板可见属性给出符号级 `CanWrite`（public 可写 + `{Name}Property`）且证明派生 ⊇ 手写标注；C2 起分析器改读派生 `CanWrite` | spec/01 §4.2 | ✅ C2 |
| D63 | ✅ C1：运行时声明特性落地 `Runtime/UiAttributes.cs`（`UiContentKind`/`[UiContent]`/`[UiIgnore]`/`[UiParameter]`/`[UiSlot]`；`[UiTag]` 已于 K32 退役），内置控件加标注；标注纯元数据，不改生成形态 | design/14 §14.4.6/§14.4.4 | ✅ C1 |
| D64 | ✅ C1+C2：`Compiler.Metadata` 目录模型 + Roslyn 符号扫描器（`UiCatalogFactory.Build`）落地，纯函数、不抛异常，问题进 `Problems`/`HiddenTags`；C2 删除手写目录，生成器读派生目录 | design/14 §14.5/§14.10.2/§14.12.2 | ✅ C2 |
| D65 | ✅ C2：`EclipseUI.Compiler.Metadata` 源码链接进生成器程序集（排除 Metadata 的 `Polyfills.cs`），诊断码常量经单一定义流转；测试项目只引用生成器，避免重复类型身份 | design/14 §14.10.1/§14.12.2 | ✅ C2 |
| D66 | ✅ C1：`UiContentKind` 与 `UiContentModel` 数值一一对应，有断言测试 | design/14 §14.4.4 | ✅ C1 |
| D67 | ✅ C1：失效级别守卫白名单收敛为唯一例外 `Element.IsHitTestVisible`；`Panel.ClipToBounds` 失效级别 `None`→`Render` | design/14 §14.11.1 | ✅ C1 |
| D68 | ✅ C1+C2：派生目录属性项 6→17，差集逐条钉进快照测试；C2 起模板实际变宽生效 | design/14 §14.12 | ✅ C2 |
| D69 | ✅ C2：8 输入事件从静态宿主 `InputEvents` 迁入 `Element`（19 处调用改名，删静态类），`CheckBox` 补 `CheckedChanged`；事件表面 1 → 9 个事件名（37 事件项） | design/14 §14.4.3/§14.12.2 | ✅ C2 |
| D70 | ✅ C1：`UiElementDef`/`UiCatalog` 手写结构相等（record 对字典成员按引用比较，会破坏 §14.10.3 管道值比较）；`CatalogValueEq` Ordinal 稳定排序 + unchecked 哈希（netstandard2.0 无 `System.HashCode`） | design/14 §14.10.3 | ✅ C1 |
| D71 | `[UiIgnore]` 与 `[UiParameter]`/`[UiSlot]` 同时出现时 `[UiIgnore]` 生效并按 **EUI028** 记问题；design/14 §14.6.1 只写「不得」，没为这条分配诊断码，EUI028 原文只覆盖「参数声明非法」。C4 未处理（沿用 EUI028，不复用新码）；需要细化时另起新码并先记台账 | design/14 §14.6.1/§14.9.1 | 开放 |
| D72 | ✅ C2（语义修订）：golden 快照落成仓库内文件 `TestData/BuiltInSurface.golden.txt`（`ECLIPSEUI_UPDATE_SURFACE_GOLDEN=1` 再生）；原文「固定目录从快照反序列化」不采纳——快照改为扫描结果的**渲染评审门**（显式再生成 + diff 强制评审），纯结构测试各自现扫 `Compilation`（全扫实测 45 ms） | design/14 §14.11.1/§14.12.2 | ✅ C2 |
| D73 | ✅ C4（残余风险登记）：组件 `@bind-P` 只静态校验「存在 `{P}Changed` 委托参数」（EUI040），不校验其实参类型与 `P` 匹配——恰与 spec/01 §4.14 「缺失/类型不符 → EUI040」中的「类型不符」不可严格一致。类型不匹配由生成代码的 C# 编译错误兜底（经 `#line` 指回 `.ui`）；不要在运行时静默吞掉。若未来 Roslyn 类型推断下放到扫描期，可再收紧。**→ 2026-11-11 已收紧（D110），本条关闭** | §4.4.3 规则 1 / spec/01 §4.14 | ✅ C4 → ✅ D110 |
| D74 | ✅ G3（2026-11-08，② 收敛）：① 使用点 EUI019/EUI020/EUI021 落地（C5）——`UiElementDef.IsFromReferencedAssembly` 区分来源，只有引用程序集控件的 `def.Problems` 在用点暴露，源项目控件仍落 EUI002/EUI006；`Ref`（EUI029）同切片落地。② 声明处出口落地：`UiMetadataProblem` 携带纯数据 `UiSourceLocation`，生成器注册目录级 `SourceOutput` 把源项目 problem 报在 C# 声明处（成员级取 `def.Problems`、类型级取 `catalog.Problems`）；标签解析失败按类别分流 EUI016（冲突）/EUI017/EUI018/EUI030/EUI031（不可用）/EUI001（未知）。EUI024 已落地（2026-11-08，D82）。实现口径见 D81，`MetadataOutletTests` 14 例 | design/14 §14.5.4/§14.9.1/§14.12.5、spec/02 §12 D81 | ✅ C5 ① / ✅ G3 ② |
| D75 | ✅ C6（解析口径两级）：自身插槽的解析——分析器按「目录显示名与 `.ui` 文件名短名匹配」投影自身 def（静态前端无符号信息）；生成器则用 code-behind **符号**直接沿基类链读 `[UiSlot]` 传给分析器/发射器作权威（code-behind 类在源码里不派生 `Component`——基类由生成注入，目录里不会有它的 def）。两级在正常房间结构下指向同一组插槽；目录匹配失败而符号可用时生成侧仍有效。与既有 `FindCodeBehind` 的近似-符号双口径同型 | §4.12.5 | ✅ C6 |
| D76 | ✅ C6（契约补写）：基链同名**成员**的胜出规则原文未定。Roslyn 的 `GetAttributes()` 不把特性带进 override（与反射的 `Inherited=true` 语义不同），因此「按名字一律去重」会让 `public override Element? Header`（不写特性）把基类 `[UiSlot("Header")]`/`[UiParameter]` 静默丢掉；「不去重」又会把同一个 override 报成重复插槽（EUI028）。实现定为：**派生侧显式写了模板特性或 `[UiIgnore]` 才算覆盖**，否则基类声明继续生效；`[UiIgnore]` 在 override 上是基类同名声明的显式退出。已回写 design/14 §14.10.2（参数/插槽条目）与 spec/01 §4.15 | design/14 §14.10.2/§14.6.1 | ✅ C6 |
| D77 | ✅ G3（契约补写 + 实现口径）：块布局透明的机制原文只写「不引入额外间距/尺寸约束」，design/09 的切片描述是「不再作为 StackPanel 参与布局」。实现选择**保留块为被父容器定位的 `StackPanelBase` 节点**，只把它的布局内边距取 0、间距取最近非透明布局祖先的 `Gap`，并让零尺寸透明块整体退出父容器的间距计数——而不是把条目摊平进父容器的子序列。理由：摊平会让块的 `FinalRect` 与子元素坐标系脱钩，`ElementPainter` 的偏移累加、`Element.PointFromRoot`/`HitTest`、`DamageTracker` 都得跟着改；保留节点则这四条路径一行不动，排布结果与摊平等价（`BlockLayoutTests` 10 例 + `TaskListEndToEndTests` 2 例钉住，含命中测试穿块回归）。同时补写三条原文未定的规则：① 块自身 `Gap`/`Padding` 属性值不参与布局；② 空块（含只含空块的嵌套块）不占间距槽；③ 空块仍 arrange 到零矩形，避免 `FinalRect` 残留上一轮位置而被命中/脏区读到。新增 `Element.IsLayoutTransparent`（无 `{Name}Property` 字段，不进模板表面，golden 快照不变）。G4 的 Grid/WrapPanel 需要各自的透明排布口径，届时另记 | spec/01 §4.10/§4.11、design/03 D16 | ✅ G3 |
| D78 | ✅ G3（契约补写 + 实现）：`Adopt` 接管不迁移子组件原根队列里已入队的失效——所有权变更后该队列不再被排空，接管前的 `Invalidate()` 被静默丢弃（GalleryApp 空 Tasks 卡根因）；且 `Depth` 在接管时缓存，链长变化后残留旧深度，§2.4「同深度按入队顺序」可能把孙排在父前。实现：`Adopt` 按入队顺序 drain 子队列并重入新根（按待渲染标记合并）；`Depth` 改所有权链实时计算。契约补写 spec/03 §2.4/§2.7；`ComponentOwnershipTests` 5 例钉住（含接管前失效、孙随子迁移、父先于子、幂等不重复、合并） | spec/03 §2.4/§2.7 | ✅ G3 |
| D79 | ✅ G3（契约补写 + 实现）：原文 §2.4 规则 9 只承诺行号、把列映射排到 G3。实测 Roslyn 4.9.2 增强型 `#line (sl, sc) - (el, ec)`：列平移 `sc−1` 只作用于紧接的一行，后续行行号递增、列按生成列；`end` 元组不参与单行诊断换算，但 start 必须严格早于 end，否则报 `CS8939` 且整段映射失效、错误落回 `.g.cs`。实现：`Emitter.Mapped` 统一发射 span 型指令，`sc` 按「生成缩进 + 用户 token 在生成行内的偏移」左移并钳制 ≥ 1（生成前缀长于源列时退化为行映射，行号仍精确），空 span/钳制后给 end 补 1 列宽度；`MappedToken` 从生成文本自动定位用户表达式偏移，35 个调用点全部由行号改为 `TextSpan`。同步补写 §2.4 规则 9/10 与 §11 等价口径（旧/新语法别名的生成文本改为剥离 `#line` 后比对）。`LineSpanMappingTests` 5 例（Roslyn 语义固化 3 例 + 生成指令合法且无 CS8939 + 靠右表达式列精确），`GeneratorContractTests` 3 处断言同步 | §2.4、§11 | ✅ G3 |
| D80 | ✅ G3（实现口径补写）：§10 只说「诊断位置在 code-behind 声明」，未定载体。实现用纯数据 `UiSourceLocation`（路径 + span + 0 基行列）走逐文件 facts，`Report` 以 `Location.Create(path, span, lineSpan)` 重建外部文件位置（不把 `SyntaxTree`/`Location` 带进增量缓存）；保留名 = `__el/__cmp/__blk/__run/__bind/U/R` + 数字后缀，生成依赖成员白名单 = `Invalidate/Mount/Track/TrackContent/AddDisposable`，基类白名单 = 省略/`Component`/`EclipseUI.Component`/`global::EclipseUI.Component`；EUI045 报 Error 但不阻止生成输出，其它错误诊断仍短路。`CodeBehindConflictTests` 23 例钉住五类触发、位置、零停用码 | §10、§11 T16 | ✅ G3 |
| D81 | ✅ G3（实现口径补写）：D74 ② 原文只说「声明处报错需要定位源声明语法」，未定载体/去重/码映射。实现：① `UiMetadataProblem` 增加可选纯数据 `UiSourceLocation`（成员符号 `Locations` 优先、类型声明兜底；引用程序集无 `IsInSource` 位置 → null），进 record 值相等/哈希；② 生成器新增目录级 `RegisterSourceOutput(catalogProvider)`，与逐文件管道解耦，同一条 problem 无论 `.ui` 数量只报一次，`Distinct()` 再去重 catalog/def 两处的重复项；③ 源项目控件的 EUI019/EUI020/EUI021 problem 按 §14.9.2 折叠为 EUI026，EUI026/EUI028/EUI031 保持原码；④ 诊断不阻断生成输出（与 EUI045 同口径），`Report` 对无 path 的内存语法树退化为 `Location.None`；⑤ 逐文件管道 `WithTrackingName("PerFileGeneration")`，增量断言不再混读所有 `SourceOutput` step；⑥ 标签码分流只改 `Analyzer.VisitElement` 的诊断码，消息与 span 不变。`MetadataOutletTests` 14 例 | design/14 §14.9.1/§14.9.2、spec/02 §11 | ✅ G3 |
| D82 | ✅ G3（2026-11-08，实现口径）：EUI024 事件处理器签名匹配落地——常量 `UiDiagnostics.EventHandlerSignature` 早已分配；`TemplateGenerator.ValidateEventHandlerSignature` + `TemplateProbe.IsHandlerCompatible`（复用 `ProbeTree` 合成 partial 方法体）借 Roslyn 把处理器表达式放进 code-behind 符号上下文，仅当「表达式自身语义干净 + 对目标委托的转换确定失败」时在 `.ui` 属性值报 Error 并短路生成输出（与 EUI004 一致）；符号未解析/重载失败/`dynamic`/表达式内部错误静默，交回生成代码经 `#line` 由 C# 报错（§2.4 规则 8 不重复报告）。控件事件复用 `Emitter.EventHandlerType`（`internal static`）、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`，保证诊断与生成代码类型判定一致。`EventHandlerSignatureTests` 16 例 | §4.6.1 规则 7、§9 T20、design/14 §14.11.2 | ✅ G3 |
| D83 | 部分收敛（S4/S5，2026-11-08）：`PropertySystem.cs` 新增 `IValueBox`/`ValueBox<T>`，`Element._localValues` 由 `Dictionary<StyledProperty, object?>` 改为 `Dictionary<StyledProperty, IValueBox>`，重复赋值只改泛型字段、消除值类型装箱；`Component.Dispose` 在锁内清 `_pendingRenders`/`_pendingSet`。`RuntimeHardeningTests` 7 例（分配预算、继承/绑定/通知语义、泄漏回归）。**明确未做**：`Element.Unmount/Detach`（design/14 明确 v1 不提供、G4 才补）、AOT/裁剪配置、旧语法清理、最终审计清单、帧预算/分配预算门槛、CI 配置、S5 基准门槛 | spec/03 §13.1、design/09 §9.3 G8/S5 | 部分收敛（S4/S5） |
| D84 | 部分收敛（G2 CPU 侧，2026-11-08）：`PixelGoldenTests` 4 例 + `TestData/PixelGolden.baseline.txt`（纯图元、无字体依赖的 CPU 像素 golden：`ContentHash` 全图门禁 + 探针 ±2 容差 + `ECLIPSEUI_UPDATE_GOLDEN=1` 生成开关 + 失败输出实际 PNG 到 TestResults）；`SceneGraph/RetainedFrameCompositor.cs`（CPU 侧 retained 合成原型：持有上一帧 `RenderTarget`，按 `FrameDamage` 只 blit 脏区，断言脏区外逐字节不变、与整帧重绘等价）；`tools/bench-60fps.ps1` 与 `design/reports/target-machine-60fps-template.md`。**明确未做/边界**：原型只是合成层、不减少光栅化工作量，真正局部重绘需 `IRenderBackend.Render` 增加脏区/scissor 重载且 VelloSharp 0.5.0-alpha.3 未提供公开 API；GPU surface 的 retained layer 依赖 HWND + wgpu device 本机无法验证；「稳定 60fps」结论依赖目标机真实显示器（本机 29Hz 虚拟显示），只给可复现测量程序与报告模板；像素探针 DSL（render-gate-2026-11 backlog #7）仍缺 | design/09 §9.3 G2、design/reports/render-gate-2026-11.md | 部分收敛（G2 CPU 侧） |
| D85 | 规范先行（K22，2026-11-08）：模板 `@using {Ns}` 已定为合法指令、生成文件须按模板顺序输出普通 `using {Ns};`、标签只在导入命名空间内解析且内置控件无豁免（未导入报 `EUI001` + 缺 using 建议）。**当前实现**：`Lexer.ParseDirective` 仍把 `@using` 归 `EUI009`，`Emitter` 不输出任何 using，`UiCatalog.Resolve` 仍是全局短名字典、与命名空间无关；spec/02 §2.2/§2.5 旧口径已加注取代关系。收敛条件：P1 指令表、P2 `UiUsingDirective`、P3 文件头与目录过滤、P6 全部样例 / 约 94 例 golden 一次性迁移补 `@using` 并回归 | K22、spec/01 §1.5、design/16 §5.3 | ✅ S6（2026-11-08 收敛） |
| D86 | 规范先行（K21/K23 ④，2026-11-08）：`@code { }` 为合法顶层节点，位置不限、可多个块、书写顺序不影响成员可见性，成员合并进同一 partial 类并与 code-behind 并存，`EUI005` 放宽。**当前实现**：`@code` 撞 `EUI009`，`EUI005` 仍强制同名 `.cs`，`TemplateProbe.ProbeTree` 只注入 code-behind 声明。收敛条件：P1 块体捕获、P2 `UiCodeBlock` + 根层例外、P3 成员透传 + **先全量收集再建探针 Compilation**（否则前置 `{expr}` 误报 `EUI007`/`EUI024`）+ `EUI045` 扩到 `@code` 成员、P6 单文件端到端用例与底部 `@code` 样例 | K21、K23、spec/01 §1.5、design/16 §5.2/附录 B | ✅ S6（2026-11-08 收敛） |
| D87 | 规范先行（K23 ③，2026-11-08）：`else`/`else if`（不带 `@`）与 `@switch`（`case`/`break`/`default`）纳入目标语法，`EUI008` 停用、新码从 `EUI046` 起。**当前实现**：只有单分支 `@if`，分支续接撞 `EUI100`，`@switch` 报 `EUI008`。收敛条件：P1 续接与 case 头、P2 分支/switch 节点、P4 `ConditionalBlock` 多分支（`AddBranch`/`SetDefault`）与 `SwitchBlock<T>`（草案见 design/16 附录 B）、P5 停用码零出现 + 诊断快照；`case` 内嵌 `@foreach` 与 `@for`/`@while` 属 Q3/P4 待定 | K23、spec/01 §1.5、design/16 §3.1/附录 A | ✅ S6（2026-11-08 收敛） |
| D88 | 规范先行（K23 / design/16 §3.1，2026-11-08）：新增 `@* ... *@` 编译器丢弃注释，未闭合报 `EUI100`（顺带关闭 **D12**）；`<!-- -->` 按 spec/01 §8 规则 2 给弃用期，具体形态待 Q6。**当前实现**：只识别 `<!-- -->`，且 `Lexer.SkipComment` 未闭合时静默吞到文件尾（D12 开放）。收敛条件：P1 词法、`TokenizerTests` 与未闭合注释用例、P6 样例迁移；Q6 未拍板前不删除 `<!-- -->` 支持 | K23、D12、design/16 §3.1 | ✅ S6（2026-11-08 收敛） |
| D89 | ✅ S4/G4：**Element.Unmount 卸载链路**（K33）——`UnmountedEvent`（RoutingStrategy.Direct，标 `[UiIgnore]` 退出模板事件表面）、`Element.Unmount()`（摘父级 + 子树自上而下触发钩子/事件，幂等）、`OnUnmount` 控件钩子；组件 `Mount()` 订阅根元素 UnmountedEvent → `Dispose()`（作用域/绑定随之解除），`Dispose` 退订并复位 `IsMounted`、新增 `IsDisposed`；手工 `Children.Remove` 仍不触发（保守原语义）；`FocusManager.ClearFocusIfDetached()` 接入 HeadlessRoot.UpdateLayout 与 WindowHost 帧循环。测试 `UnmountTests` 9 例（事件/钩子/组件释放/绑定解除/嵌套 Track/幂等/Remove 契约锁定/焦点修剪） | design/09 §9.3 S4（Element.Unmount 属 G4）、design/14 §14.4.5/§14.6.4、design/15 §15.3/§15.7 | ✅ S4/G4（2026-11-09） |
| D90 | ✅ G4 布局：**WrapPanel 流式布局**——`Orientation`（Horizontal/Vertical）+ `Gap`；主轴装箱换行、期望尺寸取最长行（首版漏 maxMain 已修）、越界子项独占一行不裁剪；目录表面 5→6 标签、17→22 属性项、37→44 事件项，golden 重生成（+15 行）；`WrapPanelTests` 5 例（水平/竖直换行、Gap+Padding、不换行、越界独占、空面板） | design/09 §9.3 G4 布局补全 | ✅ G4（2026-11-09） |
| D91 | ✅ G2：**像素探针 DSL**（render-gate-2026-11 backlog #7）——`PixelProbe` 流式断言门面：点 `At`/`AlphaAt`、区域 `DominantColor`（覆盖率，失败给 top3 直方图）/`Contains`/`NotContains`（首命中坐标）/`CountIn`/`Count`、帧间 `SameAs`/`SameAsOutside`（`RenderTarget` 与 byte[] 快照重载，逐字节精确）；失败聚合一次性报告，`Failures` 可外查。`PixelGoldenTests` 升级双层门禁：语义探针（期望值自描述）先于基线比对、生成模式写基线前先过语义探针；Damage/Retained 手写双循环收敛为 DSL。既有红根因：VelloSharp AA 舍入差 1（`4,32` 半覆盖像素 `#FFFFFF80`→`#FFFFFF7F`），基线重生成后 `PixelGoldens_MatchBaseline` 首次全绿；`PixelProbeTests` 8 例 | design/09 §9.3 G2 ④、design/13「后续」像素 golden 项 | ✅ G2（2026-11-09） |
| D92 | ✅ G2：**CPU/GPU 交叉一致性门禁**（backlog #8）——`CrossBackendGateTests` 场景矩阵 14 项（实色/圆角/椭圆/路径/三种渐变/裁剪栈/变换/图层透明度/混合模式/阴影/1:1 图像/虚线，文本不进矩阵）；四级容差策略 Tight ±2/0.05%、Edges ±2/0.5%、Gradient ±4/2%、Loose ±16/5%，超阈值门禁失败（聚合违例 + 首违点双侧颜色）；无 GPU 自动跳过并写 TestResults/cross-backend-gate-report.txt 留痕，有 GPU 时每场景一行实际 mismatch 数据作校准依据（首跑 14 场景 0 违例、最大通道差 ≤1） | design/09 §9.3 G2 ⑤、render-gate-2026-11 backlog | ✅ G2（2026-11-09） |
| D93 | ✅ G2：**retained 显示列表（段级）**——`ElementPainter` 自绘命令段缓存（ConditionalWeakTable；键 = RenderVersion+绝对位置+尺寸+焦点态；clip/滚动条段外每帧生成）；失效契约与 DamageTracker 一致，arrange 平移由段键位置捕获；`LastBuildStats`/`ResetSegments`；金标准 retained 渲染 == 全量渲染（`RetainedPaintTests` 8 例，含 GPU 后端分支 retained 合成等价）。VelloSharp 无 scissor API，光栅化仍整帧、构建层 retained 先行 | design/09 §9.3 G2 ⑥ | ✅ G2（2026-11-09） |
| D94 | ✅ S5：**预算门槛扩展**——`RuntimeHardeningTests` +3：附加属性重复赋值不重分配值盒；布局热路径分配常数级（Grid 轨道解析引用键缓存 + cells/`double[]` 缓冲复用 + `ElementCollection` struct enumerator 消除 foreach 装箱）；retained 帧构建 ≤512B/帧（锁 D93 段复用，结构性常数为每帧新 DisplayList/FrameDamage） | design/09 §9.3 G2 ⑦、§9.4 S5 | ✅ S5（2026-11-09） |
| D95 | ✅ S5：**AOT/裁剪试点**（Gallery.Desktop）——full trimming 0 警告通过、发布产物实跑出图（源生成器架构无运行时反射）；AOT ILC 通过但运行阻塞于上游 `VelloSharp.NativeLibraryLoader` 静态构造器 `Assembly.GetCallingAssembly()`（AOT 不支持，需上游改 `AppContext.BaseDirectory`）；链接需手动 `LIB`/`INCLUDE`（MSVC+Windows SDK 路径，ILC 自动探测不含 SDK）。试点配置固化 csproj：`PublishTrimmed`/`TrimMode=full` Release 条件 + `EnableAot` 自定义开关（**坑：`-p:` 全局属性传染 netstandard2.0 源生成器 → NETSDK1124**） | design/09 §9.3 G2 ⑧、§9.4 S5 | ✅ S5（2026-11-09；AOT 待上游） |
| D96 | ✅ S5：**最终审计与文档同步**——全量 657 例全绿（Headless 332 / Compiler 241 / Rendering 84）0 警告。审计修复：① `RuntimeHardeningTests` A1 去装箱测试的 `[Fact]` 被注释行吞掉、自 D83 起从未运行（恢复后实测通过）；② spec/03 §2.6 死循环上限契约补测试（`Component.MaxRenderIterations = 100`：超限 fail-fast + 上限内恰好收敛，实现本已存在）。README/design/09/spec/03 验收清单与数字全面同步，S5 出口全项核对关闭（正式吞吐基准表随 G8） | design/09 §9.4 S5、spec/03 §13.1/§2.6 | ✅ S5（2026-11-09） |
| D97 | ✅ G2：**retained 段缓存标签回归修复 + 画廊布局复位**——① `PaintButton`/`PaintCheckBox` 的 `_text.Paint` 写主帧列表 `_list` 而非段缓冲 `_paintList`（D93 重构漏改）：首帧文字命令先于背景 AddRange（背景后画盖掉居中文字）、且文字不进段复用帧彻底丢字；两条路径同错使「retained == 全量」金标准抓不到——补 2 例探针回归测试（按颜色可识别的 FillRect 文字绘制器，锁「文字在控件背景之后 + 复用帧存在」）。② G4 showcase（~700px）排在 add 按钮与任务卡之前，把二者推出 760px 视口：GalleryCheck `add task appends item` 点击窗口外坐标静默丢失（bisect D92–D96 全 FAIL，非渲染回归）——交互演示复位到 showcase 之前，selfcheck PASS | design/09 §9.3 G2 ⑨ | ✅ G2（2026-11-09） |
| D98 | ✅ G4：**S-A 主题令牌化**——`ElementTheme` virtual 令牌 + 静态 Dark/Light 预置（可实例化定制）；`ElementPainter.ReplaceTheme` 换表丢弃全部段（段内命令持有颜色快照）+ 重置脏区整帧重绘；`WindowHost.SetTheme` 运行时切换；Gallery「Toggle theme」双宿主接线（`ThemeTests` 3 例） | design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D99 | ✅ G4：**S-B Class 样式 + 有效值分层**——属性系统分层 local > class > 继承 > 默认（`SetStyleValue`/`ClearStyleValues`/`ClearLocalValue`，失效语义与 SetValue 一致）；`Element.Classes` + `Styling.Provider`（`IStyleProvider` 注入；Rendering 层 `StyleRegistry` 类名直配 setter 序列、无运行时选择器引擎；晚装配补应用 pending）；`Visual` 附加属性组（Background/Foreground/CornerRadius，null=不覆盖）+ painter 读覆盖回落主题（未设置零漂移）；「显式取消覆盖」=`ClearLocalValue`。模板 `Class` 语法与编译期类名校验、按主题资源查找留下一片（`StyleTests` 6 例；Gallery primary/tool 双 class 演示） | design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D100 | ✅ G4：**S-C 最小资源查找——`ThemeColor` 主题感知双色**——`ElementTheme.IsLight` 虚标志（LightTheme override true，自定义表按视觉明暗覆盖）；`Visual.BackgroundResource/ForegroundResource`（`ThemeColor?`，解析优先级 **resource > 静态覆盖 > 主题令牌**）+ `StyleBuilder.Background/Foreground(ThemeColor)` 重载；painter 在**段构建期**解析（`ResolveBackground`/`ResolveForeground`/`PeekForeground`）——解析发生在段构建时，换主题 `ReplaceTheme` 丢段重建即随新表刷新，**无需新增失效维度**（`ThemeTests` +2：双色按 IsLight 解析、资源背景随换主题切换且复用语义保持——注意资源卡不能作树根，根节点跳过卡片绘制）；画廊分区卡/标题/样式表全改双色资源，inline 静态粉保留以并示「静态覆盖 vs 主题跟随」两种语义；完整资源查找（资源字典/`Style=` 模板语法）仍留 S-C 后续 | design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D101 | ✅ G4：**AppBuilder + 依赖注入（Avalonia 式启动形态）**——Runtime 新增 `Application`（`Services`/`MainWindow`/`Host`/`DefaultTheme` + `OnFrameworkInitializationCompleted`）、`AppBuilder`（`Configure<TApp>`/`ConfigureServices`/`UseTheme`/`UseStyles`/`UseSetup`/**`Build`**/**`Start`**——`Build` 供无头/离屏/测试形态）、`Window`（描述式窗口：Title/尺寸/Content/RootComponent/Theme/帧循环调节项）；**依赖方向解耦**：Runtime 不引用 Rendering/平台包——主题走 `IHostTheme` 标记接口（`ElementTheme` 实现，调用侧隐式上转）、样式走 `IStyleProvider` 泛型传注册表实例；容器用 Microsoft.Extensions.DependencyInjection（`UseSetup` 平台先注册、用户 `ConfigureServices` 后注册——MEDI 同类型最后注册优先，可覆盖）。扩展片：Platform.Windows `UseWindows()` + `ClassicDesktopLifetime`（材料化 Win32 窗口 + 表面 + `WindowHost` 帧循环；`WindowHost` 实现 `IWindowHost`、`Application.SetTheme` 主题接线；诊断输出沿用）；Rendering `ISurfaceFactory` + `ISurfacePresenter.Description`（一行式后端诊断）；Rendering.Vello `UseVello()`；Text.Shaping `UseHarfBuzzText()`（字体候选 + `TextMeasurer.Current` 装配 + 无字体回落 measuring）。画廊：`GalleryApp`（场景构建 + 主题按钮经 `Application.SetTheme`）+ `GalleryLaunchOptions`（CLI→DI 消费演示）+ Program 缩为 builder 链；`--cpu/--shot` 走 `Build()` 离屏对照（与窗口同一套装配）。`AppBuilderTests` 8 例；全量 678 例全绿（340/241/97）0 警告；GalleryCheck selfcheck PASS | design/09 §9.3 工程化 | ✅ G4（2026-11-09） |
| D102 | ✅ G4：**伪类状态样式（CSS `:hover`/`:active`/`:focus` 对应物）**——`PseudoStates` Flags 掩码（Hover/Pressed/Focused）进 `Element`：`InputRouter` 维护 Hover（命中目标翻转、捕获期间冻结、释放重算）与 Pressed（按下即置、**先路由后清**——保 Button Click「按下才触发」门控）、`FocusManager` 维护 Focused；状态变化 = 渲染失效 + class 层重应用（`ReapplyStyles` 与 `Classes` setter 共用一条路径）；`StyleRegistry.Define` 返回链式 `ClassStyleVariants`（`.Hover(s=>…)/.Pressed/.Focus`——变体按声明顺序应用，同时匹配时后声明胜，与 CSS 同优先级按源顺序一致）；ElementPainter 段键 `bool Focused` → `PseudoStates` 掩码（状态翻转段重建，即使值失效短路）+ 焦点判定改「元素状态 ∪ focused 参数」并集（retained 复用键与全量重画同源）；Button/CheckBox 删私有 `_pressed` 迁移到基类状态（Released 事件内仍可读按下态，时序契约）。**既有 bug 修复**：焦点环 stroke 居中于外扩 FocusRingWidth 的路径、外缘达 1.5×宽度+AA——visualBounds 只膨胀 1×，环外缘像素留在脏区外（GPU retained 合成 vs 整帧参照逐像素比对抓出 79 像素泄漏），膨胀改 1.5×+1；`StyleTests` +4、`PseudoStateTests` +6；全量 688 例全绿（346/241/101）0 警告；selfcheck PASS；静态出图 hash 与 D101 一致（无状态变化零漂移） | design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D103 | ✅ G4：**:disabled + IsEnabled（样式系统 E3 片，design/17 执行计划首片）**——`PseudoStates.Disabled=8` 第 4 位（**有效值**：`Element.IsEnabled` 声明值 + `IsEffectiveEnabled` 沿祖先链合取继承，合成进掩码——变体匹配/段键零特殊处理）；`IsEnabled` setter 对整棵子树逐后代失效 + 重应用（有效状态变化影响后代 Disabled 位）；`InputRouter`：命中禁用元素吞按下（不捕获/不悬停/不聚焦/不路由）、按住中途禁用释放吞 Click（只清按下位）、禁用不路由移动/滚轮/按键、UpdateHover 禁用视为无目标；`FocusManager`：`Focus` 拒绝禁用（直禁与祖禁）、`Focusables`/Tab 遍历跳过禁用；ElementPainter `ResolveForeground/ResolveBackground/PeekForeground` 置灰令牌回落（`ElementTheme.DisabledForeground/DisabledBackground` 深浅两套，class 变体优先于令牌）；`StyleRegistry.ClassStyleVariants.Disabled()`（`:disabled` 变体链）；`Visual.FocusRingWidth` class/inline 覆盖（StyleBuilder `FocusRingWidth`，painter 环宽与脏区膨胀读 class 值回落令牌）；画廊样式区 `:disabled` 演示按钮（primary 变体置灰绿）；`StyleTests` +5、`PseudoStateTests` +6；全量 699 例全绿（352/241/106）0 警告；selfcheck PASS；深浅双主题全页出图核对（disabled 置灰、文字可读、无回归） | design/17 §3.2/§7 E3、design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D104 | ✅ G4：**Eustyle 样式表编译器（样式系统 E1 片，design/17 §4）**——`.eustyle` 全局样式表与 `@style` 组件块（§4.8）编译为生成代码，运行时零引擎：EustyleParser（SCSS 子集——类规则 + 变体两种形态：选择器 `.card:hover` 与块内 `&:hover`（`:active` 为 `:pressed` 的 CSS 别名）+ `$变量` 编译期替换 + `theme(dark, light)` 双色 + `#rgb/#rrggbb/#rrggbbaa` + 裸数字 + `//`/`/* */` 注释；错误恢复消费停止字符——初版恢复点停在 `;` 不消费导致死循环，测试宿主 SIGTERM 暴露）；EustyleGenerator 发射 `registry.Define(...)` 块 lambda 变体链（类型 global:: 全限定；闭合括号随块——首版变体链缺收尾 `)` 被 Contains 断言漏过、画廊真实构建才暴露，现生成产物必须过 CSharpSyntaxTree 语法级校验）；诊断 **EUI050**（语法/不支持形态）/**EUI051**（未知属性，目录 = StyleBuilder setter 集）/​**EUI052**（值类型不符：形态错/未定义变量/单位后缀显式拒绝）——design/17 初版误分配 EUI048–052 与既有 K31/G4 码冲突，按实际占用顺延修正并预留 EUI053/054（E4/E5）；TemplateGenerator `.eustyle` 管道（Collect → 单 `StyleBootstrap.g.cs`：`public static Apply(StyleRegistry)` 供 `UseStyles` 方法组直连，命名空间取项目根命名空间，任一文件 Error 短路）；`@style` 块：Lexer（与 `@code` 同构整块捕获）/Parser（UiStyleBlock）/Analyzer（仅顶层，同款诊断）→ UiCompiler 类名加组件前缀 `{Component}__{class}` → Emitter 静态幂等 `__RegisterStyles()`（守卫位 `__euiStylesRegistered`，Build 首行调用，注册进 `StyleRegistry.Instance`；`__eui*` 前缀与 `__RegisterStyles` 进 EUI045 生成保留名守卫）；画廊迁移：删 `GalleryStyles.cs` → `app.eustyle`（primary/tool 全变体走样式表，颜色 hex 化）+ `Showcase.ui` `@style` 块演示；`EustyleTests` +23（解析/变量/变体两形态/诊断码/span 偏移映射/生成器契约含语法级校验/错误短路）；全量 722 例全绿（352/264/106）0 警告；selfcheck PASS；深浅双主题出图核对（primary/tool/disabled 外观与迁移前一致，零回归） | design/17 §4/§5、design/09 §9.3 G4 | ✅ G4（2026-11-09） |
| D105 | ✅ G4：**模板 `Class`/`Style` 属性转正（样式系统 E2 片，design/17 §4.8/§5）**——① 类名目录管道：`.eustyle` 类名 + 各 `.ui` `@style` 块前缀类名（`{Component}__`）Collect 成值相等 `StyleClassCatalog`，combine 进逐文件管道（目录不报诊断——解析错误由各自主管道报告，有错即空集）；② `Class="a b"` 字面量：空白拆分逐名解析（**先本文件 `@style` 块（裸名→注册名映射，遮蔽同名全局类）、再全局目录**），未知类名报 **EUI031**（转正语义：原「保留未实现」终止；`<Class>` 标签形态保留不变），生成 `Classes = new string[]{...}`；`Class={expr}` 探针推断形态——string 发 `Split`（默认）、`IEnumerable<string>` 经 `ClassKindFact`（ValueSpan.Start 定位，跨 doc 应用）发 `ToArray`、确定其它类型报 EUI031，经 `EmitDynamicUpdate` 走 U/R 推送（根级触发字段提升）；③ `Style="prop: value; ..."`：只允许字面量声明序列（表达式 EUI031——元素属性无状态语境），`Eustyle.ParseDeclarations` 新入口（拒绝类规则——`SkipBracedBlock` 大括号配对整块跳过防残渣二次报错；拒绝 `$变量`；尾声明无分号 + EOF 容忍），发射 local 层直赋——`Visual.SetBackground/SetForeground/SetCornerRadius/SetFocusRingWidth`（theme() 升级 `SetBackgroundResource/SetForegroundResource`）、`Panel.PaddingProperty`/`TextBlock.FontSizeProperty` 走 `SetValue`，每条声明独立 #line 映射到值 span（**首版缺收尾 `)`**——E1 同款教训重演，单元断言补 CSharpSyntaxTree 语法级校验防回归）；④ `@bind-Class`/`@bind-Style` → EUI040；⑤ 画廊 Showcase.ui：`Class="demo-scope"`（本文件 @style 闭环）+ `Class="primary" Style="corner-radius: 18"`（local>class 层级演示）+ `Class="tool primary"`（数组序后者胜）；`EustyleTests` +17（声明序列/发射器含语法校验/管线已知未知类名/空目录报未知/@style 本地闭环/表达式三形态/bind 拒绝）；C3/C4 旧保留语义测试改写为转正语义（无目录语境宽松回写 + 裸词 Style 报 EUI050）；全量 739 例全绿（352/281/106）0 警告；selfcheck PASS；双主题全页出图 + PNG 探针核对 primary 绿两主题渲染 | design/17 §4.8/§5/§7、design/03 §3.3.9/§3.3.2/§3.5.4 | ✅ G4（2026-11-09） |
| D106 | ✅ G4：**嵌套塌缩 + `.ui` 注入（样式系统 E4 片，design/17 §4.5）**——① 解析器：`EustyleNested`（DirectChild/TypeName/ClassConstraint/SelectorSpan/Setters/Nested），`ParseBody` 加嵌套分支（`>` 直接子 / `.` 类约束 / PascalCase 类型名——属性名全小写的区分依据），嵌套体支持声明 + `&:state` 变体块 + 再嵌套；变体块内嵌套选择器报 EUI050（「状态 × 结构」无对应物）；② 塌缩器 `EustyleCollapse.Flatten`：派生名 `{父}__{kebab(类型)}{-约束}`（`TextBlock.title` → `text-block-title`），**同名段合并**（CSS 同选择器多块 = 声明合并——首版误判为冲突，实测画廊场景即两个 `> Button` 块，改为 setters 拼接 + 子嵌套递归聚合），派生名与**显式类名**撞车报 **EUI053**（`StyleDerivedNameConflict` 新常量；修正 design/17 初版误写 EUI051——已被 E1 占用）；注入规则（RootClass/Chain/DerivedName，ChainSeg 类型名塌缩时即 kebab 化）；③ 管道：`StyleSourceFact`（类名 + 注入规则）替代 `StyleClassFact`，`StyleClassCatalog` 扩展 `Injects`（值相等 `InjectRuleData`/`InjectSegData`，诊断 span 不进缓存数据）；`@style` 块派生名带前缀注册并进 localStyleClasses 映射（模板 `Class="panel__button"` 闭环校验），嵌套类约束按「先本文件」序解析；④ 注入匹配（ExecuteFile，Analyze 后）：根类按解析后注册名比较，直接子**穿透** @if/@foreach/@switch（块内模板编译期展开、运行时就是直接子），插槽填充不穿透；命中回写 `UiElement.InjectedClasses`（去重）；⑤ Emitter：字面量 Class 合并注入（派生类排解析类名后 → §2.2 派生类胜）、`Class=""` 仅注入时发注入数组、无 Class 属性注入发独立数组、表达式形态跳过（§4.5-3 明示边界）；`EustyleRule` 构造扩 4 参（Nested）。EustyleTests +13（嵌套解析/塌缩命名与合并/显式冲突 EUI053/嵌套 :state 双写法等价/变体块内嵌套拒绝/派生 Define/注入 direct+foreach/表达式跳过/派生类目录校验/@style 本地前缀注入）；画廊 app.eustyle 加 `.card` 塌缩链路（`> Button`、`TextBlock.hint`、同名 `> Button:hover` 合并进变体）+ Showcase.ui 分区卡演示（含 @foreach 行注入）；页脚 739→752；**752 例全绿（352/294/106）** 0 警告；selfcheck PASS；双主题出图 + PNG 探针核对 card 背景两主题渲染（light 卡片 y1081-1233 连续色块）；DLL 探针确认派生类注册 + 注入双引用 | design/17 §4.2/§4.5/§5/§7 | ✅ G4（2026-11-09） |
| D107 | ✅ G4：**注册制伪类（样式系统 E5 收尾片，design/17 §3.2/§5/§7）**——① 运行时：`PseudoStatesRegistry.Register(name)` 启动期静态注册表（名称规范化去前导 `:`；**平台保留名 hover/pressed/active/focus/disabled 撞名抛 ArgumentException**——与 @states 编译期同名单；幂等同掩码——生成代码变体链内联 Register 与宿主各自调用互不干扰；位从 `Disabled<<1`=0x10 自增，int 掩码 27 个注册位、耗尽抛 InvalidOperationException 不够升 long（§8 既有风险条目）；`IsRegisteredMask` 内部校验 = 掩码即凭证）；`Element.SetRegisteredState(mask, bool)`（public——**拒绝平台位/None/未注册位**，掩码只接受 Register 分配位的非空子集），注册位 OR 进 `PseudoStates` 合成掩码——变体匹配与段键零特殊处理；`ClassStyleVariants.State(mask, build)` 注册位变体链（与平台位变体共用应用序，声明序后者胜）；② 解析器 `@states { name; name; }` 顶层声明块：**预扫两遍解析**（声明序无关——首版预扫推进 `_i` 后 Document 未重置，规则被静默跳到块尾后，测试暴露）；撞平台名报 EUI050、重复名去重；三处状态解析（选择器链/嵌套选择器/`&:state` 变体块）统一 `ResolveState`「平台位 → 本文件声明 → 全局目录」，未命中报 EUI050（消息提示 @states 声明途径）；`EustyleSetter` 加 `RegisteredState`（注册名归属，Variant 恒 None）；`Style=` 声明序列不开预扫（`scanStates: false`——元素属性无状态语境，@ 块走 '@' 分支报错而非被预扫吞掉）；③ 管道两级收集：`ExtractStyleStates` 预扫声明（`.eustyle` 全文 / `.ui` 逐 @style 块，经 `Eustyle.CollectStates` 公开入口）→ `StyleStatesCatalog` 值相等目录 → `ExtractStyleClasses`/`ExecuteFile`/`ExecuteStylesheet` 的 Parse 传全局并集兜底跨文件引用（注册名是全局掩码命名空间，**不做类名式组件前缀**）；`StyleClassCatalog` 扩 `States`；④ 生成：变体分组键 (Variant, RegisteredState)，注册位组发射 `.State(global::EclipseUI.PseudoStatesRegistry.Register("name"), s => …)`；`EmitStateRegisters` 在 Apply/`__RegisterStyles` 顶部发幂等注册行（未被变体引用的声明位也注册）；⑤ **EUI054 双通道冲突**（`StyleDualChannelConflict`，Warning 不短路）：新 `HandwrittenDefines` 管道扫宿主 C# 语法树（方法名 Define 且首实参字符串字面量的调用——注释/非字符串不算，误报容忍）与样式表类求交集，Warning 挂样式表类名 span，产物照常生成；⑥ painter 补齐：`PaintTextBlock` 有 class/local 背景即画底色（`ResolveBackground` 此前只被 Button 分支调用——TextBlock 挂 .spinner 背景有值无绘制的现状补齐；圆角读 class 覆盖回落按钮半径令牌）；画廊 app.eustyle `@states { spinning; }` + `.spinner`/`:spinning` 变体 + GalleryScene 双芯片演示（idle 灰 / `SetRegisteredState(mask, true)` 绿——掩码即凭证）；EustyleTests +10（选择器/变体块/嵌套注册名/声明序无关去重/未声明 EUI050/平台名冲突/跨文件 externalStates/@style 注册行/Style= @ 拒绝/EUI054 命中与不冲突）、PseudoStateTests +7（幂等递增/冒号规范化/平台名与非法名拒绝/翻位/SetRegisteredState 拒绝三态/State 变体端到端/声明序后者胜）；**769 例全绿（359/304/106）** 0 警告；selfcheck PASS；双主题 height 3000 出图 + PNG 探针核对 spinning 绿两主题命中（dark #22c55e / light #1aad52，y2078-2093）、idle 灰命中、hash 与改动前一致（默认视口芯片滚出不漂移） | design/17 §3.2/§5/§7/§8、design/03 §3.3.9 | ✅ G4（2026-11-10） |
| D108 | ✅ **样式属性单一来源 + CLR 原名规范名（design/17 §4.3）**——① 删除 `EustyleParser.Properties` 手写 6 项字典、`SortedPropertyNames()` 手写数组、`EustyleGenerator.EmitLocalSetters` 手写 switch；新增 `StylePropertyCatalog`（Compiler 纯数据、值相等）与 `StylePropertyCatalogFactory`（Metadata 的 Roslyn 扫描），随 `UiCatalog` 走同一条 `CompilationProvider` 派生并挂在 `UiCatalog.StyleProperties`，Parser / EUI051 支持列表 / `Style=` 发射 / theme() 准入全读这一份；② 扫描 `StyleBuilder` 公开非泛型 setter 推出属性集与值形态（Color/ThemeColor→color、double→number），按约定找 `{Name}Property` / `{Name}ResourceProperty` 静态字段并校验 `T`，缺字段/歧义/形态不符进 `Problems` 以 `EUI026` 报出；③ 属性名一律用控件原始 CLR 名（`CornerRadius`/`FontSize`/`FocusRingWidth`），**大小写敏感、精确匹配**——`StylePropertyCatalog.Find` 按 Ordinal 比较，无别名、无大小写/`-`/`_` 归一化，旧 kebab 写法与大小写变体一律报 EUI051（消息提示 case-sensitive CLR name），大写开头属性名与嵌套类型选择器由前瞻 `{`/`;` 区分；④ `Style=` 发射统一为 `el.SetValue(<目录字段>, <值>)`（D105 的逐属性 `Visual.SetXxx` 形态由本项取代）；⑤ 一致性闸门 `StylePropertyCatalogTests`（反射对齐 StyleBuilder setter 集 / 目标字段真实存在 / theme 准入），`EustyleTests` 补规范名、旧写法拒绝（kebab/大小写变体 Theory + 内联 `Style=`）、嵌套歧义、PascalCase 未知属性、无资源字段拒 theme、生成器端到端，并把全部既有样式源码改用规范名；画廊 `app.eustyle` / `Showcase.ui` 改用规范名；**784 例（359/319/106）0 回归**（`PixelGoldens_MatchBaseline` 像素哈希本机差异为改动前既有） | design/17 §4.3/§4.4/§4.8 | ✅（2026-11-10） |
| D109 | ✅ **组件标签上的元素级属性落到根元素（spec/02 §4.12.6）**——组件不是 `Element`（spec/03 §5.3），`Class`/`Style`/附加属性此前被直接发给组件实例，生成 `cmp0.Classes`（CS1061）与 `Grid.SetRow(cmp0, 1)`（CS1503），分析器放行而 `dotnet build` 红。修复：① 发射点移到 `Mount()` 之后、挂父级之前（`Root` 那时才存在）；② 附加属性目标改 `cmp.Root`（含动态形态的字段提升——此前 `Grid.Row={expr}` 在根级必然编译失败）；③ `Style=` 走 `cmp.Root.SetValue(...)`；④ `Class=` 走新增的 `Component.ApplyClasses(...)`：运行时持有根自身类的**一次性快照**，每轮只整体替换外部类部分（根级 `Class={expr}` 重算不累积），合并序「自身类在前、外部类在后」与控件同属性后写者胜一致；⑤ 塌缩注入（E4）命中组件标签时走同一入口。用例：`ComponentTagElementAttributesTests` 5 例（字面量/顺序/根级动态/块内/注入 + 生成产物零编译错误）、headless `ComponentTagElementAttributeTests` 4 例（真实工程夹具 `ComponentTagHost.ui` → `ComponentTagTarget.ui`：`Grid.GetRow` 命中、类集合并序、`Style=` local 层、动态重算不累积）；Compiler 366 / Headless 364 / Rendering 106 / Ide 132 全绿 | spec/02 §4.12.1/§4.12.6/§4.19/§5.1、design/14 §14.6.4/§14.6.7/§14.5.5、spec/01 §4.2 | ✅（2026-11-11） |
| D110 | ✅ **`{P}Changed` 的形状在扫描期判定（spec/02 §4.4.3 规则 1，关闭 D73）**——此前只校验「同名成员是委托」，`[UiParameter] Action? CountChanged` 或 `Action<string>` 配 `int Count` 都会通过 EUI040，直到生成 `cmp0.CountChanged = v => …` 才由 C# 报 CS1593/CS1503（分析器放行、`dotnet build` 红）。现在 `CollectComponentMembers` 在配对时按符号判两件事：① `DelegateArity == 1`；② 回调形参类型与 `P` **同一类型**（`SymbolEqualityComparer.Default`，可空标注忽略）。不满足则 `ChangedCallbackName = null`、原因写入新增的 `UiParameterDef.ChangedShapeProblem`，使用点由 EUI040 报出期望形状（含 `Action<T>` 字面写法）；补全因为只看 `ChangedCallbackName` 自动不再列出该目标。用**身份**而非隐式转换判定：生成的 lambda 形参由目标委托推断，随后直接进 `EqualityComparer<T>.Default.Equals(source, v)` 与 `source = v`，`Action<object>` 收 `int` 语义合法但这两行编译不过。顺带：`ValidateBindSource` 对组件目标要求 `ChangedCallbackName != null`，目标不可绑时不再叠加一条 EUI037（与控件侧「目标不在表面就早退」同口径）。用例：`BindTargetTests` 3 例（零参回调 / 载荷类型不符 / 目录侧形状拒绝 + 形状正确者不受影响）；另加 `ComponentSurfaceMatrixTests`——一个模板里把参数/绑定/事件/默认内容/具名插槽/块模板/元素级属性各用一次，断言「生成产物 + code-behind」合成一个编译单元零错误（这类缺陷只在真编译时才现形）；Compiler 370 / Headless 364 / Rendering 106 / Ide 132 全绿 | spec/02 §4.4.3 规则 1、spec/01 §4.5/§6、spec/03 §8.3、design/14 §14.6.1 | ✅（2026-11-11） |

- 每项收敛必须同时删除对应的 v3 路径；不得两套模型并存。
- 新增偏差必须登记编号（从 D89 起）并注明目标阶段；不得只改代码不改台账。
- 台账编号与 design/03 §3.5.4 保持一致；两边冲突以本文为准并回写 design/03。

## 13. 变更规则

1. 修改任何生成形态前，先改 spec/01（若涉及语法）或 spec/03（若涉及语义），再改本文。
2. 每个新增/删除的生成形态必须同步：§0.5 索引、§4 形态、§9 测试、§12 台账。
3. 编号/顺序/前缀属于契约；变更必须提升契约大版本（§8.3），并在 §12 记录迁移方式。
4. 不得在本文保留"临时生成形态"；未实现的语义标 S1–S5/C1–C6/G3/G4，不得默默生成近似形态。
5. 契约评审分开回答：完整性与可实现性、可解析性与语义自洽性；两者都通过才定稿。
6. 本文与 spec/03 冲突时：求值时机/失效语义按 spec/03，文本形态/顺序/编号按本文；
   评审必须消解冲突，不得长期并存。
7. **决策与实施分离**：K23 已定但尚未实现的目标语法按 spec/01 §1.5 登记，本文相关条目保留「当前实现」描述，
   差距登记在 §12（D85–D88）；不得把目标形态写成已生效，也不得因代码未实现而不登记决策。

## 14. 关键决策 K1–K31

> **权威记录**：所有架构、语法、范围与排期决策都登记在本表（理由栏注明日期与提出方），
> 并同步受影响的规范条目与台账（spec/01 §6.2/§7.1、design/03 §3.5.4、spec/02 §12、design/09）。
> 会话记录与 AI 工作记忆只作留痕，**不是决策的权威载体**；只写在那里视为未落地。

| # | 决策 | 理由 |
|---|---|---|
| K1 | 失效重算取代依赖订阅；模板状态是普通 CLR 成员 | 状态/集合类型不受限；无 `INCC`/`Signal`/`[UiState]` 约束 |
| K2 | 组件级动态 → `U` 方法 + 字段；块级动态 → `R` 局部函数 + render action | 保留树 + 少分配；块作用域可释放 |
| K3 | `Build` 是第 0 次渲染；`Render` 只重算，不构造 | 首次求值一致；无虚拟 DOM diff |
| K4 | `@if`/`@foreach` 由块协调器管理；生成器不做元素 diff | 分支/条目生命周期集中；Keyed 复用可控 |
| K5 | `BlockScope` 由块创建并传入（`Action<BlockScope>`） | 模板失败时块持有部分资源，可释放 |
| K6 | 块 setter 只保存；生成代码显式 `Refresh()` | 避免自动刷新导致嵌套块二次初始化；首次求值可控 |
| K7 | 组件先登记作用域，再赋参数/内容，最后 `Mount` | 参数/内容/Mount 失败时组件仍可释放 |
| K8 | 事件处理器先转 `h{N}` 委托局部，再包装 | 表达式只出现一次；避免每次调用重建委托 |
| K9 | 组件参数推送相等保护 + 子 `Invalidate`；双向用 `PChanged` | 避免无意义失效；父/子不互相覆盖 |
| K10 | 组件内容 build-once、静态展开；子组件 `TrackContent` | 与 design/14 内容契约一致；无内容 diff |
| K11 | 每个绑定点的表达式只出现一次（bind/PChanged 固定读写位置除外） | 避免重复求值/诊断；`#line` 清晰 |
| K12 | 插值文本用 `{(expr)}` 包裹 | 避免 `:` 格式说明符；保留原始表达式 |
| K13 | 只为跨方法引用提升字段 | 减少生成字段；块内闭包捕获局部 |
| K14 | `idCounter`/`fnCounter` 共享、源码顺序、不复用 | golden 稳定；无字典序漂移 |
| K15 | 不重写表达式；仅插值/括号/目标类型转换 | 诊断落到 `.ui`；不引入语义差异 |
| K16 | 非法输入最小 `Build` 回退，不抛异常 | R5；避免级联错误 |
| K17 | `EUI045` 守护 code-behind 冲突 | 生成成员固定名；冲突必须显式 |
| K18 | 组件内容列表总是 `List<Element>`（仅内容存在时分配） | 单根/多根统一；`IReadOnlyList` 直接赋值 |
| K19 | **泛型标签（`<DataGrid<Order>>`）暂缓实现，不排入 G4 或任何当前里程碑**；保留语法位维持现状（撞 `<` 报 `EUI100`），D5 的精确 `EUI030` 诊断随之暂缓；目录侧「泛型类型标签不可用 → EUI030」使用点分流保持不变 | 用户决策（2026-11-08）：泛型标签语法与诊断都不是当前阻塞项，不为不排期的项投入；暂缓 ≠ 取消，D5 仍是开放项，重新排期时须先更新本表 |
| K20 | **决策权威位置**：所有架构/语法/范围/排期决策以本表为权威登记点，并同步受影响的规范条目与偏差台账；AI 会话记忆、聊天记录只作留痕，决策未进项目文档视为未落地 | 用户要求（2026-11-08）：决策只存在于对话或 AI 记忆里会在后续会话丢失，且无法随代码一起评审；已同步 spec/README 权威关系第 6 条与 spec/01 §8 规则 7 |
| K21 | **`@code` 位置与成员可见性对齐 Blazor**：模板内 `@code` 块可位于文档顶层任意位置（根元素之前、之后或前后各一），允许多个块，成员全部合并进同一个 partial 类；**书写顺序不影响模板表达式的可见性**（上方模板可引用下方 `@code` 成员）；`@code` 不进入元素内容，不引入 `@functions` 别名 | 用户要求（2026-11-08）：Blazor 允许 `@code` 在模板下方；Microsoft Learn 的 `Markup.razor` 即「上引用下」，Razor 语法参考明确「More than one `@code` block is permissible」。本条只冻结位置口径，**不提前放开 `spec/01` 对 `@code` 的拒绝**；整波方向已由 K23 登记（K22 为 `@using`），实施细节从 K24 起登记并同步规范正文。实现时须先全量收集 `@code` 成员再建探针 Compilation，否则前置表达式会误报 `EUI007`/`EUI024` |
| K22 | **模板支持 `@using`；所有标签（含内置控件）必须先 `@using` 才能解析；生成代码保留模板声明的 using 指令**：`@using Ns` 收集后输出生成文件中的普通 `using Ns;`（按模板顺序、去重）；不生成 `global using`、`using static` 或模板未声明的 using（类型别名由 **K31** 另行放开）；标签解析由「全局短名字典」改为「按导入命名空间过滤目录」，未导入的标签报 `EUI001` 并给出缺失 `@using` 建议，同名歧义仍 `EUI016`；内置控件位于 `EclipseUI.Elements`，亦须显式 `@using EclipseUI.Elements` | 用户决策（2026-11-08）：EclipseUI 无 HTML 原生标签层、所有标签都是 CLR 类型，不应有隐式全局可见的特例；同时让 `@code` 原文（如 `List<string>`）在生成文件里有合法命名空间，消除附录 B 的 Q5 / global using 缺口。**状态：✅ S6 已实现（2026-11-08）**——`Lexer` 捕获 `@using`（`EUI009` 特化消息）、`Emitter` 按模板顺序输出 using 组、`UiCatalog.Resolve` 按导入命名空间过滤（global 命名空间类型始终可见），全部样例与约 94 例 golden 已一次性迁移并回归；整波方向已由 K23 登记，实施细节从 K24 起 |
| K23 | **`.ui` 语法对齐 Blazor/Razor 整波方向与范围**（用户 2026-11-08 明确要求，本轮全部决定汇总）：① **方向**——语法表面向 Blazor/Razor 对齐，取其组织形态精华（流程控制、模板内代码块、单文件、Razor 注释），弃 HTML 宿主语言的糟粕；**不复用 Razor 编译器与 Blazor 运行时**，K1–K18 保留元素树 + 失效重算模型不动；既有「不复用 Razor 语法层」措辞按 C1 修订为「不复用 Razor 编译器 / HTML 语法债，书写形态可对齐」。② **不做 Web 指令**——`@page`/`@layout`/`@rendermode`/`@namespace`/`@model`/`@section`/`@inject`/`@preservewhitespace`/`@typeparam`/`@inherits`/`@implements`/`@attribute`/`@attributes` 与 CSS `class`/`style` 语义一律不提供；HTML 标签兜底、`@("xxx")` 字面量、属性大小写不敏感、最小化属性、XML 实体 / CDATA 同属不采纳。③ **流程控制对齐**——`else`/`else if`（不带 `@`）、`@switch`（`case`/`break`/`default`）纳入目标语法，`EUI008` 停用、`EUI009` 收窄、新诊断从 `EUI046` 起。④ **单文件**——`@code { }` 合法且与同名 `.cs` partial 并存，`EUI005` 放宽为「`@code` 或同名 partial 至少其一」；位置与成员可见性按 K21。⑤ **`@using`** 按 K22 全量落地。⑥ **表达式记号**——保留 `{expr}` 单花括号为本波工作基线（Q1 推荐口径 A），`@` 只用于指令 / 代码块 / 注释。⑦ **排期**——新增 **S6「语法对齐」** 切片，插在 G3 之后、G4 之前（按 Q8 推荐值登记），实施阶段 P0–P6 见 design/16 | 用户决策（2026-11-08）：「目前的语法我不满意，请列一个计划和 Blazor 对齐；取其精华去其 HTML 相关的糟粕」、「`@page`/`@rendermode` 之类的不需要有」、「流程控制上也要和 Blazor 对齐」、「也需要支持单个页面同时要写代码和模板」；Q1 用户在论证后按保留 `{expr}` 口径要求产出示例并连续追问生成形态，未提异议，故按该口径登记工作基线；Q8 按 design/16 推荐值登记排期，如需调整须修订本表。**状态：✅ S6 已实现（2026-11-08）**——`@code`/`@using`/`else`/`else if`/`@switch`/`@* *@` 全链路落地（词法→解析→分析→探针→发射→运行时块协调器），规范先行偏差 **D85–D88** 全部收敛标 ✅；Q3/Q4/Q6/Q7 与 case 细项已按 **K24–K30** 登记并实施；全部样例与约 94 例 golden 补 `@using` 迁移后 `dotnet test EclipseUI.slnx` 497 例全绿。已同步 spec/01 §1.5/§2.7/§2.9/§3.1/§6.2/§7.1、spec/02 §2/§4.14/§12、spec/03 §13、design/01/02/03/08/09/16、README 三份。**后续改判（2026-11-11）**：`@namespace` 从②的「不做」清单移出，改为必填命名空间声明（`EUI055`/`EUI056`），见 §2.2 与 spec/01 §1.5；本行其余决定不变 |
| K24 | **`@for`/`@while`/`@do` 本波不提供（Q3 推荐口径 A）**：循环一律用 `@foreach`（索引循环用 `Enumerable.Range` 或 `@code` 方法返回集合）；`@for`/`@while`/`@do` 落 `EUI009`，消息给出改写建议（`use @foreach with Enumerable.Range or a code-behind method`）。不新增块类型、不定义循环次数变化的复用语义（design/16 R2 规避）；未来有真实需求须先按新 K 登记 | 用户决策（2026-11-08）：R2 风险（动态次数与 Keyed 复用冲突）收益不成比例；`@foreach` + `Enumerable.Range` 已覆盖索引循环；design/03 §3.7 逃生舱「模板表达不了的流程写 C# 方法」 |
| K25 | **`@code` 内容边界（Q4 推荐组合）**：`@code` 块是**原文透传的类成员区域**——① 嵌套类型（class/struct/enum/interface/record）允许，透传零特判；② 块内 `using` 指令不允许，由 C# 类体语义报错（经 `#line` 指回 `.ui`），引导写模板顶层 `@using`；③ 空 `@code { }` 合法无操作；④ 块内顶层语句（非成员声明）不建专门诊断，由 C# 报错；⑤ `@code` ↔ 同名 `.cs` 的成员同名冲突由 C# partial 规则报，**`EUI045` 只扩展到「`@code` 成员撞生成保留名（`Build`/`Render`/`__el{N}`/`__cmp{N}`/`__blk{N}`/`__run{N}`/`__bind{N}`/`U{N}`/`R{N}`）」**，诊断位置在 `.ui` 的 `@code` 块内成员 span；词法层只做整块捕获 + span，不做成员粒度白名单 | 用户决策（2026-11-08）：透传 + 探针 Compilation + C# 兜底的哲学（§2.4 规则 8「不重复报告 C# 错误」）覆盖全部粒度校验，白名单方案纯成本无收益；与 Blazor `@code` 同口径 |
| K26 | **`<!-- -->` 永久保留为与 `@* *@` 等价的合法注释（Q6 口径 C）**：两套注释并存，`<!-- -->` 不设弃用期、不发告警、不迁移存量（§8 规则 2 的弃用期要求不触发——语义未改义，`@* *@` 是新增语法）；两种注释**未闭合统一报 `EUI100`**（`Lexer.SkipComment` 静默吞 EOF 的行为废止，D12 关闭）；design/16 §3.1 与 D88 中「`<!-- -->` 给弃用期（形态待 Q6）」的表述按本条修订 | 用户决策（2026-11-08）：拍板 C（永久保留）；`@* *@` 与 `<!-- -->` 等价并存，文档/工具链双轨登记 |
| K27 | **旧绑定语法 `P:bind`/`P:value` 本波移除（Q7 口径 B，提前 S5/G8 的旧语法清理）**：`EUI043` 停用且保持停用（测试与快照零出现，§8 规则 4）；新增 **`EUI046`（Error）**：`P:bind`/`P:value` 已移除，提示写 `@bind-P={expr}`；`spec/01` §2.3（`:ident` 修饰符）、§4.6（旧语法章节改为「已移除」）、§6.2 与 `spec/03` §11 同步；`design/09` G8 的「`:bind`/`:value` 清理」项随之提前完成 | 用户决策（2026-11-08）：拍板 B——少一个弃用周期；S6 本就是语法对齐切片，旧语法清理并入后 S5 剩余范围只剩基准/AOT/审计 |
| K28 | **`@{ ... }` 代码块不采纳**：`@` 后跟 `{` 报 `EUI100`，消息引导「statements go in `@code` members or code-behind methods」；理由：`@{}` 内联进 `Build` 语句流会让局部变量被后续 `{expr}` 隐式共享，而动态点在 `U{N}`/`R{N}` 里又不可见，语句级生成形态（Build 一次性 vs 每次渲染重执行）与 K3/K4 模型交织，语义分岔成本远超收益；`@code` 成员 + code-behind 方法已覆盖。未来重开须按新 K 登记 | 用户决策（2026-11-08）：按推荐口径 A 拍板；K23 ② 永久清单未含 `@{}`，以本条单独登记 |
| K29 | **`@switch` 分支体能力与细节（Q「case 内嵌 @foreach」推荐口径 + 三条附带）**：① case/default 分支体 = **完整内容模板**（元素、文本、嵌套 `@if`/`@foreach` 均可），与 `@if` 分支体能力对等，`SwitchBlock<T>.AddCase(key, Action<BlockScope>)` 与 `SetContent` 同构；② case 标签必须是**编译期常量**（字面量/const 成员/枚举成员等常量形态；非常量形态在语法层拒绝，常量形态的 const 校验由探针判定、不可判定交 C#），非常量报 **`EUI047`（Error）**；③ **无贯穿语义**——每个 case 独立分支（等价隐式 break），但语法上仍要求写 `break` 对齐 C# 肌肉记忆，非空 case 缺 `break` 报 `EUI100`；嵌套块内的 `break` 报 `EUI100`（块内无「跳出」语义）；④ `default` 至多一个（重复报 `EUI100`）；分支匹配用 `EqualityComparer<T>.Default`（design/16 附录 B 口径）；缺 `default` 合法（无匹配时块为空，同 `@if` 全假） | 用户决策（2026-11-08）：分支体能力拍 A（附录 A 示例成立）；三条附带口径（常量标签/无贯穿但要求 break/default 至多一个）一并确认 |
| K30 | **`@switch` section 边界口径（实施补丁，S6 落地时发现）**：① 工具流里 `break;` 与下一个 `case`/`default` 头常常同处一个文本段，解析器按「行首标记 + C# 片段扫描」把段拆成 Case/Default/Break/Text 片段，`break;` 必须**单独结束该行**（其后只允许空白）；② 由此 `case 1:` 紧跟 `case 2:` 在语法上**不是 C# 的共享标签组**，而是「case 1 分支体为空」——无贯穿语义下这会静默渲染空内容，故显式拒绝：空 case 无 `break;` → `EUI100`「case has no body and no 'break;' (multi-label sections are not supported)」；③ `default` 无 `break;` 只在**非空体**时报错，末尾空 `default` 合法 | 实现口径（2026-11-08）：K29 ③ 只说「非空 case 缺 break 报 `EUI100`」，未覆盖多标签形态；实测「看似可用、运行时才炸」的风险必须先拒，口径写进 spec/01 §4.16 与 spec/02 §4.17 |
| K31 | **`@using Alias = Ns.Type` 一体两用**：① 生成 `using Alias = global::Ns.Type;`（服务 `@code` 与模板表达式，Blazor 口径）；② **同一条别名也是模板标签别名**——`<Alias/>` 解析为该类型，**优先于目录 tag**，且**绕过命名空间可见性**（目标命名空间不必 `@using`）。这是本框架相对 Razor 的扩展：Razor 的别名只影响 C# 名称绑定，重名只能靠完整限定标签。约束：目标必须是目录里的已知元素/组件（否则 `EUI048`）；同一模板内别名唯一、不得与已导入命名空间同名（`EUI048`）；别名不进 `CollectImports`；探针树同步写入别名行。词法层把 `@using static`/`@using (...)`/尾分号/泛型别名/`A.B =` 从误导性的 `EUI006` 改为 `EUI100` 专属消息并吞掉整行 | 用户决策（2026-11-09）：按「A+B 合并」落地——别名既当 C# 别名也当标签；生成形态见 spec/02 §4.18，语法见 spec/01 §4.17 |
| K32 | **`[UiTag]` 退役**：`EclipseUI.UiTagAttribute` 从 `EclipseUI.Runtime` 删除，目录标签**恒等于类型短名**（`UiCatalogFactory` 的 `TagOf`/`IsValidTag` 一并删除）。理由：改名只能作用于自家库、改不了第三方库，与 K31 别名职责重叠；且它让「类型名 = 标签名」这条可预测性失效，并为目录引入第二事实源。随之改口径：① EUI016 消息改为 `tag 'X' is ambiguous between … ; pick one with a template alias, e.g. '@using XAlt = Ns.X' then use <XAlt/>`；② EUI031 保留标签名冲突消息去掉「or its [UiTag]」；③ 重名只能来自「不同命名空间/不同程序集的同名类型」，测试改用同名类型构造歧义 | 用户决策（2026-11-09）：`[UiTag]` 相关设定全部删除，消歧统一走 K31 别名 |

## 附录 A · 规范示例（TodoList）

模板（spec/01 附录 A 的 TodoList，节选）：

```xml
<StackPanel Gap="8">
  <TextBlock FontSize="24">待办清单</TextBlock>
  <CheckBox @bind-Checked={ShowCount} Label="显示任务数量" />
  @if (ShowCount) {
    <TextBlock FontSize="14">共 {Tasks.Count} 项</TextBlock>
  }
  <Button OnClick={() => AddTask()} Label="添加任务" />
  @foreach (var t in Tasks) {
    <StackPanel Key={t.Id} Gap="4">
      <CheckBox @bind-Checked={t.Done} />
      <TextBlock>{t.Title}</TextBlock>
    </StackPanel>
  }
</StackPanel>
```

生成文本（省略 `#line`，字段/成员按 §8.1 顺序）：

```csharp
// <auto-generated> 由 EclipseUI 模板编译器生成；请勿手动修改。 </auto-generated>
#nullable enable
namespace Examples;

public partial class TodoList : global::EclipseUI.Component
{
    private global::EclipseUI.UiBinding __bind3 = null!;
    private global::EclipseUI.ConditionalBlock __blk4 = null!;
    private global::EclipseUI.ForEachBlock<global::Examples.TodoItem> __blk10 = null!;

    protected override global::EclipseUI.Element Build()
    {
        var el0 = new global::EclipseUI.Elements.StackPanel();
        el0.Gap = 8d;

        var el1 = new global::EclipseUI.Elements.TextBlock();
        el1.FontSize = 24d;
        ((global::EclipseUI.ITextContentHost)el1).AddRun("待办清单");
        el0.Children.Add(el1);

        var el2 = new global::EclipseUI.Elements.CheckBox();
        __bind3 = global::EclipseUI.UiBinding.TwoWay(
            el2,
            global::EclipseUI.Elements.CheckBox.CheckedProperty,
            readTarget: () => el2.Checked,
            writeTarget: v => el2.Checked = v,
            readSource: () => ShowCount,
            writeSource: v => ShowCount = v,
            invalidate: Invalidate);
        Track(__bind3);
        el2.Label = "显示任务数量";
        el0.Children.Add(el2);

        __blk4 = new global::EclipseUI.ConditionalBlock();
        el0.Children.Add(__blk4);
        __blk4.SetCondition(() => ShowCount);
        __blk4.SetContent(scope5 =>
        {
            var el6 = new global::EclipseUI.Elements.TextBlock();
            el6.FontSize = 14d;
            var run7 = ((global::EclipseUI.ITextContentHost)el6).AddRun("");
            void R0()
            {
                run7.Text = $"共 {(Tasks.Count)} 项";
            }
            R0();
            scope5.AddRenderAction(R0);
            scope5.AddRoot(el6);
        });
        Track(__blk4);
        __blk4.Refresh();

        var el8 = new global::EclipseUI.Elements.Button();
        var h9 = (global::System.Action)(() => AddTask());
        el8.AddHandler(global::EclipseUI.Elements.Button.ClickEvent,
            () => { h9(); Invalidate(); });
        el8.Label = "添加任务";
        el0.Children.Add(el8);

        __blk10 = new global::EclipseUI.ForEachBlock<global::Examples.TodoItem>();
        el0.Children.Add(__blk10);
        __blk10.SetItems(() => Tasks);
        __blk10.SetKeySelector((global::Examples.TodoItem t) => (object?)(t.Id));
        __blk10.SetTemplate((global::Examples.TodoItem t,
            global::EclipseUI.BlockScope scope11) =>
        {
            var el12 = new global::EclipseUI.Elements.StackPanel();
            el12.Gap = 4d;
            var el13 = new global::EclipseUI.Elements.CheckBox();
            var bind14 = global::EclipseUI.UiBinding.TwoWay(
                el13,
                global::EclipseUI.Elements.CheckBox.CheckedProperty,
                readTarget: () => el13.Checked,
                writeTarget: v => el13.Checked = v,
                readSource: () => t.Done,
                writeSource: v => t.Done = v,
                invalidate: Invalidate);
            scope11.AddDisposable(bind14);
            void R1()
            {
                bind14.Refresh();
            }
            R1();
            scope11.AddRenderAction(R1);
            var el15 = new global::EclipseUI.Elements.TextBlock();
            var run16 = ((global::EclipseUI.ITextContentHost)el15).AddRun("");
            void R2()
            {
                run16.Text = $"{(t.Title)}";
            }
            R2();
            scope11.AddRenderAction(R2);
            el12.Children.Add(el13);
            el12.Children.Add(el15);
            scope11.AddRoot(el12);
        });
        Track(__blk10);
        __blk10.Refresh();
        return el0;
    }

    protected override void Render()
    {
        __bind3.Refresh();
        __blk4.Refresh();
        __blk10.Refresh();
    }
}
```

说明：

- ID 依次为 `el0`/`el1`/`el2`/`__bind3`/`__blk4`/`scope5`/`el6`/`run7`/`el8`/`h9`/`__blk10`/
  `scope11`/`el12`/`el13`/`bind14`/`el15`/`run16`；`R0`–`R2` 共享 `fnCounter`，与 `U` 同一个计数器。
- 根级 `@bind`、`@if`、`@foreach` 提升为字段；块内元素/绑定保持局部。
- `h9` 是事件处理器委托局部，包装只调用它并 `Invalidate()`。
- 组件内容示例见 §4.12.3；`content{N}` 只在有内容时分配。

## 附录 B · 生成器检查清单

1. 文件头（有命名空间时三行，否则两行）？无 `using`？所有具名类型 `global::`？文件作用域命名空间？
2. 只有字段/`Build`/`Render`/`U`？`Build` 签名正确？无 `Dispose`/构造/属性？
3. 字段提升只发生在跨方法引用？其余全局部？`null!` 初始化？
4. 编号源码顺序、不复用？`U`/`R` 共享 `fnCounter`？字段与局部分享 `idCounter`？
5. `Build` 每个动态点首次求值一次？`Render` 只调用 `U`/`Refresh`？不构造？
6. 组件顺序：登记 → 参数 → 内容 → `Mount` → 元素级属性（Class/Style/附加属性，作用于 `cmp.Root`）→ 挂父/返回 Root？
7. 块顺序：`new` → 挂父 → setter → `Track` → 显式 `Refresh`？
8. 块内资源 `scope.AddDisposable`？组件 `Adopt` + `AddDisposable`？内容 `TrackContent`？
9. 绑定：控件 `UiBinding`；组件 `PChanged` + 推送？`invalidate: Invalidate`？
10. 事件：`h{N}` 只出现一次？包装 `Invalidate`？无 `Track`？
11. 文本：静态合并？动态一个插值串？`{(expr)}` 包裹？动态 run 空串创建？
12. 组件内容：`List<Element>`？单根 `Count == 0 ? null : [0]`？`@bind` → `EUI040`？`Key` → `EUI010`？
13. `#line`：每个用户语句映射？`#line hidden` 紧跟？脚手架不映射？路径原样（反斜杠不双写）？
14. 禁止项：无 `SetValue`/`GetValue`/`Bind.TwoWay`/`static`/`try`/`using`/`async`/`#pragma`？
15. 确定性：同一输入字节相同？hint 稳定？无时间戳/GUID/字典序？
16. 失败：非法输入不抛？最小 `Build` 回退？不生成半套成员？
