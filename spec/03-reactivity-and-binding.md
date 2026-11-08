# 03 · 反应式与绑定契约（失效重算）

> 状态：v1 定稿（2026-10-29）。**S1–S3 已落地**（S1/S2 2026-10-30，S3 控件侧 2026-11-02、组件侧随 C4 2026-11-05）；
> S4/S5 未开始，实现按 §13 分阶段推进。
> 本文是模板**反应式语义、组件失效、块刷新与双向绑定**的规范正本。
> 语法/词法见 [spec/01](./01-template-syntax.md)，生成文本形态见 [spec/02](./02-generated-code-contract.md)，
> 控件/组件元数据见 [design/14](../design/14-control-template-contract.md)（将迁移为 `spec/04`）。
>
> **v4 换轨**：模板反应式不再基于 `[UiState]`/`[UiObservable]`/`Signal<T>` 与依赖分析，
> 改为**组件级失效 + 保留元素树 + 重算动态表达式**（Blazor 式失效模型，不做虚拟 DOM diff）。
> design/15 的 v3 状态契约降为历史记录，不再作为实现依据；旧模型与差异见 §15 与 design/03 §3.5.4。

## 0. 文档约定

### 0.1 范围

| 本文定义 | 本文不定义（去哪看） |
|---|---|
| 组件 `Mount`/`Invalidate`/`Render`/`Dispose` 生命周期与线程模型 | `.ui` 词法/句法/诊断触发点（[spec/01](./01-template-syntax.md)） |
| 失效队列、合并、宿主刷新时机、重入与死循环防护 | 生成文本的精确形态、`#line`、命名（[spec/02](./02-generated-code-contract.md)） |
| 保留元素树、动态表达式求值时机、块刷新 | 控件如何声明属性/事件/内容模型（design/14） |
| `@if`/`@foreach` 的分支重建与 Keyed 复用 | 布局、绘制、hit test（design/05） |
| `@bind-P` 的单向刷新、回写、组件 `PChanged` | `UiBinding`/`BlockScope` 的逐语句生成形态（spec/02 §4） |
| 事件自动失效、异步/后台线程的显式失效 | 样式/主题（G4） |
| 诊断码 EUI007/EUI010/EUI033/EUI037/EUI040 的反应式口径 | 控件元数据诊断 EUI001–EUI033（design/14 §14.9） |

### 0.2 权威关系

1. **反应式语义以本文为准**。spec/01 §4 只描述模板侧的可见语义；冲突时按本文修订。
2. **生成形态以 spec/02 为准**。本文冻结"生成代码何时求值、写什么、何时失效"；
   `Render`/`U{N}`/`BlockScope` 的逐语句文本由 spec/02 定义。
3. **状态声明不再有独立契约**。模板作者写普通字段/属性；不存在 `[UiState]`/`[UiObservable]`/`[UiComputed]`
   状态生成器，也不要求 `INotifyPropertyChanged`/`INotifyCollectionChanged`。
4. **控件/组件元数据以 design/14 为准**；其中旧的"可观察参数/`Signal<T>` 参数/EUI044"条款由本文取代。
5. **偏差只允许出现在台账**。实现与本文的差异必须登记在 design/03 §3.5.4（v4 台账）与 spec/02 §12；
   台账之外的差异视为实现缺陷。

### 0.3 规范用语

| 词 | 含义 |
|---|---|
| **必须** | 违反即视为实现缺陷；契约测试必须覆盖 |
| **不得** | 生成器/运行器禁止生成或接受的形态 |
| **应当** | 允许暂缺，但评审必须记录原因与阶段 |
| **可以** | 实现自由度，不影响语义与可观察行为 |

### 0.4 术语

| 术语 | 含义 |
|---|---|
| 渲染（render） | 执行组件的 `Render()`：重算动态表达式、推送参数、刷新块。**不重建**静态元素 |
| 失效（invalidation） | 把组件标记为"渲染结果可能过期"；由 `Invalidate()` 触发 |
| 失效队列 | 根组件持有的待渲染组件队列；按帧合并、按深度排序 |
| 保留树（retained tree） | `Build()` 构造、跨多次 `Render()` 复用的元素树 |
| 动态表达式 | 模板 `{expr}` 位置上的 C# 表达式；每次所属作用域渲染/刷新时重算 |
| updater | `U{N}`（组件根级）与 `R{N}`（块内）的统称：重算表达式并写目标 |
| 渲染动作（render action） | `BlockScope.RenderActions` 中的 `Action`；块刷新时重跑 |
| 块刷新（refresh） | `ConditionalBlock.Refresh()` / `ForEachBlock<T>.Refresh()` |
| 条目（entry） | `@foreach` 中一个 Key 对应的一次模板实例化 |
| 参数推送（parameter push） | 父组件 `Render()` 写子组件参数并调用子组件 `Invalidate()` |
| 回写（write-back） | 目标控件/组件变化时把新值写回绑定源，再失效所属组件 |
| `PChanged` | 组件为参数 `P` 声明的向上变更回调（`Action<T>`） |
| 写契约（write contract） | 只有 `Invalidate()` 之后的 `Render()` 会读取新值；直接改对象不会自动刷新 |

### 0.5 状态标记

| 标记 | 含义 |
|---|---|
| ✅ | 当前生成器已实现，行为与本文一致（台账列出的除外） |
| S1–S5 | 语义已冻结，按 §13 分阶段实现 |
| C1–C6 | 控件/组件元数据迁移阶段，见 design/14 §14.12 |
| G3 / G4 | 语义已冻结，排在 G3 / G4 实现 |
| ⛔ | 该形态永不允许；实现出现即测试失败 |

规范对**目标状态**生效。未实现的语法项必须按 spec/01 §6.2 报指定诊断，不得落进"未知属性/未知标签"的兜底。

## 1. 定位、动机与冻结线

### 1.1 为什么改成失效重算

v3 模型要求模板状态是"可观察路径"，因此限制状态变量类型：集合必须是 `ObservableCollection<T>` 或实现 INCC，
嵌套对象必须实现 INPC/可观察属性，表达式必须可静态分析。实际后果是：

- 用户为了写一个列表，被迫把 `List<T>`/数组/字典改成 `ObservableCollection<T>`；
- 用户为了写一个派生值，被迫引入 `[UiComputed]` 或把方法调用拆成属性；
- 任一不满足就退化为一次性表达式（`EUI036`/`EUI038`），UI 静默不更新；
- 依赖分析、路径监听、订阅作用域带来一整套运行时与生成器复杂度。

v4 把**反应式单位**从"表达式/路径"提升到"组件渲染"：

1. `Build()` 构造静态结构与保留元素树，只执行一次；
2. `Invalidate()` 标记组件过期；
3. 帧循环在布局前调用 `Render()`，重算该组件的全部动态表达式；
4. `@if`/`@foreach` 通过块刷新处理结构变化；Keyed 条目复用元素实例；
5. 不做虚拟 DOM diff，不重建静态元素，不做依赖分析。

这样任意状态类型都可用：`int`、`string`、`List<T>`、数组、字典、LINQ 结果、普通类、record、struct，
只要 `@foreach` 的表达式实现 `IEnumerable<T>`。代价是**显式失效**：非 UI 事件引起的状态变化必须调用 `Invalidate()`；
每次失效重算组件动态作用域，而不是只重算一条绑定。

### 1.2 与 Blazor 的关系

| 方面 | EclipseUI v4 | Blazor |
|---|---|---|
| 触发 | `Invalidate()` / UI 事件自动 / 参数推送 | `StateHasChanged()` / UI 事件自动 / 参数变化 |
| 反应式单位 | 组件 `Render()` | 组件 `BuildRenderTree` |
| 结构更新 | 保留元素树 + 块刷新；无通用 diff | 渲染树 diff → DOM patch |
| 集合 | 每次刷新重新枚举任意 `IEnumerable<T>` | 每次渲染重新枚举任意 `IEnumerable<T>` |
| 子组件参数 | 父渲染写参数 + `Invalidate()` 子组件 | 设置参数 → 子组件渲染 |
| 双向绑定 | `UiBinding` + `PChanged` 回调 | `@bind` + `EventCallback` |
| 自动观察 | 无 | 无 |
| 非事件变化 | 显式 `Invalidate()` | 显式 `StateHasChanged()`（或 `InvokeAsync`） |

v4 **不采用** Blazor 的虚拟 DOM diff：元素实例保留，静态结构不比较；只有动态表达式重算与块结构协调。
这避免了整棵渲染树的分配与比较，但要求模板把"结构变化"收敛到 `@if`/`@foreach`。

### 1.3 冻结线

以下决定已冻结；改义必须先新增状态项、诊断映射与迁移期，不得"先生效、后补文档"：

1. **模板作者只写普通类型**：字段、自动属性、普通类/record/struct；不要求 `[UiState]`、`Signal<T>`、
   `INotifyPropertyChanged`、`ObservableCollection<T>`。
2. **反应式单位是组件渲染**：`Invalidate()` → `Render()` 重算该组件的动态表达式；不存在按路径/按信号的定向订阅。
3. **`P={expr}` 每次渲染重算**：控件属性直接赋值（属性引擎相等短路）；组件参数带相等保护地推送并失效子组件；
   不做依赖分析，不因方法调用/索引器/lambda 退化。
4. **文本 `{expr}` 每次渲染重算**：`Text` 模型重设整段字符串；`TextRuns` 模型只重设动态 run 的 `Text`。
5. **`@bind-P={expr}` 双向**：源必须是可写左值（字段/属性）；控件目标走 `UiBinding`，
   组件目标要求 `PChanged` 回调；初始方向源 → 目标；目标变化回写源并失效。
6. **结构变化只由块处理**：`@if` 切换分支时销毁/重建该分支；`@foreach` 每次刷新重新枚举集合并按 Key 复用；
   不存在通用元素 diff。
7. **Keyed 复用条件是 Key 相等且 item 身份相等**：引用类型按引用，值类型按值；同 Key 不同 item 重建条目。
8. **事件处理器 build-once**：控件事件与组件委托参数在 Build 时注册，不在每次渲染重新生成；
   处理器完成后自动 `Invalidate()` 所属组件（组件委托参数失效父组件）。
9. **非 UI 事件的变化必须显式 `Invalidate()`**：后台线程、定时器、`await` 续体、直接修改集合/对象，均不自动刷新。
10. **大小写敏感，不做别名**；表达式就是 C# 表达式；不兼容 XAML。
11. **移除旧状态契约**：`[UiState]`/`[UiObservable]`/`[UiComputed]`、`IUiObservable`、`UiWatch`、依赖分析、
    `EUI014`/`EUI034`–`EUI036`/`EUI038`/`EUI039`/`EUI041`/`EUI042`/`EUI044` 不再是模板契约的一部分。
12. **未实现的语法必须显式保留并报诊断**（`EUI031`/`EUI029`/`EUI030`/`EUI008`；插槽错误用 `EUI022`，✅ C6 已实现），
    不得落进"未知属性/未知标签"的兜底。

任何违反冻结线的提案，必须先在本文登记状态与诊断，再改代码；不接受"先生效、后补文档"。

### 1.4 非目标

- 不做运行时依赖追踪、代理对象、IL weaving、轮询或反射；
- 不做通用渲染树 diff、虚拟 DOM、组件参数 diff；
- 不自动监听 `INotifyPropertyChanged`/`INotifyCollectionChanged`（类型可以实现，但框架不订阅）；
- 不保证表达式只求值一次；表达式必须无副作用（§4.5）；
- 不做跨线程状态同步；只保证 `Invalidate()` 可从任意线程调用。

## 2. 组件生命周期

### 2.1 `Mount()`

```csharp
public void Mount()
{
    if (IsMounted) return;
    Root = Build();     // 构造保留树 + 首次求值
    IsMounted = true;
}
```

- `Build()` 执行一次，负责：构造元素/组件、赋静态字面量、注册事件、配置块、**首次求值全部动态表达式**、
  完成子组件参数推送与 `Mount()`。
- `Build()` 返回根元素；组件为根时返回 `cmp.Root`。
- `Mount()` **不调用** `Render()`；首次求值已融合在 `Build()` 内（生成形态见 spec/02 §5.2）。
- `Mount()` 幂等；重复调用不重新 `Build()`。
- `Build()` 抛异常时 `IsMounted` 保持 false、`Root` 不可用；调用方负责释放已登记的组件/资源（R5/R8）。
- 代码后置可以在 `Build()` 之后读取 `Root`；`Build()` 内不得读取自己的 `Root`。

### 2.2 `Invalidate()`

```csharp
public void Invalidate()
```

- **线程安全**：可从任意线程调用；只做"标记 + 入队"，不执行用户代码、不触碰元素树。
- 未 `Mount()` 或已 `Dispose()`：no-op。首次 `Build()` 会读取当前状态，无需补一次渲染。
- 同一组件的多次 `Invalidate()` 在两次刷新之间合并为一次 `Render()`。
- 组件有所有者时，失效请求进入**根组件的失效队列**；无所有者时组件自己就是根。
- `Invalidate()` 不传播到父组件：子组件失效只重算子组件，不重算父组件。
- `Invalidate()` 不唤醒宿主；宿主每帧在布局前调用 `ProcessInvalidations()`（§2.4）。
- 生成代码在以下位置调用 `Invalidate()`：
  - 控件事件处理器完成后（所属组件）；
  - 组件委托参数（父组件传入的回调）被调用后（父组件）；
  - `UiBinding` 的目标属性变化回写源后（绑定所属组件）；
  - 组件内部修改参数后由组件作者显式调用（配合 `PChanged`）。
- 代码后置中的后台/异步变化必须显式调用；框架不观察任何对象。

### 2.3 `Render()`

```csharp
protected virtual void Render()
```

- 生成器在模板含动态内容时 override；全静态模板不生成 `Render()`，基类空实现。
- `Render()` 只做四类事：
  1. 调用根级动态赋值方法 `U{N}()`（控件属性、文本、组件参数）；
  2. 调用根级 `UiBinding.Refresh()`；
  3. 调用根级 `ConditionalBlock.Refresh()` / `ForEachBlock<T>.Refresh()`；
  4. 按源码顺序重复上述调用。
- `Render()` **不得**构造新元素、注册事件、安装绑定、调用 `Mount()`；这些只属于 `Build()`。
  例外是块 `Refresh()` 内部按条件/条目构造内容——这是块协调器的职责，不是 `Render()` 直接构造。
- `Render()` 的重算范围是该组件的**根级动态绑定与根级块**；块内部由块的刷新动作递归处理。
- `Render()` 在 UI 线程执行；不得从后台线程直接调用。
- `Render()` 抛异常时异常向上传播到 `ProcessInvalidations()` 调用方；组件保持已挂载状态，
  本次失效已消费，后续 `Invalidate()` 可以重试。生成代码不捕获、不吞掉异常。

### 2.4 宿主契约与失效队列

- 宿主必须在**每帧布局之前**对根组件调用：

```csharp
public bool ProcessInvalidations()
```

  返回本次是否执行过至少一次 `Render()`。调用时机：`Measure`/`Arrange` 之前，输入事件处理之后。
- 无头宿主在每次 `UpdateLayout()` 前调用；窗口宿主在帧循环的 `Measure` 前调用。
- 根组件持有失效队列；所有子组件的 `Invalidate()` 通过所有权链（§2.7）进入同一队列。
- 处理顺序：
  1. 按组件深度**升序**（根先于子；深度 = 所有权链长度，实时计算，§2.7），同深度按入队顺序；
  2. 出队时若组件已释放/未挂载则跳过；
  3. 调用 `ShouldRender()`（默认 true）；false 则消费本次失效但不执行 `Render()`；
  4. 清除待渲染标记后执行 `Render()`；`Render()` 期间的新失效重新入队，在同一轮 `ProcessInvalidations()` 中继续处理。
- 一轮 `ProcessInvalidations()` 必须有**迭代上限**（实现定义，建议 100 次 `Render`）；超过上限视为渲染死循环，
  必须抛出异常（消息包含组件类型名），不得挂起或静默丢弃。
- 深度升序保证父组件先推送参数，子组件随后以新参数渲染；避免同一帧内子组件先旧后新渲染两次。
- `ProcessInvalidations()` 本身不是线程安全的；只能由 UI 线程调用。它与后台 `Invalidate()` 通过队列锁同步。

### 2.5 `ShouldRender()`

```csharp
protected virtual bool ShouldRender() => true;
```

- 在每次出队渲染前调用；返回 false 时跳过本次 `Render()`。
- 用于粗粒度性能保护（例如父组件只改了不影响输出的状态）；返回 false 会**跳过该组件整棵动态作用域**，
  包括块刷新与参数推送。子组件若自身被失效，仍会独立渲染。
- 代码后置可以 override；生成器不生成该成员，不冲突（spec/02 §10.1）。
- `ShouldRender()` 不得有副作用；抛异常按 `Render()` 异常处理。

### 2.6 重入与循环

- 组件在自己的 `Render()` 中调用 `Invalidate()`：当前 `Render()` 完成后重新入队，按 §2.4 的迭代上限处理。
- A 的 `Render()` 失效 B、B 的 `Render()` 又失效 A：同轮继续处理，直到队列空或触顶。
- 子组件在父组件 `Render()` 中被失效：子组件入队，深度大于父，随后处理。
- `Invalidate()` 在 `Build()` 期间调用：no-op（未挂载）；`Build()` 的首次求值已读取当前状态。
- `Invalidate()` 在 `Dispose()` 期间/之后调用：no-op；已入队的组件在出队时被跳过。

### 2.7 所有权

```csharp
public void Adopt(Component child)
```

- 每个组件最多有一个所有者；`Adopt` 幂等（同一所有者重复调用无副作用），不同所有者 → `InvalidOperationException`。
- `Adopt` 接管时**迁移**子组件原根队列中已入队的待渲染失效：按入队顺序取出并重入新根队列，
  与新队列中已有失效按待渲染标记合并；接管前入队的失效不得被静默丢弃（D78）。
- 深度不缓存：排序时按所有权链实时计算，接管改变链长后不残留旧深度（保证 §2.4 的父先于子）。
- `Track(Component)` 等价于 `Adopt(child)` + 登记释放（spec/02 §5.3）。
- 组件内容中的子组件用 `receiver.TrackContent(child)`：所有者是**接收方**，随接收方释放，而不是随模板父组件释放。
- 块/条目作用域内的子组件不加入 `Track`（否则随整组件释放，而不是随条目释放），
  必须显式 `Adopt`，再把组件加入 `BlockScope.Disposables`。
- `Dispose()` 先释放 `Disposables`/`_tracked` 中的子组件，再清空队列待渲染标记；子组件失效不再入队。
- 所有权只用于失效队列与生命周期，不改变元素树父子关系；组件不是 `Element`。

### 2.8 `Dispose()`

- 释放顺序：按登记**逆序**释放（子组件/绑定/块先于更早的资源），然后标记 `_disposed`，最后清空待渲染标记。
- 幂等；释放后 `Invalidate()`/`Mount()` no-op；`Track` 新资源立即释放。
- `ForEachBlock.Dispose` 必须释放所有仍存在条目的 `BlockScope.Disposables`（嵌套绑定/子组件/子块）；
  `ConditionalBlock.Dispose` 释放当前分支的内容作用域。
- 组件内容中的子组件由接收方 `TrackContent` 登记，随接收方 `Dispose()` 释放（不是随模板父组件）。
- 释放后不得再执行渲染动作；块的 `Refresh()` 在 `Dispose()` 后 no-op。
- 释放不要求从失效队列移除对象；出队时按 `_disposed` 跳过（允许一帧的强引用延迟）。

## 3. 保留树与生成类状态

### 3.1 保留什么

- `Build()` 构造的元素实例跨渲染保留；`Render()` 只改属性/文本/列表结构，不重新 `new` 静态元素。
- 控件局部状态（焦点、按下态、文本选择、滚动）在动态属性更新与 Keyed 复用中保留；
  `@if` 分支切换与条目重建会丢失对应子树状态（§6/§7）。
- 组件实例跨父渲染保留；参数推送只改属性并失效子组件，不重建子组件。

### 3.2 生成类的字段

生成器只在**跨方法引用**时提升为字段；其余保持 `Build()`/模板内的局部变量（spec/02 §3）。

| 字段前缀 | 内容 | 何时需要字段 |
|---|---|---|
| `__el{N}` | 控件元素 | 根级动态属性/文本需要 `Render` 再次写入 |
| `__run{N}` | 动态文本 run | 根级 `TextRuns` 动态段 |
| `__cmp{N}` | 组件实例 | 根级动态参数或 `@bind` 参数需要推送 |
| `__blk{N}` | `ConditionalBlock`/`ForEachBlock<T>` | 根级块需要 `Refresh()` |
| `__bind{N}` | `UiBinding` | 根级 `@bind` 需要 `Refresh()` |

- 字段只在 `Build()` 内赋值一次；`Render()` 只读字段。
- 块内容/条目内的动态节点是局部变量，由块的渲染动作闭包捕获；不提升为字段。
- 生成字段名保留 `__` 前缀；代码后置声明同名成员 → `EUI045`。

### 3.3 动态赋值方法 `U{N}`

- 根级动态赋值生成 `private void U{N}()`（实例方法），方法体只含一个绑定点的求值与写入。
- `Build()` 在源码位置调用 `U{N}()` 完成首次求值；`Render()` 按源码顺序再次调用。
- 表达式在生成文本中**只出现一次**（在 `U{N}` 方法体内），避免重复 C# 诊断与 `#line` 歧义。
- `U{N}` 不得有参数、返回值、泛型、`async`；不得捕获局部变量（只能访问字段与 `this`）。
- 控件属性/文本/组件参数三类绑定都使用 `U{N}`；`@bind` 的控件目标由 `UiBinding` 承担，
  组件目标由 `U{N}`（推送）+ `PChanged` 回调（回写）共同承担。

### 3.4 块内渲染动作 `R{N}`

- 块内容/条目模板内的动态绑定生成局部函数 `void R{N}()`，在模板构造时调用一次，并加入 `BlockScope.RenderActions`。
- 刷新时块重跑 `R{N}()`；`R{N}` 只访问闭包捕获的局部元素/run/组件/绑定与 `this`。
- 嵌套块在 `R{N}` 中调用 `Refresh()`；嵌套块自己的内容由其自己的 `R` 负责。
- 表达式同样只出现一次（在 `R{N}` 内）。

## 4. 动态表达式

### 4.1 哪些表达式是动态的

- 模板中所有 **`{expr}` 值位置**都是动态的：`P={expr}`、文本 `{expr}`、`@if (...)`、`@foreach (...)`、
  `Key={expr}`、`@bind-P={expr}` 的源、组件动态参数、`Ref={expr}`（G3 仅 Build 求值）。
- 字面量（双引号字符串）是静态的，只在 Build 时转换与赋值。
- 事件处理器表达式是**build-once**：控件 `On*`、组件委托参数。不参与渲染重算（§9）。
- `Class`/`Style`/附加属性按 G4 定义；实现前的保留行为不参与渲染。
  具名插槽（✅ C6）不参与渲染重算，规则见 §10.4。

### 4.2 求值时机

| 构造 | Build（首次） | 每次所属作用域渲染/刷新 | 备注 |
|---|---|---|---|
| 字面量 | 求值一次 | 不重算 | 编译期转换 |
| `P={expr}` 控件属性 | `U{N}()` | `U{N}()` | 属性引擎相等短路 |
| `P={expr}` 组件参数 | `U{N}()` | `U{N}()` | 相等保护 + 子组件 `Invalidate()` |
| 文本 `{expr}`（`Text`） | `R`/`U` 赋值 | 重设整段字符串 | 一个文本节点一个插值串 |
| 文本 `{expr}`（`TextRuns`） | 动态 run 赋值 | 只重设 run.Text | 静态 run 不重算 |
| `@if` 条件 | `SetCondition` + 首次 `Refresh()` | `Refresh()` 重算条件 | 分支切换重建 |
| `@if` 内容绑定 | 内容构造时 `R` 首次求值 | 分支存活时重跑 `R` | 新建分支不重复跑 |
| `@foreach` 集合 | `SetTemplate` 后首次 `Refresh()` | `Refresh()` 重新枚举 | 任意 `IEnumerable<T>` |
| `Key={expr}` | 首次枚举 | 每次枚举每个 item | 不得为空表达式 |
| item 内容绑定 | 条目构造时 `R` 首次求值 | 复用条目重跑 `R`；重建条目重新构造 | 见 §7.3 |
| `@bind` 源 | `UiBinding` 构造：源 → 目标 | `Refresh()`：源 → 目标 | 目标变化回写源 |
| 组件事件/委托参数 | 赋值一次 | 不重算 | `Invalidate()` 包装 |
| 控件事件 | `AddHandler` 一次 | 不重算 | `Invalidate()` 包装 |
| `Ref={field}`（G3） | 赋值一次 | 不重算 | 保留 |

### 4.3 Build 与 Render 的关系

- `Build()` 是"第 0 次渲染"：它必须完成所有动态表达式的首次求值，保证 `Mount()` 返回时树已完整。
- `Render()` 是第 1..N 次渲染：重算同一批表达式；不新增构造语句。
- 同一个表达式在生成文本中只出现一次（§3.3/§3.4）；首次求值通过调用 `U{N}()`/`R{N}()` 完成。
- 控件属性/文本的首次与后续求值结果相同则写入被属性引擎短路；组件参数由生成代码显式做相等保护。
- 顺序是契约的一部分：`Render()` 按根级动态构造的源码顺序调用 `U`/`Refresh`；块内按 `R` 内的源码顺序。

### 4.4 表达式的可见名字

| 位置 | 可见名字 |
|---|---|
| 根级（元素/属性/文本/条件/集合/Key） | code-behind 实例成员（`this` 隐式）、同命名空间类型简单名、全限定类型 |
| `@if` 内容 | 同上；不能声明局部变量 |
| `@foreach` item | item 变量 + code-behind 实例成员 + 外层 item 变量（嵌套时） |
| item 内的嵌套块 | 内层 item 变量遮蔽同名外层变量（C# 规则）；其余同上 |
| `@bind` 源/Key/条件/集合 | 与所在位置相同 |

- 模板不声明局部变量、类型参数；类成员与命名空间导入按 **spec/01 §1.5 目标语法**
  （`@code` / `@using`，K21/K22/K23 ✅ S6 已实现（2026-11-08），偏差 D85/D86）；复杂逻辑放 code-behind 或 `@code`。
- 当前实现：生成文件不引入 `using`，简单类型名按生成类所在命名空间解析（spec/02 §2.2）。
  **K22（✅ S6 已实现（2026-11-08））**：生成文件保留模板 `@using` 对应的普通 using，为 `@code` 原文提供命名空间；
  生成代码自身仍一律 `global::` 全限定。
- 不使用 Roslyn 重写表达式；除文本插值的 `$"..."` 与必要的括号/目标类型转换外，表达式原样输出。

### 4.5 纯度要求

- 表达式可能在 Build 与每次 Render 中重复求值；必须无副作用、无 I/O、无随机、无时间依赖。
- `@foreach` 集合每次刷新重新枚举；不得使用一次性枚举器、无限序列或带副作用的 LINQ。
- `@bind` 的读写属性 getter/setter 必须相互一致；getter 有副作用会破坏相等保护。
- 需要副作用时写在事件处理器/code-behind 方法中，再显式 `Invalidate()`。

### 4.6 旧 `Signal<T>` 与状态生成器

- `Signal<T>` 仍是运行时类型，但**不是模板反应式机制**：模板不会自动订阅信号，也不会把裸名重写为 `.Value`。
- 若 code-behind 仍保留 `Signal<T>` 字段，模板必须显式写 `Count.Value`；写入 `Count.Value = x` 不会自动刷新 UI，
  需要再调用 `Invalidate()`。
- `[UiState]`/`[UiObservable]`/`[UiComputed]` 及状态生成器不属于 v4；模板编译器不读取这些特性。
  已有代码迁移见 §15。

## 5. `P={expr}` 与文本

### 5.1 控件属性

- 目标必须是 public 可写属性并存在 `{Name}Property`（`EUI019`/`EUI002`）。
- 每次 `Render` 重新求值并写公开 setter；写入走 `StyledProperty` 引擎，相等值短路，不产生 `PropertyChanged`。
- 不得调用 `SetValue/GetValue` 绕过属性引擎（R3）。
- 控件属性的变化只标脏布局/绘制，不需要组件 `Invalidate()`；`Render` 本身由其它原因触发。

### 5.2 组件参数

- 目标必须是 `[UiParameter]` + public setter；类型任意（受 §10.1 约束）。
- 动态参数推送生成：

```csharp
private void U0()
{
    var v = (global::System.String?)(OwnerName);
    if (!global::System.Collections.Generic.EqualityComparer<global::System.String?>.Default.Equals(__cmp0.Name, v))
    {
        __cmp0.Name = v;
        __cmp0.Invalidate();
    }
}
```

- 先转型到参数声明类型，再比较；相等则**不赋值、不失效**子组件。
- 赋值走公开 setter；不写生成字段。
- 首次 `Build` 调用 `U0()` 时子组件尚未 `Mount`，`Invalidate()` 是 no-op；参数值在 `Mount` 前已就位。
- 组件参数没有 diff、不重建组件；子组件 `Render` 重算自己的动态表达式。
- 参数为委托类型时按"组件事件"处理（§9.2），不生成 `U`、不参与渲染重算。
- `@bind-P` 与 `P=` 不能同时用于同一参数（`EUI033`）。

### 5.3 文本：`Text` 模型

- 全静态：Build 合并静态段，赋一次字符串。
- 含 `{expr}`：一个文本节点生成**一个完整插值串**，每次渲染重设目标字符串属性。
- 插值使用 C# `$"..."`：当前区域文化、`ToString()` 语义；不支持格式说明符（写 `.ToString("F2")`）。
- 静态段中的 `{`/`}` 写 `{{`/`}}`；`\`/`"` 按 C# 转义。
- 目标属性由内容模型元数据给出；`Text` 目标 + 显式同名属性/`P={expr}` → `EUI033`。

### 5.4 文本：`TextRuns` 模型

- 连续静态段合并为一个 `AddRun("...")`，Build 一次，不保留变量。
- 每个动态段 `AddRun("")` 创建空 run；Build 调用 `R`/`U` 设初值，后续只重设 `run.Text`。
- 静态 run 与动态 run 按源码顺序创建；不重建 run，不重新 `AddRun`。
- 富文本/多样式 run 不在 v1 范围（G4）。

### 5.5 重复赋值

- 同一目标同时被文本内容与显式属性/`P=` 写入 → `EUI033`；生成器只保留一个写入。
- 同一属性重复出现 → `EUI025`。
- 同一组件参数同时 `P=` 与 `@bind-P` → `EUI033`。

## 6. `@if`

### 6.1 语义

- 条件必须是 C# `bool` 表达式；Build 首次求值，之后每次组件渲染由块 `Refresh()` 重算。
- 条件为 true 时构造并挂载分支内容；false 时不构造。
- 条件在两次渲染间不变：分支内容保留，重跑分支的渲染动作。
- 条件翻转：销毁旧分支（释放其作用域），构造新分支；新分支的渲染动作在构造时已执行一次，不再重复。
- 分支内元素局部状态（焦点、滚动）在翻转时丢失；这是结构性重建，不是 diff。

### 6.2 块 API

```csharp
public void SetCondition(Func<bool> condition);
public void SetContent(Action<BlockScope> contentBuilder);
public void Refresh();
public void Dispose();
```

- 顺序固定：`new` → 挂父级 → `SetCondition` → `SetContent` → 作用域登记（`Track`/`Disposables.Add`）→ 显式 `Refresh()`。
- `SetCondition`/`SetContent` 只保存，不刷新；模板由块创建 `BlockScope` 并传入，模板用
  `AddRoot`/`AddRenderAction`/`AddDisposable` 登记。
- 首次求值由生成代码在登记后调用 `Refresh()` 完成；`Track` 在 `Refresh` 之前，内容构造失败时块可释放。
- `Refresh()`：条件未变且为 true → 重跑当前 `BlockScope.RenderActions`；条件变化 → 释放旧内容作用域、
  清空子元素、按新条件构造内容；false 且原为 true → 只释放/清空。
- 空分支合法：不生成 `SetContent`；不生成元素。
- `Refresh()` 在 `Dispose()` 后 no-op；`Dispose()` 释放当前内容作用域。
- 块自身是 `IDisposable`；根级块 `Track`，内容/条目内块加入所在作用域的 `Disposables`。

### 6.3 嵌套

- `@if` 内容中的 `@if`/`@foreach` 是嵌套块；父分支的 `R` 中调用子块 `Refresh()`。
- 分支切换释放父作用域时，子块随 `Disposables` 释放；不得泄漏条目。

## 7. `@foreach`

### 7.1 集合要求

- 表达式必须是 `IEnumerable<T>`（含 `T[]`、`List<T>`、`Dictionary<K,V>`、`HashSet<T>`、LINQ 结果、
  自定义实现、`ObservableCollection<T>`）；`T` 必须可推断。
- 值为 `null` 视为空集合，不构造条目，不报错；`null` 字面量因无法推断 `T` 报 `EUI007`。
- `IAsyncEnumerable<T>`、`IEnumerator<T>`、非泛型 `IEnumerable`、`dynamic` → `EUI007`。
- 集合表达式每次刷新**重新求值并重新枚举**；不得是无限序列或一次性枚举器。
- 不要求 `INotifyCollectionChanged`；集合原地变化在下次刷新时自然被看到。`ObservableCollection<T>` 的变更事件
  不会自动触发渲染，仍需 `Invalidate()`。
- 不建议在 `@foreach` 表达式里做重 LINQ；在 code-behind 缓存结果并显式失效。

### 7.2 块 API

```csharp
public void SetItems(Func<IEnumerable<T>?> itemsGetter);
public void SetKeySelector(Func<T, object?> keySelector);
public void SetTemplate(Action<T, BlockScope> template);
public void Refresh();
public void Dispose();
```

- 顺序固定：`new` → 挂父级 → `SetItems` → `SetKeySelector`（有 `Key` 时）→ `SetTemplate` → 作用域登记 → 显式 `Refresh()`。
- 三个 setter 只保存，不自动刷新；模板由块创建 `BlockScope` 并传入，模板用
  `AddRoot`/`AddRenderAction`/`AddDisposable` 登记。
- 首次枚举由生成代码在登记后调用 `Refresh()` 完成；`Track` 在 `Refresh` 之前，模板失败时可释放。
- `Refresh()`：求值集合（null → 空）→ 重新枚举 → 按 Key 对齐 → 复用/重建/删除 → 更新子元素顺序。
- `Dispose()` 释放所有仍存在条目的 `BlockScope.Disposables`；之后 `Refresh()` no-op。
- 块自身是 `IDisposable`，登记规则同 `@if`。

### 7.3 Keyed 复用

- `Key={expr}` 只允许在 `@foreach` 条目模板的第一个元素根上；位置/形式错 → `EUI010`。
- 无 `Key` 时 key = item 自身（`(object?)item`）。
- Key 表达式返回 `null` 时归一到一个共享空键；多个 null key 条目按出现顺序复用。
- Key 比较按装箱后的 `Equals`/`GetHashCode`；重复 key 用 `Queue<Entry>` 按出现顺序匹配。
- **复用条件 = Key 相等且 item 身份相等**：
  - 引用类型：`ReferenceEquals(oldItem, newItem)`；
  - 值类型：`EqualityComparer<T>.Default.Equals(oldItem, newItem)`。
- 复用条目：保留元素实例，重跑该条目的 `BlockScope.RenderActions`。
- 同 Key 不同 item：释放旧条目作用域，**重建条目**；禁止复用捕获旧 item 的闭包。
- 新 Key：新建条目（模板构造时完成首次求值）。
- 未复用的旧条目：释放作用域；从 `Children` 移除。
- 条目顺序按本次枚举顺序；`Children` 只在根序列实际变化时更新（相同实例序列不重排、不触发额外布局）。

| 情况 | 结构 | 渲染动作 | 闭包/item |
|---|---|---|---|
| Key 同 + item 同 | 复用 | 重跑 | 捕获的 item 仍有效 |
| Key 同 + item 不同 | 重建 | 新模板首次求值 | 新闭包，旧闭包随旧作用域丢弃 |
| Key 不同/新增 | 新建 | 首次求值 | 新闭包 |
| 条目消失 | 释放+移除 | 不再执行 | 释放 |

- Key 的职责是**身份对齐**，不是数据版本；需要保留元素局部状态时保持 item 引用稳定。
- item 为值类型时，同 Key 且值相等才复用；值变化会重建条目并丢失局部状态。
- `IEnumerable<object>` 中的值类型 item 每次枚举都会产生新装箱，按引用比较会每次重建；建议用具体 `T`。

### 7.4 item 作用域

- item 变量是模板 lambda 的参数，作用域覆盖条目内容与嵌套块。
- item 模板可引用：item 成员、外层 item 变量（嵌套）、code-behind 实例成员。
  **不再禁止引用组件级状态**（v3 的 `EUI014` 移除）：条目渲染动作是生成类内的闭包，读取 `this` 的最新值。
- `Key` 可引用任意可读表达式（包括组件状态）；没有 v3 的 `EUI039`。Key 变化会导致条目重建，由作者负责稳定性。
- `@bind` 源可以 rooted at item，也可以 rooted at 组件状态；没有 v3 的 `EUI042`。
- item 模板可以有多个根元素；`Key` 只在第一个元素根上。
- item 内的事件处理器 build-once；同 Key 同 item 复用时保留，item 引用变化时随条目重建。

### 7.5 嵌套 `@foreach`

- 内层集合可以是外层 item 的成员、组件状态或任意 `IEnumerable<T>`；每次外层条目刷新时，
  外层 `R` 调用内层块 `Refresh()`，内层重新枚举。
- 内层块是外层条目作用域的资源：外层条目重建时释放内层块及其条目。
- 嵌套深度不限；作用域与释放按最近块归属。
- 内外层同名 item 变量按 C# 遮蔽规则处理；建议避免同名。

### 7.6 空集合与性能

- 空集合/null：块无子元素；已有条目全部释放。
- `Refresh()` 的成本是 O(n) 枚举 + O(n) 字典/队列操作 + 复用条目的渲染动作；
  这是 v4 的固定成本，不做增量集合监听。
- 大列表建议：拆分组件、缓存集合、避免每次渲染重新 LINQ、用稳定 item 引用与 Key。
- `Children` 更新只在根序列变化时发生；纯属性变化不会重排列表。

## 8. `@bind-P={expr}`

### 8.1 源的要求

- 值必须是 `{expression}`；字面量 → `EUI037`。
- 源必须是**可写左值**：字段、可写属性（含 `private`/`internal` setter，生成代码在同一 partial 类内）。
- 允许：`this.Count`、`Vm.Name`、`t.Done`（`t` 为引用类型 item）、`_state.Enabled`（`_state` 是字段）。
- 不允许（`EUI037`）：
  - 方法调用、索引器、`?.`、`??`、强制转换、`new`、lambda；
  - 静态成员、常量、只读字段、`init` setter；
  - 类型与目标属性不一致（忽略可空注解）；
  - 中间段是**属性返回的 struct**（`Vm.State.Enabled = v` 是 CS1612）；中间段是 struct 时必须是字段；
  - `@foreach` 的**值类型 item** 的成员（迭代变量只读）；
  - 目标为 `[UiContent]`/`[UiSlot]` 参数、事件/委托参数、保留属性（`Key`/`Ref`/`Class`/`Style`）。
- 静态无法判定的左值问题由 C# 经 `#line` 报错；能静态判定的必须报 `EUI037`。
- 初始方向：**源值胜出**（绑定构造时源 → 目标）。随后每次所属作用域刷新都源 → 目标。
- 源写入使用公开/可见 setter；不得写生成字段或绕过属性引擎。

### 8.2 控件目标

```csharp
public sealed class UiBinding : IDisposable
{
    public static UiBinding TwoWay<T>(
        Element element,
        StyledProperty<T> property,
        Func<T> readTarget,
        Action<T> writeTarget,
        Func<T> readSource,
        Action<T> writeSource,
        Action invalidate);

    public void Refresh();
    public void Dispose();
}
```

- 第三参必须是 `{OwnerType}.{Property}Property`，全限定；不得 `GetValue/SetValue`。
- `readTarget`/`writeTarget` 使用公开 CLR 属性（`() => el.Checked` / `v => el.Checked = v`）。
- 构造：`_syncing = true; writeTarget(readSource()); _syncing = false;` 再订阅 `element.PropertyChanged`。
- `Refresh()`：`var v = readSource(); if (!EqualityComparer<T>.Default.Equals(readTarget(), v)) { _syncing = true; writeTarget(v); _syncing = false; }`。
- 目标变化：属性事件匹配 `property` 且 `_syncing == false` 时，读取目标值；与源相等则忽略，
  否则 `writeSource(v); invalidate();`。相等保护避免无意义失效。
- `invalidate` 由生成代码传入所属组件的 `Invalidate`；绑定在块/条目内也失效同一个组件。
- `Dispose` 解除 `PropertyChanged` 订阅；幂等。
- 根级绑定为 `__bind{N}` 字段并 `Track`；块/条目内为局部变量并加入 `Disposables`。

### 8.3 组件目标

组件参数双向不使用 `UiBinding`，而是"父推送 + 子回调"：

- 组件必须为目标参数 `P` 声明回调参数：

```csharp
[UiParameter] public string Title { get; set; } = "";
[UiParameter] public Action<string>? TitleChanged { get; set; }
```

- 回调名固定 `{P}Changed`；类型必须是 `Action<T>`（`T` 与 `P` 类型一致，可空标注忽略）；
  缺失、类型不符、非 `[UiParameter]`、非 public setter → `EUI040`。
- 父模板 `@bind-P={expr}` 生成：
  1. Build：`__cmp.PChanged = v => { if (!EqualityComparer<T>.Default.Equals(expr, v)) { expr = v; Invalidate(); } };`
  2. Build：调用推送方法 `U{N}()`（源 → 子参数，相等保护 + 子 `Invalidate()`）；
  3. Render：再次调用 `U{N}()`。
- 子组件内部修改参数时由组件作者负责：

```csharp
private void Cycle()
{
    var next = Color == "teal" ? "amber" : "teal";
    Color = next;
    ColorChanged?.Invoke(next);
    Invalidate();
}
```

- 回调必须在**用户/内部动作**中调用，不得在 `Render()` 中无条件调用（会造成父/子互相失效）。
- 回调可以为 null（父未使用 `@bind`）；子组件必须 null 检查。
- 父未使用 `@bind` 时，子对参数的修改只在子内部生效；下次父渲染会推送父源值覆盖。
- 子组件调用 `PChanged` 后，父组件回写源并 `Invalidate()`；父渲染再推送同值，相等保护阻止回环。

### 8.4 item 模板内绑定

- 控件目标：`UiBinding` 局部变量加入条目 `Disposables`；`invalidate: Invalidate` 失效组件。
- 组件目标：`PChanged` 回调与 `U{N}` 在条目模板内生成；`U{N}` 是 item 模板的局部函数 `R` 的一部分，
  不提升为根级 `U` 方法。
- 同 Key 同 item 复用时绑定保留；条目重建时旧绑定 Dispose，新绑定重新构造。
- 源可以是组件状态；item 变化触发组件失效后，重新枚举并复用/重建条目。

### 8.5 循环与一致性

- `UiBinding._syncing` 阻止绑定自身写目标时回写源。
- 相等保护在三个方向都存在：`Refresh` 写目标前、目标变化写源前、组件参数推送前。
- 属性 setter 若做归一化（clamp/格式化），getter 必须返回归一化后的值；否则每次渲染可能反复写目标。
- 源 getter 有副作用会破坏相等保护；禁止。

### 8.6 旧语法

- `P:bind={expr}` ≡ `@bind-P={expr}`；`P:value={expr}` ≡ `P={expr}`。
- 两者从 S3 起报 `EUI043` Warning，至少保留一个发布周期；S5 评估移除。
- 旧语法不再接受 `Signal<T>` 对象作为特殊源；信号必须显式写 `.Value`（§4.6）。
- 迁移：`:bind` → `@bind-P`；`:value` → `P=`；`Signal<T>` 字段 → 普通字段/属性。

## 9. 事件与失效

### 9.1 控件事件

- 属性名 `On{EventName}`，对应 `{Name}Event` 的 `RoutedEvent` 静态字段。
- 只接受 `{expression}`；字面量/`:bind`/`@bind-` → `EUI004`。
- 处理器必须是 `Action` 或 `Action<RoutedEventArgs>` 可转换；签名不匹配 → `EUI024`（可静态判定）/C# 错误。
- **build-once**：Build 时 `AddHandler`，不在渲染时重新注册。
- 生成包装：处理器执行后调用所属组件的 `Invalidate()`：

```csharp
var h0 = (global::System.Action)(() => AddTask());
el.AddHandler(global::EclipseUI.Elements.Button.ClickEvent,
    () => { h0(); Invalidate(); });

var h1 = (global::System.Action<global::EclipseUI.RoutedEventArgs>)(
    e => OnSomeEvent(e));
el.AddHandler(SomeEvent, e => { h1(e); Invalidate(); });
```

- 处理器表达式只出现一次（`h{N}` 初始化）；包装 lambda 只调用委托再失效，不含用户表达式。

- 处理器抛异常时不执行自动 `Invalidate()`（异常向上传播）；组件保持当前 UI，直到下一次失效。
- 事件处理器里的读取发生在触发时；不存在"事件体依赖分析"。
- 事件处理器捕获 item：同 Key 同 item 复用安全；item 引用变化时条目重建。

### 9.2 组件委托参数

- 组件的委托类型 `[UiParameter]` 就是事件；模板属性名就是参数名，不做 `On*` 推断。
- 只接受 `{expression}`；字面量 → `EUI004`。
- **build-once**：Build 时赋值；父渲染不重新生成委托。
- 委托必须返回 `void`，参数不得含 `ref`/`out`/`in`；否则 `EUI028`。
- 生成包装：调用用户处理器后失效**父组件**（提供委托的一方）：

```csharp
var h0 = (global::System.Action)(() => Close());
__cmp.OnClose = () => { h0(); Invalidate(); };

var h1 = (global::System.Action<global::System.Int32>)(x => Select(x));
__cmp.OnSelect = a0 => { h1(a0); Invalidate(); };
```

- 参数个数由委托的 `Invoke` 签名决定；包装 lambda 参数名固定 `a0`..`aN`，省略类型（目标委托推断）。
- 用户处理器先写入 `h{N}`，包装只调用它并失效；处理器表达式只出现一次。
- 子组件在内部动作中调用委托；不得在自身 `Render()` 中无条件调用（避免循环失效）。
- 子组件内部状态变化仍由子组件自己的 `Invalidate()` 负责；父组件是否重算由父决定。

### 9.3 异步与后台线程

- 控件事件包装在处理器**同步返回后**失效；`async void` 处理器在第一个 `await` 前返回，
  后续状态变化必须显式 `Invalidate()`：

```csharp
private async void Load()
{
    Tasks = await _api.GetTasksAsync();
    Invalidate();          // 必须：await 之后不会自动失效
}
```

- 后台线程修改状态后调用 `Invalidate()`；它是线程安全的。状态字段本身的跨线程可见性由用户负责
  （`volatile`/锁/不可变替换）。
- 定时器/动画回调同样：修改状态 + `Invalidate()`；或直接修改元素属性（属性引擎自行标脏）。
- 不提供 `InvokeAsync` 包装；宿主负责 UI 线程调度。`Invalidate()` 只入队，不执行用户代码。

### 9.4 事件与参数推送的顺序

- 父组件事件处理器 → 修改状态 → 自动 `Invalidate()` 父组件 → 父 `Render()` 推送参数 →
  子组件 `Invalidate()` → 子 `Render()`。
- 子组件事件 → 子组件自动 `Invalidate()`；若子调用父回调，父也失效。
- 同一帧内多次失效合并；深度升序保证父先于子。

## 10. 组件与参数

### 10.1 参数声明

- `[UiParameter]` 标在 `public` 属性上；必须有 `public` setter（`init` 不允许）。
- 参数类型任意，但必须是合法泛型实参：不得是 `ref struct`/指针/`Span<T>`（生成代码用
  `EqualityComparer<T>` 做相等保护）；否则 `EUI028`。
- 委托参数按 §9.2：`void` 返回、无 `ref/out/in`；否则 `EUI028`。
- 必填参数 `[UiParameter(Required = true)]`；缺参 `EUI027`。
- 不使用 C# `required` 修饰符（生成形态是 `new C()` 后逐条赋值）；`required`/`init` → `EUI028`。
- 参数值来自普通属性/字段；不要求 `[UiState]`、不要求 partial、不要求可观察。

### 10.2 参数推送

- 父组件的动态参数表达式在 Build 与每次 Render 中求值（§5.2）。
- 值变化时才写子参数并 `Invalidate()` 子组件；相等保护使用 `EqualityComparer<T>.Default`。
- 参数赋值在子组件 `Mount()` 之前完成；子组件首次 `Build` 读取到正确参数。
- 无参数 diff、不重建子组件；子组件 `Render` 重算自己的动态表达式。
- 父组件 `ShouldRender()` 返回 false 时不推送参数；子组件保留旧参数，直到父下次渲染。
- 子组件内部修改参数不自动通知父；需要向上同步时使用 `PChanged`（§8.3）。

### 10.3 构造顺序

```text
new cmp
→ 作用域登记（Track(cmp) / Adopt + Disposables.Add / receiver.TrackContent）
→ 参数（源码顺序：静态赋值、动态推送 U{N}()、事件/@bind 回调混合）
→ 默认内容（若有）
→ cmp.Mount()
→ parent.Children.Add(cmp.Root)（组件为内容根时返回 cmp.Root）
```

- 作用域登记必须在参数/内容之前：参数 setter、内容构建或 `Mount()` 失败时，调用方仍可释放组件。
- 参数/回调必须在 `Mount()` 之前：子组件首次 Build 读取参数。
- 块内组件用 `Adopt(cmp)` + `scope{N}.AddDisposable(cmp)`；组件内容内的子组件用 `receiver.TrackContent(cmp)`。
- `cmp.Root` 在 `Mount()` 后才非空；生成器不得提前访问。
- 组件为文档根时：登记 → 参数 → 内容 → `Mount()` → `return cmp.Root;`，不挂父级。

### 10.4 内容与插槽

- 默认内容（`[UiContent]`）与具名插槽（✅ C6，spec/01 §4.15 / spec/02 §4.12.5）是 **build-once**：父组件只在构造时生成内容，渲染时不重建。
  插槽的宿主拼接（`<ContentSlot>`）发生在接收组件自身 `Build()` 的该位置，因此槽内容也不随接收组件的失效重算。
- 内容参数不可作为 `@bind` 目标（`EUI040`）；`P={expr}` 也不写内容参数（`EUI021`）。
- 内容**体内**不得出现 `@bind`（`EUI040`）：内容没有渲染动作作用域，无法刷新绑定。
- 内容中的动态表达式属于内容的构建作用域：在 Build 时求值一次；父组件后续渲染不重算内容。
  其中的 `@if`/`@foreach` 生成普通 C# `if`/`foreach`，也是 Build 时快照，不参与失效重算。
- 内容随父状态变化的方式：把**组件标签**放进父模板的 `@if`/`@foreach`，由块重建整个组件与内容；
  或让内容自身是一个独立组件。
- 内容中的子组件用 `receiver.TrackContent(cmp)` 登记到接收方，随接收方释放；不得 `Track` 到模板父组件。
- 内容中的事件处理器仍失效模板父组件（表达式属于父模板）；接收方不得比父组件活得更久。
- 插槽非法使用报 `EUI022`（✅ C6）；把 `Slot`/`ContentSlot` 注册成控件标签名是保留冲突，报 `EUI031` 并隐藏该标签。

## 11. 诊断

### 11.1 v4 诊断口径

| 代码 | v4 触发 | 位置 | 级别 | 阶段 |
|---|---|---|---|---|
| `EUI007` | `@foreach` 集合不是 `IEnumerable<T>`、`T` 无法推断、`IAsyncEnumerable`/`IEnumerator`/非泛型/`dynamic` | 集合表达式 | Error | S2 |
| `EUI010` | `Key` 位置错（非条目第一个元素根）、字面量、空表达式 | 属性 | Error | S2 |
| `EUI033` | 同一目标重复赋值（文本+显式目标；`P=`+`@bind`；`P=`+`:bind`/`:value`；`@bind`+其它写入） | 属性值/文本 | Error | S2/S3 |
| `EUI037` | `@bind` 源非法（非可写左值/静态/const/readonly/init/方法/索引器/`?.`/struct 属性中间段/值类型 item 成员/类型不符） | 表达式 | Error | ✅ S3（控件与 item 源；自绑静态不可判定不报） |
| `EUI040` | `@bind` 目标非法（控件不可写/无 `{Name}Property`；组件参数无 public setter/委托/内容/保留；组件缺少 `{P}Changed` 或类型不符）；组件内容体内出现 `@bind` | 属性名/内容 | Error | ✅ S3（控件与组件侧；自绑静态不可判定不报） |
| `EUI043` | 旧语法 `:bind`/`:value` 弃用 | 属性名 | Warning | ✅ S3 |
| `EUI045` | code-behind 与生成成员冲突（`Build`/`Render`/`U{N}`/`__` 字段/基类/隐藏 `Invalidate` 等） | code-behind 声明 | Error | ✅ 2026-11-08（D57/D80；spec/02 §10） |
| `EUI100` | 纯语法错误（标签不匹配、属性缺值、未闭合注释、非法 `@bind-` 形态） | 语法位置 | Error | ✅ |

其余 `EUI001`–`EUI006`、`EUI008`–`EUI009`、`EUI011`–`EUI013`、`EUI015`–`EUI031` 的语义按 spec/01 §6.2（v4 修订版）为准；其中：

- `EUI025`（完全重复属性）保持 Error；
- `EUI026`（元数据声明非法）保持；
- `EUI027`/`EUI028` 的组件参数规则按 §10.1 修订；
- `EUI029`/`EUI030`/`EUI031` 保留语法不变。

### 11.2 停用诊断

以下代码在 v4 **永不发出**，保留编号不复用：

| 代码 | v3 含义 | 停用原因 |
|---|---|---|
| `EUI014` | item 模板禁止组件级状态 | 条目闭包可读 `this`，不再限制 |
| `EUI032` | 原"非 Signal 组件参数" | 已由 EUI044 取代，而 EUI044 也停用 |
| `EUI034` | 非法 `[UiState]`/`[UiObservable]` | 状态生成器移除 |
| `EUI035` | 非法 `[UiComputed]` | 移除 |
| `EUI036` | 反应式位置读取不可观察成员 → 一次性 | 无依赖分析，所有表达式合法 |
| `EUI038` | 反应式位置含不可分析方法/索引器/lambda → 一次性 | 同上 |
| `EUI039` | `Key` 依赖可观察状态 | Key 可引用任意可读表达式 |
| `EUI041` | 集合未实现 INCC | 每次刷新重新枚举，无 INCC 要求 |
| `EUI042` | item `@bind` rooted at 组件状态 | 允许组件级源 |
| `EUI044` | 非可观察组件参数不刷新 | 所有参数推送 + `Invalidate()` |

- 分析器/生成器不得再发出这些代码；测试必须断言零出现。
- spec/01 §6.2 保留这些行并标注"停用"，避免编号复用造成语义漂移。
- 新增诊断从 `EUI046` 起。

## 12. 性能模型

### 12.1 单次失效成本

```text
O(根级动态绑定数 + 根级块数 + Σ 块刷新成本)
块刷新成本：
  @if     O(1 + 分支渲染动作数)（条件不变）/ O(子树构造)（分支切换）
  @foreach O(n) 枚举 + O(n) Key 对齐 + O(复用条目渲染动作数) + O(新建条目构造)
```

- 不做依赖分析，因此失效后重算该组件**全部**动态表达式，而不是一条路径。
- 控件属性写入由 `StyledProperty.SetValue` 相等短路；相同值不触发 `PropertyChanged`/布局/绘制。
- 组件参数推送有显式相等保护；相同值不失效子组件。
- 文本赋值每次渲染都会写目标；`TextRun.Text` 是 `StyledProperty`，相同字符串短路。
- `@foreach` 每次渲染重新枚举；空转成本与列表长度线性相关，与变更数量无关。
- 静态元素/静态文本不参与渲染重算；没有虚拟 DOM，也没有整树 diff。

### 12.2 设计建议

- **按组件切分**：大页面拆成子组件，子组件状态变化只失效子组件；父组件不重算。
- **列表保持稳定引用**：复用条目保留元素与焦点；避免每次渲染都重新创建 item 对象。
- **避免重 LINQ**：在 code-behind 缓存投影结果；集合变化后显式 `Invalidate()`。
- **粗粒度保护**：`ShouldRender()` 返回 false 跳过整棵动态作用域；只在确定输出不变时使用。
- **批量修改后一次失效**：连续修改多个字段只在末尾 `Invalidate()`；同帧多次失效会合并。
- **后台线程**：只做状态替换 + `Invalidate()`；渲染始终在 UI 线程。

### 12.3 与 v3 的取舍

| 维度 | v3 定向订阅 | v4 失效重算 |
|---|---|---|
| 状态类型 | 受限（可观察路径/INCC/INPC） | 任意普通类型 |
| 单次更新 | O(受影响绑定) | O(组件动态作用域) |
| 集合 | 需 INCC 才观察内容 | 每次重新枚举，无需 INCC |
| 表达式 | 需可静态分析 | 任意 C# 表达式 |
| 显式失效 | 不需要（写即通知） | 非 UI 事件变化需要 `Invalidate()` |
| 元素保留 | 是 | 是（无 diff） |
| 组件粒度 | 细 | 组件级（可用子组件切分） |

选型理由见 §1.1；不存在"v4 一定更快"的结论。基准测试必须在 S5 用真实模板与列表规模测量
（§14 T17）。

## 13. 阶段 S1–S5

### 13.1 阶段定义

| 阶段 | 内容 | 出口标准 |
|---|---|---|
| **S1** ✅ | `Component.Invalidate/ProcessInvalidations/Render/ShouldRender`；失效队列与合并；生成字段与 `U{N}`；根级 `P=`/文本重算；控件事件自动失效 | 根级动态属性/文本在失效后更新；同帧多次失效只渲染一次；Dispose 后 no-op |
| **S2** ✅ | `ConditionalBlock`/`ForEachBlock` v4；`BlockScope.RenderActions`；`@if` 分支切换；`@foreach` 重新枚举、null=空、Keyed 复用；嵌套块；`EUI033`（文本 + 显式目标） | 分支切换释放旧作用域；同 Key 同 item 复用、不同 item 重建；列表类型不受限 |
| **S3** ✅ | `UiBinding` v4；控件 `@bind`；item 绑定；`EUI033`（`P=` 与绑定冲突）/`EUI037`/`EUI040`/`EUI043` —— 控件侧 ✅（2026-11-02）；组件 `PChanged` 与组件 `@bind` 随 C4 ✅（2026-11-05，spec/02 D54/D56 关闭） | 双向绑定源值胜出；目标变化回写+失效；无回环；组件参数向上同步 |
| **S4** | 组件所有权/深度排序；参数推送与相等保护；异步/后台失效；`ShouldRender` 与异步失效的组合语义 | 父先于子渲染；后台失效合并；异步续体显式失效；无泄漏 |
| **S5** | 基准/预算；AOT/裁剪；死循环上限；诊断清理与最终审计（旧语法清理已随 S6 提前完成，K27） | 性能预算达标；零停用诊断；样例迁移完成（✅ S6） |

### 13.2 与 C/G 阶段的关系

- C1–C6（符号目录、控件事件、字面量、组件标签、`Ref`、具名插槽）按 design/14 §14.12，
  与 S1–S5 并行；S2/S3 依赖 C4 的组件标签与 `PChanged` 元数据。
  C6 实拆只做具名插槽，附加属性留 G4；泛型标签按 K19 暂缓、不排期（design/14 §14.12.6 范围说明）。
- G3 七个小切片全部收敛（块布局透明 ✅ 2026-11-05/D77、失效迁移 ✅ 2026-11-07/D78、`Ref` ✅ C5、逐文件增量 ✅ 2026-11-08、`#line` 列映射 ✅ 2026-11-08/D79、`EUI045` 冲突诊断 ✅ 2026-11-08/D57、诊断出口补全 ✅ 2026-11-08/D74 ②、EUI024 签名匹配 ✅ 2026-11-08/D82）与 G4（样式/主题/附加属性；泛型标签暂缓 K19）不改变本文语义。
  块布局透明只改排布参数，不改块的生命周期、`Refresh` 语义与作用域资源释放。
- 阶段标记按"规范已定、代码未实现"使用；未实现项必须出现在 spec/02 §12 与 design/03 §3.5.4。

### 13.3 语法对齐 S6（K23，✅ 已实现 2026-11-08）

- **S6「语法对齐」** 是 K23 ⑦ 新增切片，插在 G3 之后、G4 之前（按 design/16 Q8 推荐值登记）。
  **S6 是 design/09 的切片编号，不是本文反应式阶段 S1–S5 的延续**；§13.1 的阶段表不因此扩项。
  它只改**语法表面与生成类壳**：`@using`/`@code`/`else`/`@switch`/`@* *@`，目标形态见 spec/01 §1.5。
- **不改变本文的反应式语义**：K1–K18 的保留元素树 + `Invalidate()` → `Render()` 失效重算、
  `Build` 只跑一次、块 `Refresh` 与 Keyed 复用、参数相等保护、`@bind-P` 读写与推送语义全部原样。
- 多分支与 `switch` 的结构变化仍由块协调器承担（冻结线 7 的 K23 ③ 扩展），不引入渲染树 diff。
- 出口标准：spec/02 §12 的 D85–D88 全部关闭、样例与 golden 完成 `@using` 迁移、
  497 例基线全绿、零停用诊断；阶段 P0–P6 见 design/16。

## 14. 契约测试矩阵

| # | 测试 | 输入 | 断言 |
|---|---|---|---|
| T1 | 根级重算 | `P={expr}`、文本 `{expr}` | Build 初值正确；`Invalidate` 后更新；相同值不触发属性事件 |
| T2 | 失效合并 | 同一帧多次 `Invalidate` | 只执行一次 `Render`；队列去重 |
| T3 | 事件自动失效 | 控件事件改变状态 | 处理器后自动渲染；异常时不自动渲染 |
| T4 | 后台失效 | 后台线程 `Invalidate` | 队列线程安全；UI 线程渲染；无竞态崩溃 |
| T5 | `@if` | 条件翻转/不变/空分支 | 翻转重建并释放旧作用域；不翻转只跑动作；空分支合法 |
| T6 | `@foreach` | 数组/List/字典/LINQ/null | 均可枚举；null 为空；`EUI007` 用例 |
| T7 | Keyed 复用 | 同 Key 同 item / 同 Key 新 item / 新 Key / 重排 | 复用/重建/新建/顺序正确；释放不泄漏 |
| T8 | item 绑定 | 控件 `@bind`、组件 `@bind`、组件级源 | 回写源并失效；条目重建后绑定正确 |
| T9 | 组件参数 | 静态/动态/`@bind`/委托 | 参数在 Mount 前就位；相同值不失效子；`PChanged` 回写 |
| T10 | 所有权/释放 | 嵌套组件、块内组件、分支切换、条目删除 | 逆序释放；队列跳过已释放；块释放条目资源 |
| T11 | 重入/循环 | `Render` 内 `Invalidate`、A↔B | 同轮处理；超上限抛异常；不挂起 |
| T12 | 直接修改 | 改 `List.Add`/item 属性但未 `Invalidate` | UI 不变（写契约） |
| T13 | 确定性 | 重复生成/打乱文件顺序 | 生成文本逐字节相同 |
| T14 | `#line` | 故意类型错误 | 诊断落在 `.ui` 行；每个表达式只报一次 |
| T15 | 样例 | CounterApp/TaskList/GalleryApp | 编译 0 error；运行冒烟（增删/勾选/条件） |
| T16 | 诊断 | 每个 EUI 触发用例 | 代码/span/级别稳定；停用码零出现 |
| T17 | 性能（S5） | 1k/10k 列表、频繁失效 | 预算内；无反射；分配可接受 |
| T18 | AOT/裁剪（S5） | 发布裁剪样例 | 无反射警告/运行时失败 |

阶段门：S1 → T1–T4/T13/T14；S2 → T5–T7/T11/T15；S3 → T8/T9；S4 → T10/T12/T15；S5 → T17/T18。

**S1 门禁（2026-10-30 通过）**：T1/T3 见 `tests/EclipseUI.Headless.Tests/S1AcceptanceTests.cs`，T2/T4 见
`InvalidationTests.cs`，T13/T14 见 `tests/EclipseUI.Compiler.Tests/GeneratorContractTests.cs`；解决方案
142 个测试全绿（Headless 77 / Compiler 4 / Rendering 61）。

**S2 门禁（2026-10-30 通过）**：T5/T6/T7 见 `tests/EclipseUI.Headless.Tests/BlockTests.cs`
（条件翻转/不翻转/空分支、数组/字典/LINQ/null、Keyed 复用/重建/重复 Key、条目释放）与
`tests/EclipseUI.Compiler.Tests/GeneratorContractTests.cs`（`@if`/`@foreach` 生成形态、任意集合与
嵌套 item 类型推断、`EUI007`）；T11 见 `InvalidationTests.cs`（重入同轮、超上限抛异常）；
T15 见 `samples/CounterApp`、`samples/GalleryApp` 与 `EndToEndTests.cs`（编译 0 error；增删/勾选/条件冒烟）。
164 个测试全绿（Headless 89 / Compiler 14 / Rendering 61）。

**S3 门禁（控件侧，2026-11-02 通过）**：T8 见 `tests/EclipseUI.Headless.Tests/EndToEndTests.cs`
（`TaskList` 条目内 `@bind-Checked={t.Done}`：回写 item、组件失效、同 Key 换实例后重建从源恢复、
条目移除后绑定释放）与 `GeneratorContractTests.cs`（`UiBinding.TwoWay` 形态、`@bind-P` 与 `P:bind`
生成文本逐字节相同、item 源与 `this.`/struct 字段中间段正例）；T16 的诊断用例覆盖 `EUI037`
（16 例：调用/索引器/`?.`/`??`/转换/`as`/`new`/lambda/静态/const/readonly/init/类型不符/属性返回
struct/值类型 item/名字不存在）、`EUI040`（事件与保留名目标）、`EUI043`（`:bind`/`:value` Warning）；
T15 见样例迁移到 `@bind-` 后 `CounterApp`/`GalleryApp` 编译 0 error 0 warning。
202 个测试全绿（Headless 102 / Compiler 39 / Rendering 61）。
组件 `@bind`、`PChanged` 与 T9 属 C4 依赖项，未在本阶段收敛（spec/02 D54/D56）。

**S3 门禁（组件侧，随 C4 于 2026-11-05 收敛）**：T9 三项出口全部有据——
参数在 `Mount()` 前就位（`GeneratorContractTests.SlotFill_AssignsAfterParametersAndContent_BeforeMount`
钉住「参数 < 默认内容 < 插槽 < `Mount()`」的发射顺序）；相同值不失效子
（`Emitter.EmitParamPush` 的根级 `U{N}` 与内层 `R{N}` 都用 `EqualityComparer<T>.Default.Equals`
守卫，`Invalidate()` 只在不等时发射）；`PChanged` 回写（`Emitter` 先赋 `{P}Changed = v => …`
再在源码位置推送，回写前同样做相等守卫以免回环）。诊断侧 `C4ComponentTests`
覆盖 `BindWithoutChangedCallback_IsEUI040`/`BindOnDelegateParameter_IsEUI040`/`BindInsideContent_IsEUI040`/
`DuplicateBoundComponentParameter_IsEUI033`，生成侧 `GeneratorContractTests.ComponentBindWithoutChanged_EUI040_BlocksGeneration`。
D54/D56 关闭。S3 至此整体 ✅；该切片收敛时 **409 个测试全绿（Headless 190 / Compiler 158 / Rendering 61）**。

**C1 目录派生（非 S 阶段，2026-11-02 通过）**：本阶段不改反应式与生成形态，只把控件元数据的来源换成符号。
与反应式的交点是 **D62**：`EUI040` 的「目标可绑」判定此前只来自手写目录的 `UiPropDef.Bindable` 标注，
现在派生目录对每个模板可见属性给出符号级 `CanWrite`（public 可写 + `{Name}Property`），
`DerivedCanWrite_CoversTheHandBindableFlags` 证明派生 ⊇ 手写标注——分析器已改读它（✅ C2）。
测试 `tests/EclipseUI.Compiler.Tests/CatalogScannerTests.cs` 42 例，当前 **257 个测试全绿
（Headless 103 / Compiler 93 / Rendering 61）**；表面与差集记录见 design/14 §14.12.1（C1）与
§14.12.2（C2：手写目录删除、事件表面 1 → 9 个事件名、golden 快照落成文件），新登记偏差 D63–D72（spec/02 §12）。

## 15. 迁移

### 15.1 从 v3 到 v4

| v3 写法 | v4 写法 | 说明 |
|---|---|---|
| `[UiState] partial class` + `public partial T P { get; set; }` | 普通类 + `public T P { get; set; }` | 去掉状态生成器 |
| 模板写 `P`（状态属性） | 模板写 `P` | 不变；普通属性直接读 |
| `Signal<T>` 字段 + 裸名 | 普通字段/属性；或显式 `Sig.Value` + `Invalidate()` | 不再自动订阅/重写 |
| `ObservableCollection<T>` | 任意 `IEnumerable<T>`（`List<T>`/数组/字典/LINQ） | `@foreach` 每次重新枚举 |
| 集合 `Add/Remove` 自动刷新 | `Add/Remove` 后 `Invalidate()`（UI 事件内自动） | 写契约 |
| item 属性变化自动刷新 | 修改后 `Invalidate()` | 写契约 |
| `P={method()}` 报 `EUI038` 且一次性 | `P={method()}` 每次渲染求值 | 无依赖分析 |
| `@foreach (var t in tasks.Value)` | `@foreach (var t in Tasks)` | 任意集合 |
| `@bind` 源必须是可观察路径 | 任意可写左值 | `EUI037` 收窄 |
| 组件可观察 partial 参数 | 普通参数 + `{P}Changed` 回调 | `EUI040` |
| `EUI036`/`EUI038`/`EUI041`/`EUI044` Warning | 无 | 停用 |
| `EUI014`/`EUI039`/`EUI042` Error | 无 | 停用 |
| 依赖分析/`UiWatch`/`IUiObservable` | 失效队列/`Render`/块刷新 | 运行时替换 |

### 15.2 迁移步骤

1. 把 `[UiState]`/`[UiObservable]`/`[UiComputed]` partial 属性改成普通属性/字段；
2. 把 `Signal<T>` 字段改成普通字段/属性；模板中的裸信号名改成 `.Value` 或普通属性；
3. 把集合类型改成最自然的 `IEnumerable<T>`（`List<T>`/数组/字典/LINQ）；
4. 在所有非 UI 事件的状态变化后调用 `Invalidate()`（后台线程、`await` 续体、定时器、直接集合修改）；
5. 把组件可观察参数改成普通参数 + `{P}Changed` 回调，子组件在内部修改参数时调用回调；
6. 删除依赖分析相关的诊断抑制/规避写法；
7. 用 `ShouldRender()`/子组件切分处理性能热点。

## 16. 变更规则

1. 新增失效/绑定/块语义：先改本文 + spec/01 对应语义 + spec/02 生成形态，再改代码。
2. 已冻结语义不得改义；必须改时走新语法 + 弃用期，并同步 design/03 §3.5.4 与 spec/02 §12。
3. 运行时 API 改签名：同步 spec/02 §7、golden、样例；提供 `Obsolete` 过渡。
4. 诊断码不复用；停用码保持停用；新增从 `EUI046` 起。
5. 每次变更跑：golden、确定性、`#line`、泄漏、样例编译（T1–T16）。
6. 性能相关变更必须附带 S5 基准数据；不得用"应该更快"替代测量。
