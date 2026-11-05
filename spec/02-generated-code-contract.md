# 02 · 生成代码契约

> 状态：v2 定稿（2026-10-29），对应模板语义 **v4 失效重算**；S1/S2 生成形态已落地（2026-10-30），其余见 §12。
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
| `P:bind` / `P:value` | §4.4.5/§4.5 | ✅ S3（生成同新形态 + `EUI043` Warning） |
| `OnE={expr}` 控件事件 | §4.6.1 | ✅ S1 |
| 组件委托参数事件 | §4.6.2 | ✅ C4 |
| `Text` 模型文本 | §4.7 | ✅ S1 |
| `TextRuns` 模型文本 | §4.8 | ✅ S1 |
| `@if` | §4.9 | ✅ S2 |
| `@foreach` | §4.10 | ✅ S2 |
| 组件标签 | §4.12 | ✅ C4 |
| `Key` | §4.13 | ✅ S2 |
| `Ref` | §4.14 | ✅ C5（Class/Style/插槽/附加属性仍 G4/G4+） |

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

生成文件必须以以下内容开始（顺序固定；`namespace` 行仅在 code-behind 有命名空间时生成）：

```csharp
// <auto-generated> 由 EclipseUI 模板编译器生成；请勿手动修改。 </auto-generated>
#nullable enable
namespace {Namespace};
```

- `{Namespace}`：code-behind 的命名空间；无命名空间时省略 `namespace` 行。
  生成器统一使用**文件作用域命名空间**，不生成块式 `namespace { }`。
- **不得**生成任何 `using` 指令（`global using` 或普通 `using`）。
- 所有具名类型引用必须带 `global::` 前缀，包括：
  - `new` 表达式、类型实参、泛型参数、数组/列表元素类型、`EqualityComparer<T>`；
  - `Action`/`Action<T>`/`Func<T>`、`IEnumerable<T>`、`IReadOnlyList<T>`、`List<T>`、
    `IDisposable`、`String`/`Int32` 等 BCL 类型。
- 允许不带的：关键字、`var`、局部变量名、lambda 参数名、字符串字面量、`this`、数字字面量。
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
- 生成成员白名单，仅此四类：
  1. 字段：`private {Type} __{prefix}{N} = null!;`
  2. `protected override global::EclipseUI.Element Build()`
  3. `protected override void Render()`（模板含根级动态构造时）
  4. `private void U{N}()`（根级动态赋值，方法体只含一个绑定点的求值/写入）
- `Build()` 不得加 `async`、`virtual`、`sealed`、`unsafe` 或特性；必须有 `return {root};`。
- `Render()` 不得构造元素、注册事件、安装绑定、调用 `Mount()` 或 `Refresh()` 之外的块操作；
  只调用 `U{N}()`、`__bind{N}.Refresh()`、`__blk{N}.Refresh()`（按源码顺序）。
- 不得生成嵌套类型、属性、事件、构造函数、`Dispose()`、辅助方法、`#region`、注释（文件头除外）。
- 局部函数（`R{N}`）只允许出现在 `SetContent`/`SetTemplate` 的 lambda 内，
  签名固定 `void R{N}()`，不得返回值、`async`、泛型、参数。
- 类名不是合法 C# 标识符 → `EUI013`；code-behind 缺失/嵌套/非 partial 按 spec/01 §6 报错，不生成。

### 2.4 `#line` 映射

每个**包含用户表达式或字面量**的生成语句必须映射回 `.ui` 源位置：

```csharp
#line 12 "C:\Workspace\EclipseUI\samples\TodoList.ui"
        el0.Label = $"共 {(Tasks.Count)} 项";
#line hidden
```

规则：

1. `#line` 行号是 `.ui` 的 1 基行号；文件名是 Roslyn `AdditionalText.Path` 原样写入——
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
9. 列映射/精确 span 映射排 G3；v1 只保证行号。

### 2.5 生成文本禁止项

生成代码**不得**包含：

| 禁止项 | 原因 |
|---|---|
| `using` / `global using` | 依赖项目设置，违反 R7 |
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

#### 4.4.5 旧语法 `P:bind`

`P:bind={expr}` 生成与 `@bind-P={expr}` **完全相同**的文本；分析器报 `EUI043` Warning。
`expr` 为 `Signal<T>` 对象时不再有特殊形态：`Signal<T>` 不再是模板契约，按普通可写左值校验；
不通过则 `EUI037`（spec/03 §8.6）。

### 4.5 旧语法 `P:value`

`P:value` 是 `P={expr}` 的旧别名：

- 字面量值：按 §4.2 生成赋值；`EUI043` Warning。
- 表达式值：按 §4.3 生成（控件属性或组件参数）；`EUI043` Warning。
- 未实现时按 spec/01 §4.6 报 `EUI002` + 弃用说明；S3 起统一为上述形态。

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
7. 处理器签名无法静态判定时由 C# 报错；可判定的唯一匹配由分析器报 `EUI024`（C3）。

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
parentN.Children.Add(cmp0.Root);
```

规则：

1. **顺序固定**：`new` → 作用域登记 → 参数（源码顺序，静态/动态/事件混合）→ 组件内容 →
   `Mount()` → 挂到父级（根组件返回 `cmpN.Root`）。
2. 作用域登记在参数之前：
   - 组件级：`Track(cmp{N})`；
   - 块模板内：`Adopt(cmp{N}); scope{N}.AddDisposable(cmp{N});`
   - 组件内容内：`receiver.TrackContent(cmp{N});`
3. `Track(cmp)` 等价于 `Adopt` + 登记释放；块/内容内必须拆开，以便随条目/接收方释放。
4. 参数/回调必须在 `Mount()` 前完成；子组件首次 `Build()` 读取参数。
5. `cmp.Root` 在 `Mount()` 后才非空；生成器不得提前访问。
6. 组件为文档根时：登记 → 参数 → 内容 → `Mount()` → `return cmp{N}.Root;`，不挂父级。
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
| `<Slot Name="...">` / `<ContentSlot>` | 具名插槽 | G4 | `EUI031`（G4 后 `EUI022`） |
| 附加属性 `P.Attached` | 附加属性 | G4 | `EUI031` |
| 泛型标签 `<C<T>>` | 泛型组件/控件 | G4 | `EUI030` |

- 保留语法在实现前**不生成任何代码**；分析器报对应诊断，生成器跳过该节点/属性。
- 保留语法不得退化为 `SetValue`/字符串查找；实现后按 G3/G4 单独修订本文。

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

## 5. 顺序、资源与释放

### 5.1 Build 语句顺序（冻结）

| 构造 | 顺序 |
|---|---|
| 元素/控件 | `new` → 属性（源码顺序，含静态赋值/`U`/`R`/绑定/事件）→ 内容 → `parent.Children.Add` |
| 组件 | `new` → 作用域登记 → 参数（源码顺序）→ 内容 → `Mount` → `parent.Children.Add(cmp.Root)` |
| `@if` | `new` → `parent.Children.Add` → `SetCondition` → `SetContent` → 作用域登记 → `Refresh()` |
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
| `ConditionalBlock`/`ForEachBlock` | `Track(__blk{N})` | `scope{N}.AddDisposable(blk{N})` | ⛔ |
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
- `BlockScope` 由 `ConditionalBlock`/`ForEachBlock<T>` 创建并传入模板；模板只调用
  `AddRoot`/`AddRenderAction`/`AddDisposable`。
- `ConditionalBlock`/`ForEachBlock<T>` 的 setter 只保存，不自动刷新；生成代码必须显式 `Refresh()`。
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

## 9. 契约测试 T1–T18

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
| T14 | `#line` | 每个用户语句有 `#line`/`#line hidden`；行号正确；不映射脚手架 |
| T15 | 禁止项 | 生成文本不含 §2.5 的禁止 token（`SetValue`/`GetValue`/`using`/`static`/`try`/...） |
| T16 | 失败回退 | 非法输入不抛异常；输出最小 `Build`；诊断由分析器给出 |
| T17 | 可空/编译 | `Nullable=enable` 下无新增警告；`warnings-as-errors` 项目可编译 |
| T18 | 运行时语义 | Build 首评一次；Render 重算；块刷新；Keyed 复用；双向绑定；事件失效 |

- T1–T17 是编译器/golden 测试；T18 是宿主/运行时集成测试（S1–S3）。
- golden 文本按本章的形态比对，不比对空白行；`#line` 路径可按测试临时目录参数化。

## 10. `EUI045` 与 code-behind 边界

`EUI045` 守护生成成员与 code-behind 的冲突；计划 C3 实现（spec/03 §11.1）。

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

## 11. 与旧契约的差异入口

- v1 的 `UiWatch`/`Subscribe`/`Signal<T>`/`IINCC`/依赖分析/`subs`/`itemSubs`/`upd` 全部作废；
  仅保留在 design/03 §3.5.3 与 design/15 §15.13.4 的历史台账中。
- v1 的 `SetValue`/`GetValue`/`Bind.TwoWay` 生成形态 ⛔；`@bind` 必须用 `UiBinding`。
- v1 的"首次赋值 + 订阅"顺序被"Build 首评 + Render/Refresh 重算"取代。
- v1 的 `subs{N}.Add(...)`/`itemSubs{N}.Add(...)` 被 `scope{N}.AddDisposable(...)` 取代。
- v1 的 `SetContent(Func<List<IDisposable>, Element[]>)` 被
  `SetContent(Action<BlockScope>)` 取代；`SetTemplate(Func<T, BlockScope>)` 被
  `SetTemplate(Action<T, BlockScope>)` 取代。
- 旧语法的生成文本与新语法相同，差异只在分析器 `EUI043`。

## 12. 实现偏差台账 D42–D74

**S1 已收敛（2026-10-30）、S2 已收敛（2026-10-30）、S3 已收敛（2026-11-02，控件侧）、C1 已收敛（2026-11-02，目录派生）、C3 已收敛（2026-11-03，字面量转换器/使用点诊断/保留语法）、C4 已收敛（2026-11-05，组件标签）**，
下表中标 ✅ S1/✅ S2/✅ S3/✅ C1/✅ C4 的条目已关闭；其余偏差按 S4/C3 余项/C5/G 阶段收敛。台账之外的差异视为实现缺陷。
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
| D54 | ✅ C4：组件 `@bind-P` 生成「`{P}Changed` 回调赋值（build-once）+ 推送 `U{N}`/`R{N}`」；组件目标判定/内容体内 `@bind` 禁用已落地（EUI040）；内层推送形态与 §4.3.3 一致。回调 `{P}Changed` 的**类型**与参数严格匹配仍静态不可判，由 C# 编译错误兜底（D73） | §4.4.3/§4.4.4 | ✅ C4 |
| D55 | ✅ S1：事件生成 `h{N}` 委托局部 + 包装内 `Invalidate()` | §4.6 | ✅ S1 |
| D56 | ✅ C4：组件默认内容 build-once 落地——`List<Element>` 累加、单根 `content{N}.Count == 0 ? null : content{N}[0]`、内容子组件 `receiver.TrackContent`、内容内 `@if`/`@foreach` 展开为普通 C# 语句、内容内动态属性为 Build 快照、内容内 `@bind`/`Key`/裸文本分别报 EUI040/EUI010/EUI006 | §4.12.3 | ✅ C4 |
| D57 | `EUI045` 未实现 | §10 | C3 |
| D58 | ✅ S1：hint name 为 `{ClassName}.{stableHash}.g.cs`（SHA-256 前 16 hex） | §2.1 | ✅ S1 |
| D59 | ✅ S2：`ITextContentHost.AddRun(string)` 已实现；`TextBlock` 实现该接口，运行时不再需要手建 `TextRun` | §4.8/§7 | ✅ S2 |
| D60 | ✅ S2：`ForEachBlock.UpdateChildren` 仅在根序列实际变化时改 `Children` | spec/03 §7.3 | ✅ S2 |
| D61 | ✅ S2：样例与 README 已迁移到普通 CLR 状态；`Signal<T>` 仅留作运行时遗留原语与历史章节说明，不参与模板生成 | 本文/spec/03 | ✅ S2 |
| D62 | ✅ C2：`EUI040` 的目标可绑性此前只来自手写目录 `UiPropDef.Bindable`；C1 派生目录已对每个模板可见属性给出符号级 `CanWrite`（public 可写 + `{Name}Property`）且证明派生 ⊇ 手写标注；C2 起分析器改读派生 `CanWrite` | spec/01 §4.2 | ✅ C2 |
| D63 | ✅ C1：运行时声明特性落地 `Runtime/UiAttributes.cs`（`UiContentKind`/`[UiTag]`/`[UiContent]`/`[UiIgnore]`/`[UiParameter]`/`[UiSlot]`），内置控件加标注；标注纯元数据，不改生成形态 | design/14 §14.4.6/§14.4.4 | ✅ C1 |
| D64 | ✅ C1+C2：`Compiler.Metadata` 目录模型 + Roslyn 符号扫描器（`UiCatalogFactory.Build`）落地，纯函数、不抛异常，问题进 `Problems`/`HiddenTags`；C2 删除手写目录，生成器读派生目录 | design/14 §14.5/§14.10.2/§14.12.2 | ✅ C2 |
| D65 | ✅ C2：`EclipseUI.Compiler.Metadata` 源码链接进生成器程序集（排除 Metadata 的 `Polyfills.cs`），诊断码常量经单一定义流转；测试项目只引用生成器，避免重复类型身份 | design/14 §14.10.1/§14.12.2 | ✅ C2 |
| D66 | ✅ C1：`UiContentKind` 与 `UiContentModel` 数值一一对应，有断言测试 | design/14 §14.4.4 | ✅ C1 |
| D67 | ✅ C1：失效级别守卫白名单收敛为唯一例外 `Element.IsHitTestVisible`；`Panel.ClipToBounds` 失效级别 `None`→`Render` | design/14 §14.11.1 | ✅ C1 |
| D68 | ✅ C1+C2：派生目录属性项 6→17，差集逐条钉进快照测试；C2 起模板实际变宽生效 | design/14 §14.12 | ✅ C2 |
| D69 | ✅ C2：8 输入事件从静态宿主 `InputEvents` 迁入 `Element`（19 处调用改名，删静态类），`CheckBox` 补 `CheckedChanged`；事件表面 1 → 9 个事件名（37 事件项） | design/14 §14.4.3/§14.12.2 | ✅ C2 |
| D70 | ✅ C1：`UiElementDef`/`UiCatalog` 手写结构相等（record 对字典成员按引用比较，会破坏 §14.10.3 管道值比较）；`CatalogValueEq` Ordinal 稳定排序 + unchecked 哈希（netstandard2.0 无 `System.HashCode`） | design/14 §14.10.3 | ✅ C1 |
| D71 | `[UiIgnore]` 与 `[UiParameter]`/`[UiSlot]` 同时出现时 `[UiIgnore]` 生效并按 **EUI028** 记问题；design/14 §14.6.1 只写「不得」，没为这条分配诊断码，EUI028 原文只覆盖「参数声明非法」。C4 未处理（沿用 EUI028，不复用新码）；需要细化时另起新码并先记台账 | design/14 §14.6.1/§14.9.1 | 开放 |
| D72 | ✅ C2（语义修订）：golden 快照落成仓库内文件 `TestData/BuiltInSurface.golden.txt`（`ECLIPSEUI_UPDATE_SURFACE_GOLDEN=1` 再生）；原文「固定目录从快照反序列化」不采纳——快照改为扫描结果的**渲染评审门**（显式再生成 + diff 强制评审），纯结构测试各自现扫 `Compilation`（全扫实测 45 ms） | design/14 §14.11.1/§14.12.2 | ✅ C2 |
| D73 | ✅ C4（残余风险登记）：组件 `@bind-P` 只静态校验「存在 `{P}Changed` 委托参数」（EUI040），不校验其实参类型与 `P` 匹配——恰与 spec/01 §4.14 「缺失/类型不符 → EUI040」中的「类型不符」不可严格一致。类型不匹配由生成代码的 C# 编译错误兜底（经 `#line` 指回 `.ui`）；不要在运行时静默吞掉。若未来 Roslyn 类型推断下放到扫描期，可再收紧 | §4.4.3 规则 1 / spec/01 §4.14 | ✅ C4 |
| D74 | ✅ C5（部分收敛 + 残余登记）：① 使用点 EUI019/EUI020/EUI021 落地——`UiElementDef.IsFromReferencedAssembly`（新记录字段，进值相等/哈希）区分来源，只有引用程序集控件的 `def.Problems` 在用点暴露，源项目控件仍落 EUI002（design/14 §14.9.1 v2 口径）；② **EUI026 声明处诊断仍无编译期出口**：扫描侧 problem 只进 `HiddenTags`/`def.Problems`/`catalog.Problems`，源项目控件的声明错误靠使用点 EUI002 兜底、引用程序集靠 D74 ① 暴露——声明处报错（属性/特性 span）需要生成器把源声明定位到语法，留后续切片 | design/14 §14.5.4/§14.9.1/§14.12.5 | ✅ C5 ① / 开放 ② |

- 每项收敛必须同时删除对应的 v3 路径；不得两套模型并存。
- 新增偏差必须登记编号（从 D74 起）并注明目标阶段；不得只改代码不改台账。
- 台账编号与 design/03 §3.5.4 保持一致；两边冲突以本文为准并回写 design/03。

## 13. 变更规则

1. 修改任何生成形态前，先改 spec/01（若涉及语法）或 spec/03（若涉及语义），再改本文。
2. 每个新增/删除的生成形态必须同步：§0.5 索引、§4 形态、§9 测试、§12 台账。
3. 编号/顺序/前缀属于契约；变更必须提升契约大版本（§8.3），并在 §12 记录迁移方式。
4. 不得在本文保留"临时生成形态"；未实现的语义标 S1–S5/C1–C6/G3/G4，不得默默生成近似形态。
5. 契约评审分开回答：完整性与可实现性、可解析性与语义自洽性；两者都通过才定稿。
6. 本文与 spec/03 冲突时：求值时机/失效语义按 spec/03，文本形态/顺序/编号按本文；
   评审必须消解冲突，不得长期并存。

## 14. 关键决策 K1–K18

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
6. 组件顺序：登记 → 参数 → 内容 → `Mount` → 挂父/返回 Root？
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
