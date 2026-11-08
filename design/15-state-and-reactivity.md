# 15. 状态与反应式契约（详细设计 · v3 历史）

> 状态：**已停止（2026-10-29）**。v4 换轨后，本文仅作 v3 历史记录，**不再作为实现依据**。
> 模板反应式正本：[spec/03](../spec/03-reactivity-and-binding.md)；语法见 [spec/01](../spec/01-template-syntax.md)；
> 生成文本形态见 [spec/02](../spec/02-generated-code-contract.md)；控件/组件的符号契约见 [14](./14-control-template-contract.md)；
> 组件实例与元素树见 [04](./04-runtime-and-elements.md)；总体管线见 [02](./02-architecture.md)。
>
> **v4 换轨**：普通 CLR 状态 + 组件级 `Invalidate`/`Render` + 保留元素树 + 块刷新；双向用 `{P}Changed`。
> 本文的 `[UiState]`/`[UiObservable]`/`[UiComputed]`、`IUiObservable`、`UiWatch`、依赖分析、可观察参数，
> 以及 EUI014/EUI034–EUI036/EUI038–EUI039/EUI041–EUI042/EUI044 全部废止（EUI044 亦停用，未复用编号）。
>
> **保留本文的目的**：记录 v3 的设计取舍、审计与迁移来源；§15.13.4 的 D23–D36 是 v3 代码差距台账。
> 与 spec/01–spec/03 冲突时，以 spec 为准，本文不回写。
>
> **不变量（历史）**：R1–R6 见 14 §14.3；v3 新增 R7–R9（§15.5.7）；v4 的 R1–R10 见 spec/02 §1。

## 15.0 定位、术语与冻结线

### 15.0.1 本文管什么

| 本文定义 | 本文不定义（去哪看） |
|---|---|
| `[UiState]` / `[UiObservable]` / `[UiComputed]` 的声明规则与生成形态 | `.ui` 的词法/EBNF/标签与属性语法（[03](./03-template-engine.md)） |
| `IUiObservable` / `UiWatch` / `UiBinding` 的运行时契约 | 控件如何暴露 StyledProperty/事件/内容模型（[14](./14-control-template-contract.md)） |
| `P={expr}`、`@bind-P`、文本、`@if`、`@foreach` 的反应式语义 | measure/arrange/绘制/hit test（[05](./05-layout-rendering-input.md)） |
| 依赖分析、路径可观察性、订阅作用域与代码生成形态 | LSP 补全/悬停的 UI 呈现（[06](./06-ide-support.md)） |
| 对象自变化的可观察边界与逃生舱 | 主题/样式/ControlTemplate（G4） |
| EUI034–EUI044 的诊断口径 | 控件元数据诊断 EUI001–EUI033（[14 §14.9](./14-control-template-contract.md)） |

### 15.0.2 术语

| 术语 | 含义 |
|---|---|
| 状态属性 | 由 `[UiState]` / `[UiObservable]` 生成的、读写走 `Signal<T>` 的公开 CLR 属性 |
| 可观察（observable） | 写入时能同步通知订阅者；模板分析可静态识别其写入路径 |
| 可观察路径 | 从 `this`/item 根出发，逐段都是可观察属性（或可观察集合）的成员访问链 |
| 依赖 | 表达式对状态属性、`Signal<T>` 成员、可观察路径的读取 |
| 定向更新函数（updater） | 生成器为某个绑定点生成的 `void U()`，重新求值并写目标 |
| 订阅作用域 | `Track`、`@if` 内容表、`@foreach` item scope、块依赖表之一；决定何时释放 |
| 一次性表达式 | 没有可观察依赖，或含不可分析依赖而退化为 Build 时求值一次的表达式 |
| 写契约 | 只有规定的写入方式会通知；绕过写契约的修改不保证 UI 更新 |

### 15.0.3 冻结线

以下决定冻结；改义必须先新增状态项、诊断映射与迁移期，不得"先生效、后补文档"：

1. **模板作者只写普通类型**：`[UiState] partial class` + `public partial int Count { get; set; }`；
   模板里写 `Count`，不写 `.Value`、不写 `Signal<T>`、不写 `INotifyPropertyChanged`。
2. **`P={expr}` 是单向反应式**：表达式的全部依赖可观察时，属性跟随变化；含不可观察/不可分析依赖时整条表达式一次性求值，
   报 `EUI036`/`EUI038`，**不做部分订阅**。
3. **`@bind-P={expr}` 是双向绑定**：`expr` 必须是可写可观察路径（或 `Signal<T>` 成员）；目标必须是可写控件属性或可观察组件参数。
4. **依赖由编译期分析、写入由信号通知**：运行时不追踪读取、不做 diff、不做代理、不轮询、不反射。
5. **对象自变化只承认写契约**：生成属性 setter、根对象替换、嵌套可观察对象、`ObservableCollection<T>`/INPC/INCC、
   `Version` + `[UiComputed]`。其它修改（`List<T>.Add`、私有字段、数组元素）不保证更新。
6. **`Signal<T>` 是底层原语，不是模板作者的默认 API**：保留给非 partial 状态、第三方互操作与框架内部；
   模板语言不为普通属性生成 `.Value` 重写。
7. **`@foreach` 条目在 item 引用变化时重建**：同 Key 但 item 引用不同（引用类型）或值不同（值类型）时，
   释放旧 scope 并重建条目，禁止闭包捕获旧 item。
8. **组件参数通过可观察 partial 属性反应式更新**：无参数 diff、不重跑 `Build()`；插槽/内容仍 build-once。

### 15.0.4 状态标记

本文用阶段标记实现状态；**规范本身对目标状态生效**，代码落后项必须出现在 §15.13.4：

| 标记 | 含义 |
|---|---|
| S1 | 状态生成器 + 运行时原语（`IUiObservable`/`UiWatch`/`Signal<T>` COW） |
| S2 | 单向反应式 `P={expr}` + 文本/`@if`/`@foreach` + 依赖分析 |
| S3 | 双向 `@bind-P` + 组件可观察参数 + R3 修正 |
| S4 | 嵌套/集合/INPC/INCC/`[UiComputed]` |
| S5 | 性能、AOT、线程断言、弃用清理与最终审计 |
| ⛔ | 明确拒绝，不实现 |

## 15.1 目标、非目标与选择依据

### 15.1.1 目标

- **G1 普通类型书写**：模板作者只写 `partial` 属性；不接触包装类型、字符串属性名或通知样板。
- **G2 可静态分析**：每个绑定点在编译期得到明确的依赖路径；不可分析的表达式明确退化为一次性并报警。
- **G3 零运行时魔法**：无反射、无代理、无动态 codegen、无 IL weaving；AOT/裁剪安全。
- **G4 细粒度更新**：一次写入只重跑订阅了该路径的 updater；不重建模板、不 diff 元素树。
- **G5 可预测的作用域**：每个订阅有唯一所有者；组件/条目/分支卸载即释放。
- **G6 双向可控**：`@bind-P` 通过公开 CLR setter 回写；不绕过 `StyledProperty`（R3）。
- **G7 可诊断**：非可观察依赖、非法声明、非法绑定在 `.ui` 或声明处得到稳定诊断，不静默陈旧。
- **G8 可迁移**：现有 `Signal<T>` 模板继续工作；旧语法有弃用期与机械迁移路径。

### 15.1.2 非目标

- 不做深度对象图 diff、快照比较或自动轮询。
- 不做运行时代理（`DispatchProxy`/`RealProxy`/源生成透明代理）。
- 不做 Fody/IL weaving（理由见 §15.1.4）。
- 不做跨线程状态同步、锁或自动 UI 线程调度（S5 只做调试断言）。
- 不做自动 memoization/依赖缓存；`[UiComputed]` 默认每次求值。
- 不把 `P={expr}` 自动变成双向；不猜测用户意图。
- 不支持 `struct`/`record`/泛型 `[UiState]` 类（v1；见 §15.12）。

### 15.1.3 候选方案对比

| 方案 | 模板写法 | 依赖发现 | 运行时开销 | AOT | 编译期诊断 | 结论 |
|---|---|---|---|---|---|---|
| 显式 `Signal<T>`（现状） | `count.Value` / 裸名重写 | 编译器扫描信号名 | 低 | 好 | 只能查信号名 | 保留为底层原语 |
| 手写 `INotifyPropertyChanged` | `Count` | 运行期事件 + 字符串 | 事件参数/字符串 | 好 | 弱（拼写/漏发） | 仅第三方适配 |
| Fody/IL weaving | `Count` | 只能织 setter | 低 | 风险 | 对 `.ui` 生成器不可见 | 拒绝（§15.1.4） |
| Vue 式 Proxy | `Count` | 运行期读取追踪 | 代理/依赖集合 | 差 | 无 | 拒绝 |
| **partial 属性 + 源生成 Signal** | **`Count`** | **编译期语义模型** | **低（直接委托）** | **好** | **强（EUI034–EUI044）** | **采用** |

### 15.1.4 为什么不用 Fody

Fody 的 setter 织入只能解决"写入通知"，解决不了本设计的三件核心事：

1. **读依赖发现**：模板表达式 `A + B` 的依赖必须在编译期得到；Fody 在 IL 层看不到 `.ui` 生成器的分析，也无法把依赖告诉它。
2. **跨程序集/第三方对象**：Fody 只处理被织入的程序集；模板读外部程序集的 POCO 时仍然没有通知。
3. **可诊断性**：IL weaving 发生在 C# 编译之后，`.ui` 的源生成器无法判断某属性是否会被织入，无法给出"不可观察"警告。
4. **工具链确定性**：AOT/裁剪/增量编译/调试映射都多一层不可见变换；R1/R6 的边界被破坏。

结论：即使采用 Fody，仍然需要 `IUiObservable`/`UiWatch`/依赖分析；它只省掉生成器的一小段，却引入不可静态分析的边界。拒绝。

### 15.1.5 为什么保留 `Signal<T>`

- 非 partial 场景：第三方类型、动态构造的状态、框架内部状态。
- 显式原语：需要手动组合、手动订阅/释放、或在 C# 中直接写 UI 时，`Signal<T>` 是最小 API。
- 兼容：现有模板与测试大量使用 `Signal<T>`；S2–S4 期间不破坏。
- 模板默认不暴露 `.Value`：普通属性与信号在模板里都写裸名；生成器按成员类型决定订阅方式。

## 15.2 状态声明契约

### 15.2.1 `[UiState]`：整类可观察

```csharp
using EclipseUI;

[UiState]
public partial class CounterState
{
    public partial int Count { get; set; }
    public partial string Name { get; set; }

    public CounterState()
    {
        Name = "guest";      // 非默认值在构造函数里赋值（partial 属性不能带初始化器）
    }
}
```

规则：

1. 类型必须是**顶层、非泛型、非静态、非抽象、非 record、非 struct 的 class**，且 `partial`。
2. 允许继承普通基类；**不得**继承另一个 `[UiState]` 类（无状态继承）。
3. 允许出现在 `Component` 的 code-behind 上（组件自身可观察）；**不得**出现在 `Element` 派生类型上
   （控件的属性模型是 `StyledProperty`）。
4. 类中所有 **public partial 实例自动属性**（`public partial T P { get; set; }`）成为状态属性：
   - 必须有 public getter 与 public setter；
   - 不得是 `init`/`required`/`static`/`abstract`/`virtual`/`override`/`new`/索引器/`ref` 返回；
   - 不得带初始化器（C# 限制，实测 `CS8050`）；非默认值在构造函数里赋；
   - 不得由用户提供实现体；实现由生成器提供；
   - 不得是 `Signal<T>` 或 `ISignal` 类型（避免双层包装）。
5. 非 partial 的普通属性/字段/方法原样保留，**不可观察**；模板读取它们会得到 `EUI036`。
6. 类不得手工实现 `IUiObservable`，不得声明 `Subscribe(string, Action)` 同签名方法；
   不得声明名为 `__eui`、`__EuiState` 的成员（生成器保留名）。
7. 一个类只能标一次 `[UiState]`；`[UiState]` 类内不得再对属性标 `[UiObservable]`（冗余，EUI034）。

### 15.2.2 `[UiObservable]`：按属性 opt-in

用于只想暴露少量属性的类（尤其是不能整体标 `[UiState]` 的既有类型）：

```csharp
public partial class Prefs
{
    [UiObservable] public partial bool AutoSave { get; set; }
    [UiObservable] public partial int Interval { get; set; }
    public string Raw { get; set; } = "";   // 不可观察
}
```

规则：

1. 属性必须是 `partial` 自动属性、public get/set、无初始化器、无用户实现体。
2. 类必须是顶层、非泛型、非静态、非 record、非 struct 的 `partial class`。
3. 同一类可以有多个 `[UiObservable]`；生成器合并为一个 `IUiObservable` 实现。
4. 类不得手工实现 `IUiObservable`；不得在 `[UiState]` 类上使用（EUI034）。
5. 属性类型不得是 `Signal<T>`/`ISignal`。
6. 没有 `[UiObservable]` 的 partial 属性不会被实现；若它也没有用户实现，编译器会报"partial 属性缺少实现"——
   生成器对这类声明报 EUI034，避免用户看到晦涩的 CS 错误。

### 15.2.3 `[UiComputed]`：派生只读属性

```csharp
[UiState]
public partial class Cart
{
    public partial decimal Price { get; set; }
    public partial int Quantity { get; set; }

    [UiComputed(nameof(Price), nameof(Quantity))]
    public decimal Total => Price * Quantity;
}
```

规则（S4）：

1. 只允许在 `[UiState]` 类中；属性必须是 public、**只读**（无 setter）、非 partial、非 static、非索引器。
2. 构造函数参数 `params string[] dependsOn`（等价于 `DependsOn` 属性）必须非空；
   每个名字必须是同类的：
   - 状态属性（public partial get/set）；
   - 另一个 `[UiComputed]` 属性；
   - `Signal<T>` 字段/属性。
3. 不得依赖自己、不得形成环、不得重复；未知/不可观察依赖 → EUI035。
4. 生成器**不缓存**计算值；每次读取都执行 getter。`[UiComputed]` 的 `Subscribe` 映射为对底层依赖的合并订阅。
5. getter 体内是否只读取 `dependsOn` 声明的成员，v1 **不做语义校验**（只信任声明）；这是 §15.12 的开放问题。
   读了未声明的状态不会更新，属于用户违约，不是静默 bug（文档必须写明）。
6. 计算属性可被模板读取、被父组件单向绑定；**不可**作为 `@bind` 源（无 setter）。

### 15.2.4 生成形态（冻结）

`[UiState]`（或含 `[UiObservable]` 的类）生成一个同命名空间的 partial 部分，形态如下：

```csharp
// CounterState.EuiState.g.cs（生成器输出；#nullable enable）
partial class CounterState : global::EclipseUI.IUiObservable
{
    private sealed class __EuiState
    {
        public readonly global::EclipseUI.Signal<int> Count = new(default!);
        public readonly global::EclipseUI.Signal<string> Name = new(default!);
    }

    private readonly __EuiState __eui = new __EuiState();

    public partial int Count
    {
        get => __eui.Count.Value;
        set => __eui.Count.Value = value;
    }

    public partial string Name
    {
        get => __eui.Name.Value;
        set => __eui.Name.Value = value;
    }

    global::System.IDisposable global::EclipseUI.IUiObservable.Subscribe(
        string property, global::System.Action handler)
    {
        if (property is null) throw new global::System.ArgumentNullException(nameof(property));
        if (handler is null) throw new global::System.ArgumentNullException(nameof(handler));

        switch (property)
        {
            case nameof(Count): return __eui.Count.Subscribe(handler);
            case nameof(Name): return __eui.Name.Subscribe(handler);
            default:
                throw new global::System.ArgumentOutOfRangeException(
                    nameof(property), property, "Unknown observable property.");
        }
    }
}
```

生成规则：

- 类声明只写 `partial class X` + `: global::EclipseUI.IUiObservable`；**不写基类**，避免与模板生成的
  `: global::EclipseUI.Component` 冲突（实测：partial 类可在不同部分分别声明基类与接口）。
- 可访问性（`public`/`internal`）与用户声明一致；不重复 `sealed`/`static` 等修饰符。
- 属性实现的可访问性与定义一致；`partial` 关键字必须保留。
- 成员顺序：按属性名 `Ordinal` 排序，保证文件/部分顺序变化不影响输出（R6）。
- `[UiObservable]` 类只生成被标记属性的字段/实现与 switch 分支；`[UiState]` 生成全部合格 public partial 属性。
- `[UiComputed]` 不生成字段；在 switch 中生成合并订阅：

```csharp
case nameof(Total):
    return global::EclipseUI.UiSubscription.Combine(
        __eui.Price.Subscribe(handler),
        __eui.Quantity.Subscribe(handler));
```

  依赖另一个 computed 时，生成器递归展开到最终信号（环由 EUI035 拦截）。
- 没有合格属性时仍生成类与 `Subscribe`（所有名字都抛），并报 EUI034 Warning「`[UiState]` 类没有可观察属性」。
- 生成器不生成构造函数；字段初始化器先于用户构造函数执行，构造函数里的赋值可安全写入信号。
- 生成代码只引用公开运行时 API（`Signal<T>`/`IUiObservable`/`UiSubscription`），无反射、无字符串查找。

### 15.2.5 初始化、生命周期与线程

- **初始化**：状态属性初值为 `default(T)`；非默认值必须在用户构造函数中赋。partial 属性不能带初始化器
  （实测 C# 13/.NET 10：`error CS8050`）。
- **构造期写入**：信号在 `__eui` 字段初始化后可用；构造函数赋值会通知订阅者，但此时通常没有订阅者。
- **生命周期**：状态对象不被组件 `Dispose` 拥有；组件只释放自己创建的订阅（§15.3.7）。状态对象可以比组件活得久，
  也可以被多个组件共享；共享时所有观察者都会收到通知（UI 线程）。
- **线程**：所有读写、订阅、释放都在 UI 线程；不提供锁。跨线程写入是未定义行为（S5 增加 Debug 断言）。
- **可空性**：`Signal<T>` 允许 `null`；生成字段用 `default!` 初始化。模板文本对 null 的 `ToString()` 结果为空串
  （C# 插值语义）；需要非空约束时在构造函数/属性 setter 中自行校验。
- **相等性**：`Signal<T>` 用 `EqualityComparer<T>.Default` 短路；写入相等值不通知。引用类型按引用相等，
  `record` 按值相等；可变对象内部变化不触发（§15.6）。

### 15.2.6 声明限制与诊断

| 声明 | 结果 |
|---|---|
| `[UiState]` 类非 partial / 嵌套 / 泛型 / static / abstract / record / struct | EUI034 |
| `[UiState]` 继承另一个 `[UiState]` 类 | EUI034 |
| `[UiState]` 标在 `Element` 派生类型 | EUI034 |
| 状态属性非 public / 无 get / 无 set / init / required / static / indexer / ref | EUI034 |
| 状态属性带初始化器 | EUI034（同时 C# CS8050） |
| 状态属性已有实现体 | EUI034 |
| 状态属性类型是 `Signal<T>` / `ISignal` | EUI034 |
| 手工实现 `IUiObservable` 或 `Subscribe(string, Action)` | EUI034 |
| 声明 `__eui` / `__EuiState` 成员 | EUI034 |
| `[UiState]` 类内再标 `[UiObservable]` | EUI034 |
| `[UiObservable]` 属性非 partial / 无实现 / 类非 partial | EUI034 |
| `[UiObservable]` 与 `[UiContent]`/`[UiSlot]` 同属性 | EUI034 |
| `[UiComputed]` 有 setter / 非 `[UiState]` 类 / 未知依赖 / 环 | EUI035 |
| `[UiComputed]` 依赖 `[UiObservable]` 之外的普通属性 | EUI035 |
| `[UiState]` 类无合格属性 | EUI034 Warning |

### 15.2.7 与控件、组件、参数的关系

- **控件（`Element` 派生）**不使用 `[UiState]`：控件属性继续走 `StyledProperty<T>` + 公开 CLR 包装属性（14.4.2）。
  控件属性变化通过 `Element.PropertyChanged`/失效引擎通知，模板赋值走公开 setter（R3）。
- **组件（`Component` 派生）**可以标 `[UiState]`：code-behind 的 public partial 属性即可观察状态。
  这是组件参数可反应式更新的基础（§15.7）。
- **组件参数**：`[UiParameter]` 只声明"可传入"；可观察性由 `partial` + `[UiState]`/`[UiObservable]` 决定。
  - `[UiParameter] public partial string Title { get; set; }` + 类 `[UiState]` → 可观察参数；
  - `[UiParameter] public string Title { get; set; }` → 一次性参数（现有语义）；
  - `[UiParameter] public Signal<string> Title { get; set; }` → 传统信号参数（兼容，不推荐新代码）。
- **内容/插槽参数**（`[UiContent]`/`[UiSlot]`）永远是 build-once，不得可观察（EUI034）。

### 15.2.8 与 `Signal<T>` 的关系

| 场景 | 推荐 | 说明 |
|---|---|---|
| 普通页面/组件状态 | `[UiState]` partial 属性 | 模板写裸名；编译期依赖分析 |
| 少量既有类型的属性 | `[UiObservable]` | 不改变类型整体语义 |
| 框架内部/动态状态 | `Signal<T>` | 手动订阅与释放 |
| 第三方 `INotifyPropertyChanged` | 直接读取 + `UiWatch` | S4 适配；无需包装 |
| 跨组件共享状态 | `[UiState]` 实例作为参数/字段 | 订阅独立、无所有权转移 |

`Signal<T>` 成员在模板里仍写裸名（值上下文取 `.Value`），但**普通状态属性不取 `.Value`**；
两者的订阅都由生成器负责。不要在同一属性上同时用两种机制。

## 15.3 运行时契约

### 15.3.1 `IUiObservable`

```csharp
namespace EclipseUI;

public interface IUiObservable
{
    /// <summary>订阅指定属性的变化；属性名大小写敏感，未知属性抛 ArgumentOutOfRangeException。</summary>
    IDisposable Subscribe(string property, Action handler);
}
```

- 只按**属性名**订阅；生成代码用 `nameof`，运行时不反射、不扫描。
- `handler` 在写入线程同步调用；同一次写入中按订阅注册顺序调用。
- 未知属性抛 `ArgumentOutOfRangeException`；`property`/`handler` 为 null 抛 `ArgumentNullException`。
- 返回的 `IDisposable` 幂等；释放后不再调用；重复释放无副作用。
- 不提供 `GetValue`/属性列表：读取用普通 getter；路径求值由 `UiWatch` 的 getter 委托完成。
- 实现者必须保证：**写入相等值不通知**（生成器用 `Signal<T>` 的相等短路）。

### 15.3.2 `UiWatch` 与 `UiWatchSegment`

```csharp
namespace EclipseUI;

public readonly struct UiWatchSegment
{
    public UiWatchSegment(string property, Func<object?, object?> getter);
    public string Property { get; }
    public Func<object?, object?> Getter { get; }
}

public static class UiWatch
{
    public static IDisposable Subscribe(
        object root,
        UiWatchSegment[] path,
        Action onChange,
        bool watchCollection = false);

    public static IDisposable SubscribeSignal<T>(
        Signal<T> signal,
        Action onChange,
        bool watchCollection = false);

    public static IDisposable SubscribeProperty(
        IUiObservable observable,
        string property,
        Action onChange);
}
```

- `root` 是 `this`（组件/code-behind）或 `@foreach` 的 item 对象；第一段的 `Getter` 接收 `root`。
- `path` 至少一段；每段的 `Property` 是 `nameof(属性)`，`Getter` 从上一段对象取下一段对象。
- 最后一段只用于订阅，`Getter` 不会被 `UiWatch` 调用（保留给测试/调试）；因此叶属性值不会装箱。
- `UiWatch` 不执行用户表达式，只调用显式 getter 委托；无反射。
- `watchCollection`：路径中任一中间/叶对象实现 `INotifyCollectionChanged` 时挂接集合变更；
  生成器在静态类型可能为集合时传 `true`（§15.4.7）。
- 订阅建立时**不触发** `onChange`；调用方先做一次初始求值/赋值，再订阅。
- `onChange` 在属性/集合变化时同步调用一次；中间对象被替换时先重接后续段，再调用一次。
- 释放幂等；释放后不再调用；所有事件处理器都被摘除。
- 中间对象为 `null` 时停止向下订阅；表达式求值仍按 C# 语义进行（可能抛 NRE）。
  中间对象从 null 变为非 null 时，重新建立后续订阅并调用一次 `onChange`。
- 对象同时实现 `IUiObservable` 与 `INotifyPropertyChanged` 时优先 `IUiObservable`。
- 非可观察中间对象：生成器不应生成这种路径（分析阶段退化）；运行时遇到则后续订阅为空操作，
  不抛异常（防御性，不掩盖生成器 bug——Debug 下可断言）。

`UiWatch` 的实现状态：

```csharp
sealed class WatchState : IDisposable
{
    Func<object?>? _root;              // 实际用 object root 捕获；此处置为 Func 便于未来扩展
    UiWatchSegment[] _path;
    Action _onChange;
    bool _watchCollection;
    IDisposable?[] _propertySubs;      // 每段一个
    INotifyCollectionChanged? _collection;
    NotifyCollectionChangedEventHandler? _collectionHandler;
    bool _disposed;
    bool _rewiring;
}
```

- 每次属性通知：从触发段开始重新求值；若中间对象引用变化，释放后续订阅并重接；
  最后调用 `onChange`。不比较叶值（叶值由 updater 重新求值）。
- 集合通知：直接调用 `onChange`（`@foreach` 重新 `Refresh`；文本/属性 updater 重新求值）。
- 订阅/重接在 UI 线程同步完成；不支持并发。

### 15.3.3 路径算法

```
Attach(root):
  obj = root
  for i in 0..path.Length-1:
      if obj is null: return
      sub[i] = SubscribeProperty(obj, path[i].Property, () => OnSegmentChanged(i))
      if watchCollection && obj is INotifyCollectionChanged c:
          attach c.CollectionChanged -> OnCollectionChanged
      if i < path.Length-1:
          obj = path[i].Getter(obj)     // 取下一段
      else:
          obj = null                    // 叶段不再取
```

- 每个对象最多挂一个属性订阅 + 一个集合订阅；不重复挂接。
- `OnSegmentChanged(i)`：丢弃 `sub[i+1..]` 与集合订阅，从 `i+1` 重新 `Attach`；调用 `_onChange()`。
  即使值未变也调用一次（INPC 可能发冗余通知；updater 内部由目标属性短路/相等判断抵消）。
- `OnCollectionChanged`：调用 `_onChange()`，不重接属性订阅。
- 释放：逆序 `Dispose` 所有订阅、摘除集合事件；`_disposed` 后所有回调直接返回。

### 15.3.4 `UiBinding`：双向绑定

```csharp
namespace EclipseUI;

public static class UiBinding
{
    public static IDisposable TwoWay<T>(
        Element element,
        StyledProperty<T> property,
        Func<T> readTarget,
        Action<T> writeTarget,
        Func<T> readSource,
        Action<T> writeSource,
        Func<Action, IDisposable> subscribeSource);

    public static IDisposable TwoWayComponent<T>(
        IUiObservable target,
        string targetProperty,
        Func<T> readTarget,
        Action<T> writeTarget,
        Func<T> readSource,
        Action<T> writeSource,
        Func<Action, IDisposable> subscribeSource);
}
```

语义（两种重载一致）：

1. 初始：`writeTarget(readSource())`——**源值胜出**（Build 时目标被源初始化）。
2. 订阅源变化：`subscribeSource(OnSourceChanged)`；源变化 → 若 `readTarget()` 与源值不等，写目标。
3. 订阅目标变化：
   - 控件：`element.PropertyChanged`，过滤 `ReferenceEquals(changed, property)`；
   - 组件：`target.Subscribe(targetProperty, h)`。
   目标变化 → 若源值与 `readTarget()` 不等，写源。
4. `_syncing` 重入护栏：任一侧写入时置位，同步通知回来直接返回；`try/finally` 复位。
5. 相等判断用 `EqualityComparer<T>.Default`；相等不写，避免无意义通知。
6. 目标写入必须走公开 CLR setter（`writeTarget` 由生成器生成 `v => el.P = v`）；不得用 `SetValue/GetValue` 绕过属性引擎（R3）。
7. 源写入走公开 CLR setter（状态属性）或 `Signal<T>.Value`；不得写私有字段。
8. 释放：先释放源订阅，再摘除目标事件/订阅；幂等；释放后不再回写。
9. 异常：用户 getter/setter 抛异常直接传播；已建立的订阅在 `finally` 中释放（避免半绑定泄漏）。
10. 目标/源类型必须一致（忽略可空注解）；不做用户转换/装箱转换。

`TwoWayComponent` 要求 `target` 是 `IUiObservable`；非可观察组件参数在编译期已被 EUI040 拦截，
运行时防御性抛 `ArgumentException`（不静默降级为一次性）。

### 15.3.5 `Signal<T>` 写路径（COW）

现状（[Signals.cs:57](../src/EclipseUI.Runtime/Signals.cs#L57)）每次写都 `subs.ToArray()`，产生一次数组分配。
S1 改为 copy-on-write：

```csharp
public sealed class Signal<T> : ISignal
{
    private T _value;
    private Subscription[]? _subs;   // 只在订阅/退订时复制

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            var subs = _subs;
            if (subs is null) return;
            for (int i = 0; i < subs.Length; i++)
                if (!subs[i].IsDisposed)
                    subs[i].Handler();
            // 写入路径零分配；订阅中新增的订阅者不接收本次通知
        }
    }

    public IDisposable Subscribe(Action handler)
    {
        var sub = new Subscription(this, handler);
        var old = _subs;
        if (old is null) { _subs = new[] { sub }; return sub; }
        var next = new Subscription[old.Length + 1];
        Array.Copy(old, next, old.Length);
        next[^1] = sub;
        _subs = next;
        return sub;
    }
}
```

- 写路径不分配、不加锁；订阅/退订复制数组（O(n)，订阅数通常很小）。
- 通知期间退订：`IsDisposed` 置位，当前迭代跳过；`_subs` 已被替换，后续写入不再包含。
- 通知期间新增订阅：当前迭代使用旧数组，新订阅不接收本次通知；下一次写入生效。
- 通知异常：不捕获，按注册顺序中断；UI 帧边界负责最终异常处理（G4）。
- `SubscriberCount` 返回 `_subs?.Length ?? 0`；用于泄漏测试/DevTools。
- `ISignal.BoxedValue` 保留；`Signal<T>` 不实现 `IUiObservable`（无属性名语义）。

### 15.3.6 订阅所有权

| 位置 | 所有者 | 释放时机 |
|---|---|---|
| 根属性/根文本/根 `@bind` | 组件 `Track(...)` | `Component.Dispose()` |
| `@if` 内容内的订阅 | `ConditionalBlock._contentSubscriptions` | 分支切换/块 Dispose |
| `@if` 条件依赖 | `ConditionalBlock._depSubscriptions` | 重新 `SetCondition`/块 Dispose |
| `@foreach` 集合依赖 | `ForEachBlock._depSubscriptions` | 重新 `SetItems`/块 Dispose |
| `@foreach` item 内订阅/子块 | `BlockScope.Disposables` | 条目移除/条目重建/块 Dispose |
| 组件实例 | 调用方作用域 | 同上 |
| `UiBinding` | 绑定所在作用域 | 同上 |

- 生成代码不得把订阅存静态字段、单例或 `ConditionalWeakTable`；R6/R8。
- 组件内部的订阅登记在组件自身；父作用域只负责 `Dispose` 组件实例。
- 手工从元素树移除元素不会释放绑定（Element 无 Unmount）；v1 限制，G4 补。

### 15.3.7 线程、异常与重入

- 全部订阅/通知/释放都在 UI 线程；`UiWatch` 与 `Signal<T>` 不加锁。
- 通知是同步的：一次写入可能在返回前触发多个 updater/绑定回写；不做帧级批处理（S5 评估）。
- 重入：updater 内写状态是允许的；用户必须避免无限递归。`UiBinding` 的 `_syncing` 只阻断双向回环。
- 异常：getter/setter/updater 抛异常直接传播；订阅对象保持已建立状态（除非 `UiBinding` 构造失败，按 §15.3.4 释放）。
- 跨线程：未定义；S5 在 Debug 下用 `Environment.CurrentManagedThreadId` 断言（不引入 Dispatcher）。

### 15.3.8 新增公开 API 汇总（冻结）

```csharp
public interface IUiObservable { IDisposable Subscribe(string property, Action handler); }

public readonly struct UiWatchSegment { /* Property, Getter */ }

public static class UiWatch
{
    IDisposable Subscribe(object root, UiWatchSegment[] path, Action onChange, bool watchCollection = false);
    IDisposable SubscribeSignal<T>(Signal<T> signal, Action onChange, bool watchCollection = false);
    IDisposable SubscribeProperty(IUiObservable observable, string property, Action onChange);
}

public static class UiBinding
{
    IDisposable TwoWay<T>(Element element, StyledProperty<T> property,
        Func<T> readTarget, Action<T> writeTarget,
        Func<T> readSource, Action<T> writeSource,
        Func<Action, IDisposable> subscribeSource);

    IDisposable TwoWayComponent<T>(IUiObservable target, string targetProperty,
        Func<T> readTarget, Action<T> writeTarget,
        Func<T> readSource, Action<T> writeSource,
        Func<Action, IDisposable> subscribeSource);
}

public static class UiSubscription
{
    IDisposable Combine(params IDisposable[] items);
}

[AttributeUsage(AttributeTargets.Class)]     public sealed class UiStateAttribute : Attribute;
[AttributeUsage(AttributeTargets.Property)]  public sealed class UiObservableAttribute : Attribute;
[AttributeUsage(AttributeTargets.Property)]  public sealed class UiComputedAttribute : Attribute
{
    public UiComputedAttribute(params string[] dependsOn);
    public string[] DependsOn { get; }
}
```

- 特性定义在 `EclipseUI.Runtime`，命名空间 `EclipseUI`；生成器按全 metadata 名识别（14.4.6）。
- `UiSubscription.Combine` 幂等；释放全部后重新抛出首个异常（其余仍被释放）。
- 所有 API 都是 AOT 安全、无反射、无用户代码执行。

## 15.4 模板语义

### 15.4.1 反应式位置总表

| 写法 | 求值 | 反应式条件 | 不满足时 | 订阅 |
|---|---|---|---|---|
| `P="literal"` | Build | 否 | — | 无 |
| `P={expr}` | Build + 依赖变化 | 全部依赖可观察 | 一次性 + EUI036/038 | 一次 watcher（多段路径合并） |
| `@bind-P={expr}` | Build + 双向 | 源可写可观察、目标可写 | EUI037/040 | 源 watcher + 目标事件 |
| 文本 `{expr}` | Build + 依赖变化 | 全部依赖可观察 | 一次性 + EUI036/038 | `Text` 整段一个；`TextRuns` 每个动态段一个 |
| `@if (expr)` | Build + 条件变化 | 条件依赖可观察 | 静态（EUI036/038） | 条件 watcher → 块 |
| `@foreach (var x in expr)` | Build + 集合变化 | 集合路径可观察 | 静态 + EUI041 | 集合 watcher → `Refresh` |
| `Key={expr}` | 条目创建 | **禁止**依赖可观察状态 | EUI039 | 无 |
| `OnE={handler}` | Build 订阅事件 | 否（事件触发时读当前值） | — | 无 |
| `Ref={field}`（G3） | Build | 否 | — | 无 |

“全部依赖可观察”是**整条表达式的全有或全无**规则，不是逐项订阅。

本节代码块只示意生成语义（哪些路径、何时调用 updater）；精确生成文本（`global::`、`UiWatchSegment`、
`itemSubsN`、`Array.Empty`、`#line` 等）以 [spec/02](../spec/02-generated-code-contract.md) 为准。

### 15.4.2 依赖分类与 all-or-nothing

分析器把表达式里的每个**根读取**分类：

| 类别 | 例子 | 可观察 | 处理 |
|---|---|---|---|
| 状态属性 | `Count`（`[UiState]` 生成） | 是 | 路径订阅 |
| 可观察对象路径 | `Vm.Address.City`（Vm/Address/City 均可观察） | 是 | 路径订阅 |
| `Signal<T>` 成员 | `tasks`、`showCount` | 是 | `signal.Subscribe` |
| `Signal<T>.Value` 后的读取 | `tasks.Value.Count` | 是（信号根覆盖） | 订阅信号；值图内部不单独订阅 |
| `ObservableCollection<T>` 成员 | `Tasks.Count` | 是 | 属性订阅 + INCC |
| `INotifyPropertyChanged` 对象 | `thirdParty.Name` | 是（S4） | 路径订阅 |
| 普通实例属性/字段 | `_cache.Count`、`this.Raw` | 否 | EUI036 → 一次性 |
| 方法调用 | `GetName()`、`Items.First()` | 否 | EUI038 → 一次性 |
| 索引器/动态/lambda | `Items[0]`、`dynamic` | 否 | EUI038 → 一次性 |
| 静态成员/常量/`nameof`/`typeof` | `App.Title`、`nameof(X)` | 无依赖 | 不订阅，不报警 |

规则：

1. 表达式至少有一个可观察依赖，且所有实例读取都可观察 → 生成订阅。
2. 表达式含任何不可观察实例读取/不可分析结构 → **整条表达式一次性**，不生成任何订阅；
   若表达式不是纯常量/静态，报 EUI036（普通读取）或 EUI038（不可分析结构）Warning。
3. 纯常量/静态表达式（`Label={DateTime.Now}`、`{App.Title}`）按一次性处理，不报警。
4. 不为"部分可观察"生成半套订阅：`{Count + GetName()}` 不会因 `Count` 变化而更新；
   要更新就把 `GetName()` 改成可观察属性/`[UiComputed]`，或接受一次性并抑制警告。
5. `Signal<T>.Value` 是值边界：之后读取 `List.Count`/`Dictionary[...]` 等不单独形成依赖；
   信号替换触发更新。若值内部集合会被原地修改，按 §15.4.7/EUI041 处理。

### 15.4.3 `P={expr}`（单向反应式）

- 目标：控件属性（`{Name}Property` 存在、可写）或组件参数（public setter）。
- 生成：

```csharp
void U0() => el0.Width = Vm.Width;
U0();
Track(UiWatch.Subscribe(this, new UiWatchSegment[]
{
    new UiWatchSegment(nameof(MyComponent.Vm), o => ((MyComponent)o).Vm),
    new UiWatchSegment(nameof(VmState.Width), o => ((VmState)o).Width),
}, U0));
```

- 求值一次并赋值，再订阅；首次赋值失败则 Build 失败，不建立订阅。
- 同一表达式的全部状态路径合并为**一次** `UiWatch.Subscribe`，每条路径一个 `UiWatchSegment`；
  同一路径去重；多个依赖共享同一个 `U0`（spec/02 §4.3.2/K1）。
- 表达式里的方法调用（如 `Vm.Count.ToString()`）按 §15.4.2 属不可分析结构：整条一次性、报 EUI038，
  **不会**生成上面的订阅；需要格式化就在 code-behind 提供可观察属性或用文本插值 `{Vm.Count}`。
- 目标写入走公开 CLR setter；`Element.SetValue` 负责相等短路与失效（R3）。
- 目标为 `Signal<T>` 组件参数（legacy）：表达式类型必须是 `Signal<T>`，传对象，不订阅（子组件负责）；
  不能把 `.Value` 传进去。
- 目标为普通组件参数（非可观察）：仍生成 updater（源码变化时赋新值），但组件不会重跑 `Build`；
  若表达式有可观察依赖，报 EUI044 Warning。
- 目标为可观察组件参数：updater 写入参数，子组件内部 watcher 更新，不重建。
- 目标为只读属性/无 `{Name}Property`：`P=` 报 EUI019（已进入目录）或 EUI002（未暴露）；
  `@bind` 报 EUI040。S2 起统一按此口径，不再落到 C# 编译错误。
- 目标为 `Text` 内容目标（`Label`）且元素还有文本内容 → EUI033（重复目标），不生成两套 updater。

### 15.4.4 `@bind-P={expr}`（双向）

语法（03 v3 冻结）：

```ebnf
bind-attribute ::= "@bind-" attr-name-base
attr-name-base ::= ident ( "." ident )?          (* 附加属性 G4 预留 *)
```

- 只允许 `{expression}`；字面量 → EUI037。
- 目标 `P`：
  - 控件：public 可写属性 + `{Name}Property`；不得是事件/内容/保留属性；
  - 组件：`[UiParameter]` + 可观察 partial 属性；
  - 其它 → EUI040。
- 源 `expr` 必须满足：
  1. 是**可写可观察路径**：`this.Count`、`Vm.Count`、`item.Count`；每段可观察；叶有 public setter；
  2. 或者是 `Signal<T>` 字段/属性（特殊形式，便于迁移）；
  3. 不得是方法调用、索引器、`?.`、静态成员、常量、只读属性、`[UiComputed]`；
  4. 类型与目标属性类型一致（忽略可空注解）。
  违反 → EUI037。
- item 模板内：源路径必须 rooted at item 变量；引用组件级状态 → EUI042。
- 生成（控件）：

```csharp
Track(global::EclipseUI.UiBinding.TwoWay(
    el0,
    global::EclipseUI.Elements.CheckBox.CheckedProperty,
    readTarget: () => el0.Checked,
    writeTarget: v => el0.Checked = v,
    readSource: () => Vm.ShowCount,
    writeSource: v => Vm.ShowCount = v,
    subscribeSource: h => global::EclipseUI.UiWatch.Subscribe(this, new global::EclipseUI.UiWatchSegment[]
    {
        new global::EclipseUI.UiWatchSegment(nameof(MyComponent.Vm), o => ((MyComponent)o).Vm),
        new global::EclipseUI.UiWatchSegment(nameof(VmState.ShowCount), o => ((VmState)o).ShowCount),
    }, h)));
```

- 生成（组件目标）：

```csharp
Track(global::EclipseUI.UiBinding.TwoWayComponent(
    cmp0, nameof(UserCard.Title),
    readTarget: () => cmp0.Title,
    writeTarget: v => cmp0.Title = v,
    readSource: () => Vm.Name,
    writeSource: v => Vm.Name = v,
    subscribeSource: h => global::EclipseUI.UiWatch.Subscribe(this, path, h)));
```

- 信号源：`readSource: () => show.Value`、`writeSource: v => show.Value = v`、`subscribeSource: h => show.Subscribe(h)`。
- 初始方向：源 → 目标；随后双向。控件用户操作走公开 setter → `PropertyChanged` → 源；
  组件内部写入参数 → `IUiObservable` 通知 → 源。
- 两边的写入都由相等判断短路；`_syncing` 防回环。
- 同一属性不得同时写 `P={expr}` 和 `@bind-P` → EUI033 类重复目标（S2 统一）。

### 15.4.5 文本

- `Text` 模型：整段文本一个 updater；动态段依赖并集；静态段不订阅。
- `TextRuns` 模型：每个动态段一个 `AddRun("")` + updater；静态 run 不订阅。
- 依赖规则同 §15.4.2；非可观察读取报 EUI036；不可分析结构 EUI038。
- 文本 updater 只写目标字符串/run；不重建元素。
- `{Tasks.Count}`：`Tasks` 是可观察集合属性时，订阅属性 + INCC；`Add/Remove/Clear` 后 Count 文本更新。
- `{tasks.Value.Count}`：`tasks` 是 `Signal<T>` 时订阅信号；值内部集合原地修改不触发（EUI041 如适用）。
- 文本插值不支持格式说明符（spec/01 §2.6）；需要格式写 `.ToString("F2")`。
- 文本 updater 的订阅归内容作用域（§15.3.6）；`@if` 切换释放。

### 15.4.6 `@if`

- 条件必须 `bool`；生成 `ConditionalBlock`。
- 条件依赖可观察 → `SetCondition(() => expr, subscribeDeps)`；否则 `SetCondition(() => expr)`（静态）。
- 生成 API（新增）：

```csharp
public void SetCondition(Func<bool> condition, Func<Action, IDisposable>? subscribe = null);
```

  `subscribe` 在 `SetCondition` 内调用一次，返回的订阅存入 `_depSubscriptions`；旧的先释放。
  兼容旧 `params ISignal[]` 重载（标记 Obsolete，S5 评估移除）。
- 分支内容 build-once per activation；切换销毁旧元素、重建新元素；内容订阅释放（现有行为）。
- 条件变化本身只重算 `condition()`；不重跑 Build。
- item 模板内：条件可依赖 item 的可观察属性；条目重建时内容重建；同 Key 同 item 引用不改内容，
  但属性变化会通过 item 订阅更新。**不再需要把数据版本并入 Key**。
- 条件表达式含不可观察/不可分析依赖 → 静态条件 + EUI036/038；不生成订阅。

### 15.4.7 `@foreach` 与集合

- 集合表达式允许：
  - 可观察属性路径：`Tasks`、`item.Children`；
  - `Signal<T>` 成员：`tasks`；
  - 上述路径的叶类型实现 `IEnumerable<TItem>`。
- 不允许：方法调用/LINQ/索引器/`??`/强制转换（EUI007 或 EUI038）。
- 生成（新增 API）：

```csharp
public void SetItems(Func<IEnumerable<T>> itemsGetter, Func<Action, IDisposable>? subscribe = null);
```

  `subscribe` 负责属性+集合变化；兼容旧 `params ISignal[]` 重载（Obsolete）。
- 集合变化：
  - 属性替换/`Signal<T>` 替换 → `Refresh`；
  - `INotifyCollectionChanged` 的 Add/Remove/Reset → `Refresh`；
  - `List<T>`/数组原地修改 → **不通知**；静态类型为具体非 INCC 集合时 EUI041 Warning；
  - 接口类型（`IList<T>`/`IReadOnlyList<T>`/`IEnumerable<T>`）不强制警告，但运行时只有实现 INCC 才观察内容变化；
    文档写明。
- 嵌套 `@foreach`：内层集合可以是 item 的可观察属性/集合；内层块登记在 item scope；
  条目移除/重建时释放。方法调用/LINQ 仍禁止。
- `@foreach` 自身是布局透明的（G3 D16 修复）；item 模板多根元素仍由块容器承载，布局透明后由父容器排布。
- 空集合/单元素不特殊；`Key` 缺失时用 item 自身作为 key。
- 集合表达式为 `Signal<List<T>>` 时，`SetItems` 的 `subscribe` 用 `UiWatch.SubscribeSignal(tasks, Refresh, watchCollection: true)`；
  列表原地修改不触发（EUI041）。

### 15.4.8 `Key` 与条目复用（v3 修订）

- `Key` 只允许在 item 模板第一个元素根；表达式不得引用可观察状态/`Signal<T>`/可观察路径（EUI039）。
  允许 item 的普通属性、常量、静态成员；`Key={t.Id}` 中 `Id` 必须是普通（非 partial/非 INCC）属性。
- 默认 key = item 自身。
- 复用条件（新增）：**key 相等且 item 身份相等**：
  - 值类型：`EqualityComparer<T>.Default.Equals(oldItem, newItem)`；
  - 引用类型：`ReferenceEquals(oldItem, newItem)`。
- 同 key + 同 item → 复用结构，重跑 `Scope.Updaters`（根层绑定点）；item 内部订阅保持。
- 同 key + 不同 item → 释放旧 `BlockScope.Disposables`，重建条目（重新执行模板）。
  **禁止**闭包捕获旧 item；事件处理器/绑定不会指向旧对象。
- key 不同/新增 → 新建；旧条目未复用 → 释放。
- 重复 key 按出现顺序队列复用（现有）。
- `Key` 为 null → 共享空键；同 key 且同 item 才复用；不同 item 会重建。仍建议避免 null。
- 表：条目复用时什么会重跑（v3）：

| 模板内容 | key 同 + item 同 | key 同 + item 不同 | key 不同/新增 |
|---|---|---|---|
| 元素结构 | 不重建 | **重建** | 重建 |
| 根层文本/`P={item.X}`/`@bind` | 依赖 item 属性时按订阅更新；否则不重跑 | 重建时求值 | 新建时求值 |
| 根层 `Scope.Updaters` | 重跑 | 重建时不再需要 | 新建时求值 |
| 事件处理器 | 捕获当前 item（同引用） | 重建并绑定新 item | 重建 |
| `@if (item.X)` | item 属性变化时重算（若可观察） | 重建 | 重建 |
| 嵌套 `@foreach` | item 属性/集合变化时更新 | 重建 | 新建 |
| 组件参数 updater | 重跑（同 item 但参数表达式可能依赖外部状态） | 重建组件 | 新建 |

- 条目重建会丢失元素局部状态（焦点、滚动、动画）；Key 的职责是**重排身份**，不是"数据版本"。
  需要保留局部状态时，让 item 对象稳定、通过可观察属性改数据，而不是替换 item 对象。
- `ForEachBlock.Dispose` 必须释放所有仍存在条目的 `BlockScope.Disposables`（修复 D18），
  并清空 `_entries`；避免组件卸载后 item 订阅泄漏。
- `ForEachBlock` 的复用判断增加 item 存储与比较；复杂度仍 O(变动数 + 列表长度)。

### 15.4.9 事件

- `OnE={handler}` 在 Build 时 `AddHandler`；不产生状态订阅。
- 处理器内读取状态属性/`Signal<T>` 时得到**当前值**（getter/`.Value`），因为事件触发在 UI 线程。
- 处理器写入状态 → 通知 updater → 下一次绘制更新；不要求 handler 内手动刷新。
- 事件处理器表达式不参与依赖分析；EUI036/038 不适用于事件体（除非事件体语法非法，由 C# 报错）。
- 事件处理器捕获 item：条目重建时重新绑定新 item；同 item 引用复用则捕获当前对象，安全。
- 组件事件 = 委托参数（14.6.2）；无变化。

### 15.4.10 组件

- 参数赋值：`P={expr}` 在 Build/源变化时写参数；`@bind-P` 双向。
- 可观察参数（`partial` + `[UiState]`/`[UiObservable]`）：
  - 父源变化 → updater 写参数 → 子 `IUiObservable` 通知 → 子模板 updater；不重跑 `Build`；
  - 子内部写入参数 → 父 `@bind` 回写；子自己的模板也更新。
- 非可观察参数：`P={expr}` 仍可在源变化时赋值，但子 UI 不刷新；EUI044 Warning。
  `@bind` 目标非可观察 → EUI040 Error。
- `Signal<T>` 参数（legacy）：父传对象；子模板订阅；父不生成 updater；`@bind` 不适用（类型不匹配）。
- 参数无 diff；子模板结构 build-once；插槽/内容 build-once。
- 父组件 `Dispose` → 释放绑定/观察订阅；子组件由同一作用域 `Dispose`。
- 组件自身 `[UiState]` 的非参数状态只对子模板可见；父不能通过标签访问。
- 组件作为 item：条目重建创建新组件实例；同 item 复用保留组件实例，参数 updater 重跑。
- 组件模板内的 `@if`/`@foreach` 用子组件状态做依赖，符合 §15.4.2；item 模板内的组件参数不能引用父组件状态（EUI014/EUI042）。

### 15.4.11 表达式作用域与信号重写

| 位置 | 可见名字 | 重写 |
|---|---|---|
| 根 | code-behind 实例成员（`this`） | 状态属性不改写；`Signal<T>` 裸名在值上下文取 `.Value`；目标为 `Signal<T>` 参数时保持对象 |
| `@if` 内容 | 同上 | 同上 |
| `@foreach` item | item 变量 + 实例成员 | item 成员不改写（普通 CLR 属性/字段）；`Signal<T>` 成员取 `.Value` |
| `Key` | item 变量 + 常量/静态/普通成员 | 不订阅；不取 `.Value`（禁止信号） |

- `count.Value` 与 `count` 对 `Signal<T>` 在值上下文等价（现有兼容）；状态属性**没有** `.Value`，写 `Count.Value` 是 C# 错误。
- 生成器按目标类型决定是否把 `Signal<T>` 包装成 `.Value`；状态属性是普通 CLR 类型，直接读取。
- item 变量遮蔽：C# 规则；不要用与状态属性同名的 item 变量（EUI036 可能误报/歧义）。
- 模板不声明局部变量/方法/类型；复杂逻辑放 code-behind。
- `nameof`/`typeof`/常量不产生依赖。

### 15.4.12 旧语法与迁移

| 旧写法 | v3 行为 | 迁移 |
|---|---|---|
| `P={signal}` | 变成反应式（信号依赖） | 意图若是反应式，保留；若是一次性快照，改用普通只读属性/方法并接受 EUI036 或显式 code-behind |
| `P:value={expr}`（旧版未实现） | EUI043 Warning，按 `P={expr}` 语义处理 | 改 `P={expr}` |
| `P:bind={signal}` | EUI043 Warning，按 `@bind-P={signal}` 处理 | 改 `@bind-P={signal}`；或把状态改成 partial 属性后 `@bind-P={Count}` |
| `@if (signal)` / `@foreach (... in signal)` | 继续支持，响应信号 | 可保留；新代码建议状态属性 |
| 文本 `{signal.Value}` | 继续支持 | 可简写 `{signal}` |
| item 模板引用 code-behind 信号 | EUI014 Error（收窄：仅禁止组件级状态） | 改成 item 可观察属性或组件参数 |
| `Signal<T>` 组件参数 | 继续支持（legacy） | 新代码用可观察 partial 参数 |
| `Bind.TwoWay(...)` 手写调用 | 保留 public API（兼容），生成代码不再使用 | 改 `UiBinding.TwoWay`；旧重载标记 Obsolete（S3） |

- `:bind`/`:value` 在 S3 进入 Warning，S5 评估改为 Error/移除；至少保留一个发布周期。
- `Signal<T>` 字段不需要迁移；它与 `[UiState]` 可以共存（不同属性）。
- 旧模板中 `P={signal}` 从"一次性"变"反应式"是有意行为变化；发布说明必须列出。

### 15.4.13 生成代码形态（S2/S3）

| 模板 | 生成代码（简化） |
|---|---|
| `Label={Vm.Name}` | `void U0() => el0.Label = Vm.Name; U0(); Track(UiWatch.Subscribe(this, path, U0));` |
| `Label={GetName()}` | `el0.Label = GetName();`（一次性；方法调用 → EUI038，无 updater/订阅） |
| `{tasks.Value.Count}`（文本插值） | `void U0() => el0.Label = $"{(tasks.Value.Count)}"; U0(); Track(UiWatch.SubscribeSignal(tasks, U0, watchCollection: true));` |
| `@bind-Checked={Done}` | `Track(UiBinding.TwoWay(el0, CheckBox.CheckedProperty, () => el0.Checked, v => el0.Checked = v, () => Done, v => Done = v, h => UiWatch.Subscribe(this, path, h)));` |
| `@if (ShowCount) { ... }` | `blk0.SetCondition(() => ShowCount, h => UiWatch.Subscribe(this, path, h)); blk0.SetContent(subs0 => ...); Track(blk0);` |
| `@foreach (var t in Tasks) { ... }` | `blk0.SetItems(() => Tasks, h => UiWatch.Subscribe(this, path, h, watchCollection: true)); blk0.SetKeySelector(t => t.Id); blk0.SetTemplate(t => ...); Track(blk0);` |
| item `@bind-Checked={t.Done}` | `UiBinding.TwoWay(el1, ..., () => t.Done, v => t.Done = v, ...);`，绑定登记在 item scope |
| 组件参数 `Title={Vm.Name}` | `cmp0.Title = Vm.Name;` + 源变化 updater；若参数可观察则子模板更新 |
| `@bind-Title={Vm.Name}`（组件） | `UiBinding.TwoWayComponent(cmp0, nameof(UserCard.Title), ..., ...)` |

- 所有生成类型 `global::` 限定；不依赖 `ImplicitUsings`（修复 D22）。
- 变量/函数编号按源码遍历顺序固定；路径数组按首次出现顺序；属性名用 `nameof`。
- 表达式行带 `#line` 映射；生成器不重复报告 C# 类型错误。
- 生成代码不写 `SetValue/GetValue`（控件目标用公开 setter）；`Element.SetValue` 仍是属性引擎内部实现。

### 15.4.14 订阅作用域（与 15.3.6 对应）

- 根：`Track(...)`；`@if` 内容：`subs.Add(...)`；`@if` 条件：`SetCondition` 内部；
- `@foreach` 集合：`SetItems` 内部；item：`BlockScope.Disposables`；
- 组件：调用方 `Track(cmp)`；组件内部 `Track` 归组件；
- 绑定：与所在位置的 updater 同作用域。
- 生成器必须在代码注释/测试中可追踪每个 `IDisposable` 的所有者；泄漏测试断言 `SubscriberCount == 0`。

## 15.5 依赖分析与代码生成

### 15.5.1 合成表达式宿主

分析不执行用户代码；生成器把表达式放进一个**同类型的合成方法**里，用 Roslyn 语义模型解析：

```csharp
// 仅存在于分析用的 syntax tree，不进入最终输出
partial class MyComponent
{
    private object __EuiEval_0(TodoItem t) => (t.Title + Count);
}
```

- 对根表达式：参数列表为空，`this` 是 code-behind 实例；对 item 表达式：参数名与 item 变量一致，类型由集合推断。
- 合成方法名以 `__EuiEval_` 开头；若与用户成员冲突 → EUI034（保留前缀）。
- 合成 tree 加入 `Compilation.AddSyntaxTrees` 得到 `SemanticModel`；分析完不保留。
- 表达式语法错误/未解析符号：跳过依赖分析，不生成订阅；表达式仍按原文本进入生成代码，由 C# 报错（不在 `.ui` 上叠加假警告）。
- 合成方法只用于分析，绝不进入最终生成文件；不改变用户程序集。
- 每个表达式的分析结果缓存于当前生成轮（不跨轮静态缓存，R6/D14）。

### 15.5.2 依赖分类算法

```
Classify(expr, scope):
  roots = []                                       // 每个独立根读取
  walk expr:
    - identifier/this.Member/obj.Member:
        resolve symbol
        if static/const/enum/nameof/typeof: continue
        if method invocation: roots += Opaque(method); continue
        if indexer: roots += Opaque(indexer); continue
        if symbol is Signal<T> field/property: roots += Signal(symbol); continue
        if symbol is state property or [UiObservable]/[UiComputed]: roots += Path(symbol); continue
        if symbol is INPC property (S4): roots += Path(symbol); continue
        if symbol is ordinary instance property/field: roots += Plain(symbol); continue
    - member chain: build PathSegment[] while each receiver type is observable
  if roots has Opaque and expression is in reactive position: EUI038
  if roots has Plain: EUI036
  if any Plain/Opaque: no subscriptions (all-or-nothing)
  else: subscriptions from Signal/Path roots
```

- 根识别以**符号**为准，不以文本正则匹配；字符串字面量、注释、同名局部变量不误判（替代 D10）。
- `this` 可显式/隐式；`base.Member` 按普通成员处理；泛型/可空/转换不改变根分类。
- 路径段的可观察性：
  - 状态属性/`[UiObservable]`/`[UiComputed]`/INPC 属性 → 可观察；
  - `Signal<T>.Value` → 值边界，后续不作为独立路径段（信号根覆盖）；
  - `INotifyCollectionChanged` 对象上的 `Count`/`Item` → 由集合通知覆盖；
  - 普通属性段 → 该路径不可观察（EUI036）；
  - 方法/索引器/`dynamic` → EUI038。
- `?.`：支持空传播；路径段仍按上述分类；叶为 null 时表达式按 C# 求值。
- 条件表达式 `a ? b : c`：依赖为三者并集；`??` 同理；不可分析操作数按规则报警。
- 字符串插值：拆成多个子表达式分别分析；依赖并集。
- `nameof`/`typeof`/常量折叠不产生依赖。
- `is`/`as`/强制转换：保留路径；转换失败按 C# 语义。
- lambda/匿名方法：整体视为 Opaque（EUI038），因为其执行时机不确定。
- `await`/`yield`/`with`/集合表达式：Opaque（EUI038）。

### 15.5.3 路径与目标校验

- 对 `P={expr}`：
  - 目标属性可写、存在 `{Name}Property`（控件）或 public setter（组件参数）；否则 EUI019/EUI040；
  - 表达式类型可隐式转换为目标类型；生成赋值；类型错误由 C# 报（`#line`）。
- 对 `@bind-P`：
  - 目标按 15.4.4；源路径每段可读、叶可写、类型一致；否则 EUI037/EUI040；
  - 源路径根的 item 约束（EUI042）；
  - 目标/源不得是同一路径（`@bind-Checked={Checked}` 自绑）→ EUI037（无意义回环）；
  - 目标为 `Text` 内容目标且元素有文本内容 → EUI033。
- 对 `@if`：条件类型 `bool`；否则 C# 报错（可静态判定时 EUI024 类似）。
- 对 `@foreach`：集合类型 `IEnumerable<TItem>`；`TItem` 推断；否则 EUI007。
- 对 `Key`：不得有可观察依赖；不得是字面量；位置正确；否则 EUI010/EUI039。
- 对 `[UiComputed]` 依赖：名字解析到同类成员；可观察；无环；否则 EUI035。

### 15.5.4 去重、顺序与确定性

- 依赖路径按表达式出现顺序收集，按（根符号 + 段名序列）去重；同一路径只订阅一次。
- 生成 watcher 的顺序 = 首次出现顺序；变量/函数编号按模板遍历顺序固定。
- 状态生成器属性顺序按名称 `Ordinal`；诊断顺序按 span 排序。
- 不遍历哈希表产生输出；不依赖文件系统顺序（R6）。
- 同一输入（code-behind + `.ui` + 目录）生成相同文本；golden 测试断言。

### 15.5.5 错误容忍

- 表达式无法解析/符号未解析：不生成订阅，不报 EUI036/038 假阳性；生成代码保留原表达式，C# 报错。
- 目标类型未知：跳过绑定生成，保留赋值/绑定占位，C# 报错。
- 增量生成器不得因单个模板/状态类异常而崩溃（R5）；异常转为诊断（EUI034 类）并继续其他文件。
- 生成器不执行用户代码、不加载用户程序集（R1）。

### 15.5.6 分析成本

- 每个模板一次 `Compilation.AddSyntaxTrees`（批量合成方法可合并到一个 tree，减少 semantic model 重建）。
- 每个表达式一次 `SemanticModel.GetTypeInfo`/符号遍历；不跑控制流/数据流分析（S5 可选）。
- 结果只在当前生成轮缓存；不跨轮保存静态字典（D14）。
- 目标：样例项目增量生成时间相对现有不增加 > 20%（S5 实测；不预先承诺）。

### 15.5.7 新增不变量 R7–R9

- **R7**：生成的绑定/订阅代码只调用公开运行时 API（`IUiObservable`、`UiWatch`、`UiBinding`、`Signal<T>`、元素公开访问器）；
  控件属性写入必须经过公开 CLR setter，不得绕过属性引擎。
- **R8**：每个生成订阅都有唯一作用域所有者；不得存静态字段；释放幂等；组件/条目/分支卸载后无残留。
- **R9**：依赖分析不执行用户代码、不加载用户程序集；生成文本只依赖输入（目录 + code-behind 签名 + 模板），确定性可复现。

R7–R9 与 14 的 R1–R6 一起作为评审门槛；违反的实现不得合入。

## 15.6 对象自变化

### 15.6.1 写契约

只有以下写入会通知：

1. **生成状态属性 setter**：`obj.Count = 1`（`[UiState]`/`[UiObservable]`）。
2. **根对象替换**：`Vm = new VmState()`（`Vm` 是状态属性/`Signal<T>`）；
   watcher 重接新对象的路径。
3. **嵌套可观察对象写**：`Vm.Address.City = "x"`（`Address`、`City` 都是可观察属性）。
4. **`ObservableCollection<T>` 变更**：`Add`/`Remove`/`Clear`/`Move`/`Replace`（INCC）。
5. **`INotifyPropertyChanged` / `INotifyCollectionChanged`** 第三方对象（S4）。
6. **`Version` + `[UiComputed]`**：批量修改后 `Version++`，模板读取 `[UiComputed]` 派生值；
   或替换根对象。
7. **`Signal<T>.Value` 赋值**（底层原语）。

### 15.6.2 不保证更新

| 修改方式 | 结果 |
|---|---|
| `List<T>.Add/Remove/Clear`（`T` 是普通 List） | 不通知；`@foreach` 不刷新；EUI041（静态可判定时） |
| `T[]` 元素赋值/`Array.Resize` | 不通知 |
| `Dictionary`/`HashSet` 原地修改 | 不通知 |
| 普通 CLR 属性/字段赋值（非状态属性） | 不通知；EUI036（模板读取时） |
| 私有字段/自动属性 backing field | 不通知 |
| 可变对象内部修改（同一引用） | 不通知 |
| `record with` 产生新对象但未赋给状态属性 | 不通知 |
| 静态状态/线程局部 | 不通知（不支持） |
| 构造函数/字段初始化器中的赋值 | 无订阅者；Build 读取当前值，不需要通知 |

### 15.6.3 逃生舱

- **根替换**：不可变数据/记录的整体替换是最可靠的方式；watcher 自动重接。
- **`Version` + `[UiComputed]`**：
  ```csharp
  [UiState]
  public partial class Feed
  {
      public partial int Version { get; set; }
      public List<Post> Posts { get; } = new();       // 普通属性，不可观察

      [UiComputed(nameof(Version))]
      public int PostCount => Posts.Count;

      public void Add(Post p) { Posts.Add(p); Version++; }
  }
  ```
  模板读 `{PostCount}`；`Version++` 触发 updater。`Posts` 本身不暴露给模板（或只读且不响应）。
- **`ObservableCollection<T>`**：需要逐项增删的列表；`@foreach` 自动跟踪。
- **INPC/INCC 适配**：第三方模型；`UiWatch` 直接订阅，无需包装。
- **`Signal<T>`**：手动组合/框架内部；显式写出 `.Value` 或裸名。
- **自定义组件**：把复杂数据变更封装在组件内部，通过可观察参数/回调暴露。

### 15.6.4 `[UiComputed]` 细节

- 不生成字段/信号；getter 每次读取都执行。
- `Subscribe` 合并底层依赖订阅；依赖变化时 handler 运行，模板 updater 重新读取 computed。
- 依赖可以是 computed；生成器递归展开，环 → EUI035。
- computed 属性可读不可写；`@bind` 源 → EUI037。
- computed getter 中读取未声明状态：不更新；文档明确，S5 评估是否用 Roslyn 分析 getter body 报警。
- computed 依赖 `Signal<T>`：`Subscribe` 直接订阅信号。
- 性能：computed 适合廉价派生（`Count`/`Total`）；昂贵计算自行缓存或用 `Version`。

### 15.6.5 诊断边界

- 编译期能看见模板读取的普通成员 → EUI036；看不见用户在 C# 里绕过 setter 的写入。
- 集合静态类型可判定为非 INCC → EUI041；接口/运行时类型不可判定 → 不报警，文档说明。
- 代码-behind 内直接写 `[UiState]` 的生成字段不可能（字段私有、名字保留）；用户不会绕过 setter。
- `[UiObservable]` 生成字段同样私有；安全。
- INPC 第三方对象由用户负责正确触发事件；`UiWatch` 只能监听。

### 15.6.6 线程、批处理与生命周期

- UI 线程；跨线程写未定义；S5 Debug 断言。
- 一次事件处理中多次写状态会触发多次 updater；不做自动批处理；需要合并时在 code-behind 中先算好再一次赋值，
  或用 `Version`/替换根对象。G4 帧级批处理再评估。
- 状态对象不需要 `Dispose`；订阅由观察者释放。共享状态被多个组件观察时，各自独立订阅/释放。
- 状态对象强引用订阅者 handler；订阅释放后解除；组件泄漏会表现为 `SubscriberCount > 0`（测试）。

## 15.7 组件

### 15.7.1 可观察参数

```csharp
[UiState]
public partial class UserCard : Component
{
    [UiParameter] public partial string Title { get; set; }
    [UiParameter] public partial bool IsSelected { get; set; }
    public partial int ClickCount { get; set; }        // 非参数，仅子模板可见

    protected override Element Build() => ...;
}
```

- 组件类 `[UiState]` → 所有 public partial 属性可观察；`[UiParameter]` 只决定模板可传入。
- 只对部分参数可观察：在类上不标 `[UiState]`，给参数标 `[UiObservable]`（类必须 partial）。
- `[UiContent]`/`[UiSlot]` 参数不得可观察。
- 参数默认值在构造函数中赋；`[UiParameter(Required = true)]` 仍由模板编译期检查。

### 15.7.2 父 → 子单向

- 父 `P={expr}`：若参数可观察，生成 updater；源变化 → 写参数 → 子 watcher → 子 UI。
- 若参数非可观察：仍可写，但子 UI 不刷新（EUI044 Warning）；子只在 Build/事件中读。
- 若参数是 `Signal<T>`：父传对象；子模板订阅；父不生成 updater（legacy）。
- 子模板结构不重建；仅定向 updater 运行。

### 15.7.3 子 → 父/双向

- 父 `@bind-P={parentPath}`：目标参数必须可观察；生成 `TwoWayComponent`。
- 子内部控件 `@bind-Q={Title}`：子参数 Title 可观察；控件变化 → Title → 父绑定 → 父状态。
- 初始值：父源 → 子参数；避免子默认值覆盖父值。
- 重入：`TwoWayComponent` 的 `_syncing` 防回环；父子多级绑定由每级护栏终止。
- 子参数被父覆盖时，子内部绑定更新；子自己的修改回写父；最后写入者胜出（UI 线程顺序）。

### 15.7.4 内容与插槽

- `[UiContent]`/`[UiSlot]` build-once；不因父状态变化重建。
- 内容内部的绑定/订阅仍按自己的作用域工作；父状态可被内容表达式读取（非 item 场景），但内容结构不重建。
- 组件不能通过可观察参数把内容变成响应式结构；需要动态内容用组件模板内的 `@if`/`@foreach`。

### 15.7.5 生命周期

- 父 `Track(cmp)` → 子 `Dispose`；子内部订阅随 `Component.Dispose` 释放。
- 父绑定/观察订阅在父作用域；父 `Dispose` 释放。
- 子状态对象可能被外部持有；组件 Dispose 不会清空状态，只释放订阅。
- 组件 `Mount()` 只 Build 一次；状态在 Mount 前可写，Build 读取当前值。
- 从元素树手工移除组件不会 Dispose（v1 限制，G4 Unmount）。

### 15.7.6 已知限制

1. 组件参数无 diff、不重跑 Build；参数可观察只更新已生成的 updater，不改变结构。
2. 插槽/内容 build-once。
3. `[UiContent]`/`[UiSlot]` 不可观察。
4. 组件级路由事件不实现（14.6.2）。
5. 非可观察参数从父更新时，子 UI 不刷新（EUI044）。
6. `[UiState]` 组件不支持 record/struct/泛型（v1）。

### 15.7.7 第三方组件

- 第三方组件可标 `[UiParameter]` + `[UiObservable]`（其 code-behind partial），消费端按 14.6.5 发现。
- 第三方只能暴露公开可观察参数；内部状态不影响契约。
- 消费端不执行组件代码（R1）；生成器只读符号（`[UiParameter]`、`[UiObservable]`、`[UiState]`、partial 属性）。
- 若第三方程序集用旧版生成器（无可观察参数），消费端按非可观察参数处理。

## 15.8 诊断

### 15.8.1 诊断表（EUI014、EUI032、EUI034–EUI044）

| 代码 | 条件 | 位置 | 级别 | 阶段 |
|---|---|---|---|---|
| EUI014 | `@foreach` item 模板引用**组件级**可观察状态/`Signal<T>`（单向/文本/`@if`/`@foreach`）；item 自己的可观察属性允许 | 表达式 | Error | S2（收窄） |
| EUI032 | **停用**：原"非 Signal 参数一次性赋值"；由 EUI044 取代，不再发出 | — | — | — |
| EUI034 | 非法 `[UiState]`/`[UiObservable]` 声明（§15.2.6） | 声明处 | Error/Warning | S1 |
| EUI035 | 非法 `[UiComputed]`：非只读/非 `[UiState]` 类/未知依赖/环/重复 | 声明处 | Error | S4 |
| EUI036 | 反应式位置读取不可观察的普通实例成员/字段 → 整条表达式一次性 | 表达式 | Warning | S2 |
| EUI037 | `@bind` 源非法：不可写/不可观察/方法/索引器/`?.`/静态/只读/类型不符/自绑 | 表达式 | Error | S3 |
| EUI038 | 反应式位置含不可分析结构（方法调用、索引器、lambda、dynamic、await、集合表达式）→ 一次性 | 表达式 | Warning | S2 |
| EUI039 | `Key` 依赖可观察状态/`Signal<T>`/可观察路径 | 表达式 | Error | S2 |
| EUI040 | `@bind` 目标非法：不可写/无 `{Name}Property`/事件/内容/保留属性/组件参数不可观察/类型不符 | 属性名 | Error | S3 |
| EUI041 | 集合原地变更不可观察：具体集合类型未实现 `INotifyCollectionChanged`（`List<T>`、数组等） | 集合表达式 | Warning | S2/S4 |
| EUI042 | item 模板 `@bind` 源 rooted at 组件级状态 | 表达式 | Error | S3 |
| EUI043 | 旧语法 `:bind`/`:value` 弃用 | 属性名 | Warning | S3 |
| EUI044 | 单向反应式源写入非可观察组件参数，子 UI 不会刷新 | 属性值 | Warning | S3 |

- EUI036/038 是 Warning：一次性的意图可能是合法的；构建可通过。需要"零警告"策略的项目自行提升。
- EUI041 是 Warning：集合替换仍工作；原地变更不工作。
- 所有诊断消息为英文（与现有代码一致），带修复建议；文档可本地化。

### 15.8.2 责任与 span

- 声明类（EUI034/035）：code-behind 声明处；若来自引用程序集无法定位源码，按使用点报（类似 14.5.4）。
- 模板类（EUI036/037/038/039/040/041/042/044）：`.ui` 表达式/属性名/集合表达式的精确 span。
- 生成器不因分析失败报假阳性；未解析表达式交给 C#。
- 建议（suggestion）不改变解析结果；例如 EUI036 提示"把成员改成 partial 状态属性或接受一次性"。

### 15.8.3 诊断演进

- `EUI032`（14.6.1）停用，不复用；新代码用 EUI044。
- `EUI014` 收窄：从"item 内引用任何信号/`:bind`"改为"引用**组件级**状态"；item 自身可观察属性与 `@bind` 合法。
- `EUI043` 是弃用期诊断；S5 后 `:bind`/`:value` 可移除或升级为 Error。
- 新诊断从 EUI034 起（03 §3.8 规则 4）；EUI034–EUI044 全部由 15 定义。

### 15.8.4 抑制（开放）

- v1 不提供 `.ui` 内抑制；EUI036/038/041 的 warning 可在项目级 `.editorconfig`/MSBuild 属性关闭（S5 设计）。
- 不要用"永远不报"来掩盖分析失败；分析失败时不报。

## 15.9 性能与基准

### 15.9.1 硬指标

1. **写入零分配**：`Signal<T>.Value` 写入（已有订阅）分配 0 字节；`GC.GetAllocatedBytesForCurrentThread` 前后差为 0。
2. **通知零反射**：`UiWatch`/`UiBinding` 运行时无反射、无字符串查找、无 `Activator`。
3. **订阅有界**：每个绑定点订阅数 = 依赖路径数（去重后）；不随数据项数增长（item 内除外）。
4. **释放干净**：组件/条目/分支卸载后，相关 `Signal.SubscriberCount` 回到 0（测试断言）。
5. **确定性**：同一输入 golden 文本一致；增量缓存 `Cached`/`Unchanged` 不被破坏。

### 15.9.2 基准场景

| 场景 | 方法 | 关注指标 |
|---|---|---|
| 信号写入 | 0/1/10/100 订阅者，`int`/`string` | ns/op、B/op |
| 单属性定向更新 | 1 个 `P={Count}`，写 1 次 | ns/op、B/op |
| 文本更新 | `TextBlock` 文本插值，写 1 次 | ns/op、B/op |
| 双向绑定 | CheckBox 写 1 次，源→目标/目标→源 | ns/op、B/op |
| 路径 watcher | `Vm.Address.City` 2/3 段，中间替换 | ns/op、B/op |
| 列表刷新 | 1000 项，重排/替换 1 项/全替换 | ms、分配 |
| 订阅/释放 | 1000 次 Subscribe/Dispose | ns/op、B/op |
| 生成时间 | 样例项目 10/50/100 个 `.ui` | ms、生成文件数 |

- 基准项目：`tests/EclipseUI.Benchmarks`（net10.0，BenchmarkDotNet 或手写 Stopwatch + `GC.GetAllocatedBytesForCurrentThread`）。
- 基线机：本机 6C/12T、16GB、UHD 630、.NET 10.0.401；结果写入
  `design/reports/state-reactivity-<date>.md`，注明配置。
- 不在本文写未测得的 ns 数字；只写门槛与场景。

### 15.9.3 预算（目标，S1–S5 实测后固化）

- `Signal<T>` 写入：≤ 现有实现的 1.0×（且 0 B/op）；现有实现每次 `ToArray()`，预期显著下降。
- 单属性 updater：≤ 2× 直接 C# 属性赋值 + 一次方法调用（不含目标控件内部失效/绘制）。
- `UiWatch` 通知：≤ 2× `Signal<T>` 直接订阅调用（路径段重接除外）。
- 生成时间：相对现状增加 ≤ 20%（样例项目；S5 测量）。
- 生成代码体积：每个 `P={expr}` 约 10–20 行；不复制表达式超过 2 次（初始 + updater）。

### 15.9.4 内存与代码体积

- `[UiState]` 每属性一个 `Signal<T>` 对象；空订阅数组为 null。10 个属性约几百字节（S1 实测）。
- 每个 watcher 一个 `WatchState` + 路径数组 + 每段订阅对象；Build 时分配，运行时不分配。
- `UiBinding` 每绑定一个状态对象 + 源订阅 + 目标事件委托；释放后解除。
- 不用 `ConditionalWeakTable`/弱引用；生命周期由作用域显式管理。

### 15.9.5 失败处置

- 写入分配 > 0：回退/修复 COW；不得以"分配很小"接受（硬指标）。
- 生成时间超预算：测量分析热点；合并合成 tree、减少语义模型查询；不引入跨轮可变缓存（D14）。
- 列表刷新超预算：检查 `ForEachBlock` 字典/队列分配；按实测优化，不预先复杂化。
- 任何性能优化不得改变语义或诊断；以 golden + 行为测试守护。

## 15.10 阶段与迁移

### 15.10.1 S1–S5

| 阶段 | 内容 | 出口 |
|---|---|---|
| **S1** | `[UiState]`/`[UiObservable]` 生成器；`IUiObservable`；`UiWatch` 核心路径；`UiSubscription`；`Signal<T>` COW；`UiComputed` 声明校验（不生成订阅也可） | 状态类可从 C# 读写/订阅；写入 0 分配；泄漏测试；golden 生成文本 |
| **S2** | 合成表达式宿主；依赖分类；`P={expr}` 单向；文本/`@if`/`@foreach`；块订阅工厂；item 身份重建；`ForEachBlock.Dispose` 修复；EUI034–EUI039/EUI041 | Gallery/TaskList 用普通状态运行；非可观察警告正确；无残留订阅；旧模板可编译 |
| **S3** | `@bind-P` 语法/解析；`UiBinding`；控件/组件双向；R3 修正；EUI040/EUI042/EUI043/EUI044；组件可观察参数 | 控件/组件双向工作；item `@bind` 工作；无 `SetValue` 绕过；迁移文档 |
| **S4** | INPC/INCC 适配；嵌套 `@foreach`；`[UiComputed]` 订阅；EUI035；集合策略 | `ObservableCollection` 增删更新；嵌套列表；computed 链；第三方 INPC |
| **S5** | 基准/预算；AOT/裁剪样例；线程断言；诊断抑制；`:bind`/`:value` 清理；最终审计 | 硬指标达标；无 P0/P1；文档/状态同步 |

### 15.10.2 迁移步骤（面向现有项目）

1. 升级运行时/生成器（同版本）；现有模板先保持 `Signal<T>`。
2. 把页面/组件状态改为 `[UiState]` partial 类；构造函数初始化非默认值。
3. 模板中把 `count.Value` 改为 `Count`；`P={show}` 变为反应式（确认意图）。
4. 把 `Checked:bind={show}` 改为 `@bind-Checked={Show}`（状态属性）或 `@bind-Checked={show}`（信号）。
5. 列表：`Signal<List<T>>` 换成 `ObservableCollection<T>` 状态属性，或保持根替换；
   item 类型改 `[UiState]` 以支持 item 内更新/双向。
6. 组件参数：`[UiParameter] Signal<T>` 改 `[UiParameter] partial T`（类 `[UiState]`）。
7. 清理 `:value`；S5 前完成。

### 15.10.3 退出标准

- S1：`dotnet test` 全绿；新增状态测试 ≥ 20；写入分配 0；订阅释放 0 残留。
- S2：普通状态驱动的 Gallery；EUI036/038/039/041 测试；item 重建测试；无 stale 闭包。
- S3：双向控件/组件测试；R3 审计（无 `SetValue` 生成）；EUI040/042/043/044 测试。
- S4：INCC/INPC/computed/嵌套测试；集合策略回归。
- S5：基准报告；AOT/裁剪样例；线程断言；硬指标；文档 03/14/15/README 同步；无已知 P0/P1。

## 15.11 决策记录

> 本文决策编号仅在本文件内引用；不占用 03 的 D（偏差）或 14 的 D（决策）编号。

| # | 决策 | 理由 |
|---|---|---|
| 1 | 采用 partial 属性 + 源生成 `Signal<T>` 门面 | 普通类型书写、编译期依赖、AOT、无运行时魔法 |
| 2 | `[UiState]` 整类 + `[UiObservable]` opt-in + `[UiComputed]` 无后备 | 覆盖整类/少量属性/派生值三种场景 |
| 3 | `P={expr}` 单向反应式，全有或全无 | 默认行为符合直觉；不可分析时明确一次性，不半订阅 |
| 4 | `@bind-P` 双向，源可写可观察路径/信号 | 与 Blazor 直觉一致；显式双向 |
| 5 | `IUiObservable.Subscribe(string, Action)` + `nameof` | 无反射、无共享 ID 表、生成器/运行时解耦 |
| 6 | `UiWatch` 路径 watcher + 重接 + INCC | 嵌套对象/集合/INPC 统一；运行时不执行表达式 |
| 7 | 拒绝 Fody/代理/轮询/diff | 不可静态分析、AOT/诊断/确定性风险；仍需绑定引擎 |
| 8 | `@foreach` 同 Key 不同 item 重建 | 保证事件/闭包/绑定不指向旧 item；O(变动数) |
| 9 | 组件参数可观察 partial；无 diff | 不重跑 Build；与 04 的无参数 diff 一致 |
| 10 | `Signal<T>` COW，写零分配 | 现有实现每写 `ToArray()`；硬指标 |
| 11 | 诊断 EUI034–EUI044；EUI032 停用；EUI014 收窄 | 明确错误/警告边界；不静默陈旧 |
| 12 | 文档先行；S1–S5 分阶段 | 契约先冻结，避免接口漂移 |

## 15.12 开放问题

1. **Roslyn 版本策略**：生产生成器可继续用 4.9.2（语法级识别，实测宿主可用）；但 4.9.2 解析器不支持 partial 属性
   （CS0267），**单元测试驱动无法直接构造**。建议生成器与测试统一升到 ≥4.11（或与 .NET 10 SDK 对齐），S1 决策。
2. **`@bind-` 语法**：`@bind-P` vs `bind-P` vs `P.bind`；当前选 Blazor 风格 `@bind-P`，需在 03 v3 冻结并经用户确认。
3. **item 重建 vs item holder**：当前选"引用变化重建"，保证闭包安全；超大数据列表每次刷新都替换对象时可能重建过多；
   S5 用基准决定是否引入 holder + 逐项通知。
4. **批处理**：一次事件多次写导致多次 updater；S5 评估帧级合并（不改变语义）。
5. **EUI036/038/041 抑制**：v1 无 `.ui` 内抑制；S5 设计项目级配置。
6. **`[UiComputed]` getter body 分析**：当前信任 `dependsOn`；S5 评估 Roslyn 数据流校验。
7. **接口集合类型**：`IList<T>`/`IReadOnlyList<T>` 可能运行时是 `ObservableCollection<T>`；当前不警告，靠运行时 INCC；
   S4 用测试确认策略。
8. **线程/Dispatcher**：跨线程写入暂无调度 API；S5 Debug 断言，G4 设计与帧循环结合。
9. **`[UiState]` record/struct/泛型**：v1 不支持；若需求强烈，另开契约，不复用当前生成形态。
10. **`IUiObservable` 属性枚举**：DevTools/序列化可能需要属性列表；当前不提供，避免第二事实源。
11. **序列化/AOT**：partial 属性 + 生成字段对 JSON/裁剪的影响需 S5 样例验证。
12. **组件参数默认值**：构造函数赋值是唯一方式；是否引入 `[UiParameter(Default=...)]` 待定（与 14.6.1 一致）。

## 15.13 自洽性审计

### 15.13.1 完整性

| 面 | 覆盖 | 缺口 |
|---|---|---|
| 状态声明 | `[UiState]`/`[UiObservable]`/`[UiComputed]`、初始化、限制、诊断 | record/struct/泛型（明确非目标） |
| 模板位置 | `P`、文本、`@if`、`@foreach`、`Key`、事件、组件、item | `Ref`（G3 另定） |
| 作用域 | 根/if 内容/if 条件/foreach 集合/item/组件/绑定 | 手工移除元素的 Unmount（G4） |
| 对象自变化 | 写契约、可观察/不可观察、逃生舱、线程 | Dispatcher/批处理（G4/S5） |
| 诊断 | EUI014/032/034–044 | 抑制配置（S5） |
| 迁移 | 旧 Signal、`:bind`/`:value`、组件参数、列表 | 自动迁移工具（无） |
| 性能 | 硬指标、场景、预算、报告 | 具体数字待 S1/S5 实测 |

### 15.13.2 可实现性（实测）

| 事实 | 结果 | 影响 |
|---|---|---|
| .NET 10 编译 `[UiState] partial` + 生成 partial 实现 | 成功；运行时 `count=2; notifications=2`；`nameof` 正常（2026-10-29 探针） | 生成形态可行 |
| partial 属性带初始化器 | `CS8050` | 非默认值必须构造函数赋 |
| partial 类拆分基类与接口声明 | 成功 | 状态部分只加接口，模板部分加 `Component` 基类 |
| Roslyn 4.9.2 编译的生成器在 .NET 10 宿主分析 partial 属性 | 成功：`ForAttributeWithMetadataName` + 语法级 `partial` 检测 + `IPropertySymbol` 类型/名字 + 生成实现，消费者编译运行 | 生产生成器可不升级；但见下行 |
| Roslyn 4.9.2 解析器直接解析 partial 属性 | `CS0267`，无 `PartialDefinitionPart` | 生成器单元测试不能直接构造；测试项目需 ≥4.11 或走真实宿主 |
| `nameof(global::Counter.Count)` | 编译通过 | 生成代码可全限定 |
| 现有 `Signal<T>` 写入 | `subs.ToArray()`（[Signals.cs:57](../src/EclipseUI.Runtime/Signals.cs#L57)） | S1 COW 修复 |
| 现有 `Bind.TwoWay` | `SetValue/GetValue`（[Component.cs:97-111](../src/EclipseUI.Runtime/Component.cs#L97)） | 违反 R3；S3 新 helper |
| 现有块 API | `SetCondition(..., params ISignal[])`、`SetItems(..., params ISignal[])`（[Blocks.cs:39](../src/EclipseUI.Runtime/Blocks.cs#L39)、[:127](../src/EclipseUI.Runtime/Blocks.cs#L127)） | S2 加订阅工厂 |
| 现有 `ForEachBlock` 复用 | 只按 key，不比较 item（[Blocks.cs:163-173](../src/EclipseUI.Runtime/Blocks.cs#L163)） | 新语义需改 |
| 现有 `ForEachBlock.Dispose` | 只释放依赖（[Blocks.cs:190](../src/EclipseUI.Runtime/Blocks.cs#L190)） | D18；S2 修复 |
| 生成器 Roslyn | 4.9.2（[csproj](../src/EclipseUI.Compiler.Generator/EclipseUI.Compiler.Generator.csproj)） | 见开放问题 1 |

### 15.13.3 与现有文档的冲突与修正

| 冲突 | 旧文档 | 修正 |
|---|---|---|
| `P={expr}` 一次性 | 03 §3.0.3-2、§3.3.3.1；14 D7 | 03 v3 改为单向反应式（可分析时）；14 D7 改为"仅对不可观察依赖保持一次性" |
| `:bind` 是唯一双向 | 03 §3.0.3-4、§3.3.3.2；14 §14.7.1 | v3 新增 `@bind-P`；`:bind` 弃用（EUI043） |
| item 模板禁止信号/`:bind` | 03 §3.3.6.2/§3.3.6.4；14 §14.7.4 | EUI014 收窄为组件级状态；item 可观察属性/`@bind` 合法；Key 同 item 同复用、不同重建 |
| 组件推荐 `Signal<T>` 参数 | 03 §3.3.8；14 §14.6.1/§14.6.6 | 推荐可观察 partial 参数；`Signal<T>` 参数 legacy；EUI032 停用/EUI044 |
| “读取收集依赖” | 02 单向管线 | 改为"写入通知、编译期分析依赖"（§15.13.5） |
| 父子通信靠 signals | 04 组件实例模型 | 增加 `[UiState]`/`IUiObservable`；signals 仍可用 |
| G3 `:value` | 09 G3 范围/并行修复 | 改为 S1–S3 状态与反应式；`P={expr}` 单向 |
| 14 R3 与 `Bind.TwoWay` 冲突 | 14 §14.3 R3 / [Component.cs:97](../src/EclipseUI.Runtime/Component.cs#L97) | 生成代码改用 `UiBinding` 公开 setter；旧 `Bind.TwoWay` 保留但 Obsolete |
| 14 D6 "无参数 diff，信号参数" | 14 §14.14 | D6 保留"无 diff"，推荐改为可观察 partial 参数 |
| 03 D10 正则重写 | 03 §3.3.3.4 | 15 改用 Roslyn 语义分析；D10 在 S2 修复 |
| 03 D18 块 Dispose | 03 §3.3.4 | S2 修复；15 复用条目重建 |
| EUI032 | 03 §3.5.1；14 §14.9.1 | 停用；EUI044 取代 |
| 03 状态表 `:value` 保留 | 03 §3.5.2/§3.3.9 | v3 改为弃用别名；`@bind-P` 新行 |
| README 状态 | README 头部 | 增加 15 与 v3 状态 |

### 15.13.4 与代码的差距台账（D23–D36）

> 03 v3 的 D1–D36 与本表一致（D23–D36 逐条对应）；D37–D41 是本次 spec 审计新增的**生成文本**差距，
> 见 design/03 §3.5.3 与 [spec/02 §12](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d81)，不在本表重复。
> 本表是代码差距视图；**这些是"文档已定、代码未对齐"**，不是实现声明。

| # | 规范 | 当前实现 | 影响 | 收敛 |
|---|---|---|---|---|
| D23 | `P={expr}` 单向反应式 | 一次性赋值（03 v2；[Emitter.cs](../src/EclipseUI.Compiler.Generator/Emitter.cs)） | 普通状态不更新 | S2 |
| D24 | `@bind-P` 双向 | 只有 `:bind`（`Signal<T>` 对象） | 无法绑定普通属性/组件参数 | S3 |
| D25 | `Signal<T>` 写入零分配 | 每次 `subs.ToArray()`（[Signals.cs:57](../src/EclipseUI.Runtime/Signals.cs#L57)） | GC 压力 | S1 |
| D26 | 同 Key 不同 item 重建 | 只按 key 复用，`Updaters` 重跑（[Blocks.cs:163-173](../src/EclipseUI.Runtime/Blocks.cs#L163)） | 闭包/事件指向旧 item | S2 |
| D27 | 块 Dispose 释放 item scope | `Dispose` 只释放依赖（[Blocks.cs:190](../src/EclipseUI.Runtime/Blocks.cs#L190)，原 D18） | 泄漏 | S2 |
| D28 | 块接受订阅工厂 | `SetCondition/SetItems` 只有 `params ISignal[]` | 无法订阅状态属性/路径 | S2 |
| D29 | 双向走公开 setter（R3） | `Bind.TwoWay` 用 `SetValue/GetValue`（[Component.cs:97-111](../src/EclipseUI.Runtime/Component.cs#L97)） | 绕过属性引擎 | S3 |
| D30 | 生成器测试可解析 partial 属性 | 生成器/测试 Roslyn 4.9.2；parser 报 CS0267（实测） | 无法用 `CSharpGeneratorDriver` 覆盖 | S1 决策：升测试 Roslyn 或宿主集成测试 |
| D31 | 语义级依赖分析 | 正则重写（[Emitter.cs:125](../src/EclipseUI.Compiler.Generator/Emitter.cs#L125)，原 D10） | 字符串/同名变量误改 | S2 |
| D32 | `IUiObservable`/`UiWatch`/`UiBinding` | 运行时不存在 | 无法实现 | S1/S3 |
| D33 | `[UiState]`/`[UiObservable]`/`[UiComputed]` 生成器 | 不存在 | 普通类型不可观察 | S1/S4 |
| D34 | item 可观察属性/item `@bind` | item 内禁止信号/`:bind`（原 EUI014） | 列表编辑不可写 | S2/S3 |
| D35 | 组件可观察参数 | 只推荐 `Signal<T>` 参数 | 父更新不刷新子 UI | S3 |
| D36 | EUI034–EUI044 | 只有 EUI001–EUI033（部分未实现） | 无诊断/误导 | S1–S4 |

### 15.13.5 评审清单

- [ ] 03 v3 的冻结线、EBNF、§3.3.3–§3.3.8、§3.5、§3.6、附录 A 与本文一致。
- [ ] 14 v3 的 R3/R7–R9、§14.6、§14.7、§14.8、§14.9、§14.12–§14.14 与本文一致。
- [ ] 02/04/09/README 的措辞不再声称"读取收集依赖"/"`P={expr}` 一次性"/"`:value` G3"。
- [ ] 每个 EUI 诊断有触发点、span、级别、阶段、测试。
- [ ] 每个生成形态有 golden 文本与编译测试。
- [ ] 每个订阅有作用域所有者与泄漏测试。
- [ ] 不执行用户代码、不反射、无静态可变状态（R1/R6/R7–R9）。
- [ ] 性能硬指标有基准报告；未测数字不写进文档。
- [ ] 迁移/弃用期与旧模板兼容性有测试。

## 附录 A：完整示例（`[UiState]` + 双向 + 列表）

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
    public int Id { get; init; }                 // 普通属性：用于 Key，不可观察
    public partial string Title { get; set; }
    public partial bool Done { get; set; }
}
```

- `Id` 是普通 init 属性：`Key={t.Id}` 合法（EUI039 只拦截可观察依赖）。
- `Title`/`Done` 可观察：item 文本/双向绑定可更新。

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

    private int _nextId = 1;

    public TodoList()
    {
        Tasks = new ObservableCollection<TodoItem>
        {
            new() { Id = 1, Title = "读 15 状态契约" },
            new() { Id = 2, Title = "补 EUI034" },
        };
    }

    private void AddTask()
    {
        Tasks.Add(new TodoItem { Id = _nextId++, Title = $"任务 {_nextId}" });
    }
}
```

- `Tasks` 是状态属性 + `ObservableCollection<T>`：属性替换与集合增删都通知。
- `ShowCount` 是组件状态；CheckBox 双向写它；`@if` 订阅它。
- 构造函数的赋值发生在 `Mount()` 前；Build 读取当前值。

### A.4 生成状态节选

```csharp
partial class TodoList : global::EclipseUI.IUiObservable
{
    private sealed class __EuiState
    {
        public readonly global::EclipseUI.Signal<global::System.Collections.ObjectModel.ObservableCollection<global::Examples.TodoItem>> Tasks = new(default!);
        public readonly global::EclipseUI.Signal<bool> ShowCount = new(default!);
    }

    private readonly __EuiState __eui = new __EuiState();

    public partial global::System.Collections.ObjectModel.ObservableCollection<global::Examples.TodoItem> Tasks
    {
        get => __eui.Tasks.Value;
        set => __eui.Tasks.Value = value;
    }

    public partial bool ShowCount
    {
        get => __eui.ShowCount.Value;
        set => __eui.ShowCount.Value = value;
    }

    global::System.IDisposable global::EclipseUI.IUiObservable.Subscribe(
        string property, global::System.Action handler)
    {
        switch (property)
        {
            case nameof(ShowCount): return __eui.ShowCount.Subscribe(handler);
            case nameof(Tasks): return __eui.Tasks.Subscribe(handler);
            default: throw new global::System.ArgumentOutOfRangeException(nameof(property));
        }
    }
}
```

### A.5 生成模板节选

完整、逐字节的生成示例见 [spec/02 附录 A](../spec/02-generated-code-contract.md)；
本文不再保留简化副本，避免两份生成文本互相漂移。反应式语义以本文 §15.4 为准，生成文本形态以 spec/02 为准。

> 旧版本此处示例的 `new[]`、未限定类型、`null` updaters、丢弃 `UiBinding` 返回值、
> 对 `CheckBox` 调 `AddRun` 等形态均已废止；勘误见 [spec/README](../spec/README.md) E1/E2。

### A.6 行为走查

| 操作 | 发生什么 |
|---|---|
| 点击“显示任务数量” | CheckBox 公开 setter → `PropertyChanged` → `UiBinding` 写 `ShowCount` → `IUiObservable` 通知 → `@if` 条件重算 → 分支挂载/卸载 |
| 点击“添加任务” | `AddTask` → `ObservableCollection.Add` → INCC → `@foreach` 刷新（新条目模板）；`Tasks.Count` 文本 updater 重算 |
| 勾选某项 | CheckBox → 公开 setter → `PropertyChanged` → binding 写 `t.Done` → item 状态通知 → 该 item 的订阅更新（其他 item 不受影响） |
| 代码中改 `t.Title` | 生成 setter → item 状态通知 → item 文本 updater 更新 |
| 代码中 `Tasks = new ObservableCollection<...>()` | `Tasks` 状态通知 → 列表刷新 + `Count` 文本更新；旧集合订阅被替换 |
| 代码中 `Tasks.Add(...)` | INCC → 列表刷新 + Count 更新 |
| 代码中 `new List<TodoItem>()` 的 `.Add`（若把 Tasks 改成 List） | 不通知；EUI041 Warning；`@foreach` 不刷新 |
| 把整个 TodoItem 替换为新对象（同 Id） | key 同但 item 引用不同 → 条目重建；旧 item 的绑定/事件释放 |
| 只改 `t.Id`（普通属性） | 不通知；Key 不更新（EUI039 只拦可观察依赖；普通属性改了也不会重排） |

### A.7 语法对照

| 写法 | 语义 | 依据 |
|---|---|---|
| `@bind-Checked={ShowCount}` | 双向；源是组件状态；目标 CheckBox | §15.4.4 |
| `@if (ShowCount)` | 条件订阅状态；分支切换 | §15.4.6 |
| `{Tasks.Count}` | 集合属性 + INCC；Count 变化更新 | §15.4.5/§15.4.7 |
| `@foreach (var t in Tasks)` | 状态属性集合；INCC 增删刷新 | §15.4.7 |
| `Key={t.Id}` | item 普通属性；不得可观察 | §15.4.8/EUI039 |
| `@bind-Checked={t.Done}` | item 可观察属性双向；item scope | §15.4.4/§15.4.8 |
| `OnClick={() => AddTask()}` | 事件不订阅；触发时执行 | §15.4.9 |

## 附录 B：旧模板迁移对照

```xml
<!-- v2（现状） -->
<CheckBox Checked:bind={showCount}>显示</CheckBox>
<TextBlock>共 {tasks.Value.Count} 项</TextBlock>
@foreach (var t in tasks) { <TextBlock>{t.Title}</TextBlock> }
```

```xml
<!-- v3（目标） -->
<CheckBox @bind-Checked={ShowCount}>显示</CheckBox>
<TextBlock>共 {Tasks.Count} 项</TextBlock>
@foreach (var t in Tasks) { <TextBlock>{t.Title}</TextBlock> }
```

```csharp
// v2
public readonly Signal<List<TodoItem>> tasks = new(new());
public readonly Signal<bool> showCount = new(false);

// v3
[UiState] public partial class TodoList : Component
{
    public partial ObservableCollection<TodoItem> Tasks { get; set; }
    public partial bool ShowCount { get; set; }
    public TodoList() { Tasks = new(); }
}
```

- 若暂时不迁移，`Signal<T>` 版本在 S2–S4 仍可运行；`:bind`/`:value` 在 S3 起报 EUI043 Warning。
- `P={signal}` 的行为从一次性变为反应式；发布说明必须提示。
