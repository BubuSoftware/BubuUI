# 02 · 生成代码契约

> 状态：根类型统一版，2026-11-13。当前实现与本文采用同一机制。
> 语法见 [01](./01-template-syntax.md)，更新和资源见 [03](./03-reactivity-and-binding.md)，目录见 [04](./04-ui-type-metadata.md)。

## 1. 不变量

| 编号 | 契约 |
|---|---|
| R1 | 编译阶段只读取 Roslyn 符号、模板和样式输入，不执行用户程序集或转换器 |
| R2 | 静态字面量在编译期校验和转换；表达式保留 C# 语义 |
| R3 | 根初始化当前实例；根与子项均通过公开 setter 或已声明的内容成员赋值 |
| R4 | 静态结构初始化一次；动态槽只重算值，结构变化交给块协调器 |
| R5 | 属性、回调和内容输入就位后才初始化子对象；不得提前读取派生类未初始化状态 |
| R6 | 新生成类型直接继承根 CLR 类型；无来源类别、根包装对象或平行所有权树 |
| R7 | StyledProperty 根默认值写模板层，使用点写 local 层；保留 CLR setter 校验和归一化 |
| R8 | 资源有明确作用域；失败时清理尚未挂接对象，成功后归真实内容树管理 |
| R9 | 同一输入逐字节生成同一输出；标识符和调用顺序稳定 |
| R10 | 用户表达式和代码块映射回原始文件；生成辅助代码保持 hidden |
| R11 | `@inject` 服务在 `InitializeUi` 最前解析（`base.InitializeUi()` 与根输入之前）；未注册或无已构建应用时抛 `InvalidOperationException`，不构建内容树 |

## 2. 文件与类

控件模板模式例外：带 `@template T` 的文件生成同名 `ControlTemplate<T>` 工厂和
`名称__TemplateView : 根类型`。视图有强类型 Owner，仍复用现有初始化、更新、块和源映射；
工厂每次构造新视图并返回 TemplateInstance。模板内部属性写 skin 层，不写 local/template 层。
普通文件继续采用下述根类型继承规则。详见 [design/28](../design/28-lookless-controls.md)。

- 文件名去扩展名就是类名，必须为合法 C# 标识符。`@namespace` 必填，必须与同名 code-behind 一致。
- `.bui` 本身是类型来源；没有 `.cs`、没有 `@code` 也合法。
- 生成 `partial class Name : RootClrType`。可访问性沿用同名源类型，独立模板默认 public。
- 根类型必须是目录内可继承、可合法构造的 UiObject 派生类型。sealed 和根继承环报 EUI064。
- code-behind 必须为顶层 partial 类；可省略基类，显式基类必须与解析后的根类型一致。
- `@inject TypeName Name` 生成注入属性 `public TypeName Name { get; private set; } = null!;`
  （§4.7）；属性名进入成员表面，重名报 EUI065，与生成保留名或 `@code`/code-behind 成员冲突报 EUI045。
- 根标签属性写 `this`，不再创建第二个根对象。根 Ref 同样指向 `this`。
- `Window` 是普通 UiObject 派生类型；它不继承 Element。内容位置按 Element 类型要求拒绝窗口，生成器无需辨认窗口标签名。

## 3. 编译管线

1. 扫描全部 `.bui` 的类名、命名空间、根类型、using/alias 与 `@code`。
2. 为模板声明建立同一 Roslyn partial 类型符号视图，解析 C# 根、同项目模板根、引用程序集根及继承成员。
3. 检测根继承环，扫描属性、事件、插槽和内容成员；目录是不可变的值相等数据。
4. 分析语法、标签作用域、赋值类型、绑定源和内容基数；语义探针在实际继承上下文内运行。
5. 分文件生成 C#，错误短路该文件产物；预检 EUI049 保留 Warning，由生成代码 C# 编译给出权威类型错误。
6. 输出 IDE 映射旁路数据；构建路径不开启映射捕获。

探针树继承输入 Compilation 的 ParseOptions，防止多个语法树 features 不一致。目录及缓存边界不得保存 Roslyn Symbol/Location/SyntaxTree；定位使用纯数据 span。

## 4. 初始化与动态更新

### 4.1 根初始化

```xml
@namespace Demo
@using BubuUI.Elements
<StackPanel Gap="8"><Button>OK</Button></StackPanel>
```

对应形态（省略映射与稳定编号细节）：

```csharp
public partial class Card : global::BubuUI.Elements.StackPanel
{
    protected override void InitializeUi()
    {
        using var construction = new global::BubuUI.UiConstructionScope();
        try
        {
            base.InitializeUi();
            using (this.BeginTemplateValues()) this.Gap = 8d;
            var button = new global::BubuUI.Elements.Button();
            construction.Track(button);
            button.Label = "OK";
            button.EnsureInitialized();
            this.Children.Add(button);
            construction.Complete();
        }
        catch (global::System.Exception error)
        {
            construction.Abort(error);
            throw;
        }
    }
}
```

根的普通 CLR 输入参数、默认内容参数和具名插槽在 `base.InitializeUi()` 之前赋值，让 C# 或模板基类消费输入。之后设置当前模板的 StyledProperty 默认值和结构内容。更新 override 先 `base.Render()`，再执行自身动态更新。

初始化在 `new` 和对象初始化器之后，由 EnsureInitialized 幂等触发；父集合挂接、宿主连接和测量自动确保初始化。需要在挂接前读取 Ref 的调用方可显式调用 EnsureInitialized。

完整的 InitializeUi 继承链返回后，运行时标记 IsInitialized 并调用一次 OnInitialized；生成器不在各层构建体中重复发通知。作者在 @code 中 override OnInitialized、OnAttached、OnDetached、OnUpdating、OnUpdated、OnDispose，调用 base 保留基类行为。OnUpdating/OnUpdated 由运行时包围完整 Render 继承链，与初次构建分开。生命周期钩子不占标签属性或路由事件表面。

没有显式默认内容的根声明保留调用方传入的默认内容参数，不生成空列表覆盖输入；只有写了默认内容子项时才赋该内容参数。

### 4.2 属性与文本

| 目标能力 | 字面量 | 表达式 |
|---|---|---|
| StyledProperty | 转换后公开 setter；根使用模板值 scope | 每次更新赋值，属性引擎短路相等值 |
| 普通可写 CLR 属性 | 转换后 setter | 转成目标类型，相等保护下 setter + 目标 Invalidate |
| void 委托 | 拒绝，EUI004 | 初始化时构造处理器并包装所属对象 Invalidate |
| 非 void 委托 | 按值能力处理 | 普通值表达式，不包装为事件 |
| 文本 | 静态 Text/TextRun | 动态 Text/TextRun 槽重算 |

根层动态槽为 `U{N}` 实例方法，捕获目标提升为字段；根自身直接用 this。块内动态槽为 `R{N}` 局部函数，登记到条目/分支 BlockScope。参数推送首次发生于 EnsureInitialized 之前；未初始化目标的 Invalidate 为 no-op，首次初始化读取当前输入。

### 4.3 绑定

- StyledProperty 目标生成 `UiBinding.TwoWay`，保存并按作用域登记；每轮调用 Refresh。
- 普通属性目标要求同类型单载荷 `{P}Changed` 回调；先安装回写回调，再推送当前源值，后续相等保护刷新。
- 同一对象不能同时有多份写入同一目标；`P` 与 `@bind-P`、文本与其目标属性冲突报 EUI033。
- 默认内容/具名槽的内容是初始化快照；其内不建立更新绑定作用域。

### 4.4 事件

路由事件使用目录声明的事件字段与 AddHandler；处理器形态为 Action 或 Action<RoutedEventArgs>。void 委托属性按其真实委托类型构造包装。二者处理后都使声明该处理器的对象失效；不会按 ui/C# 来源改变行为。事件与回调只安装一次，Render 不重复订阅。

### 4.5 内容

- 集合内容读取 ContentTarget，不硬编码 Children；元素按源码序加入目标集合。
- 单内容目标赋给声明成员，可为空或接收一个动态块；多个直接子项报内容模型错误。
- 默认内容参数按单项或多项形状赋值；具名 Slot 填充按源码序聚合，再写目标属性。
- ContentSlot 宿主内联把已传入槽内容装入当前内容集合，不额外生成节点。
- 参数/槽输入先于目标初始化。Ref 指向实际对象，Class、Style 和附加属性直接作用于该对象。

### 4.6 块

ConditionalBlock、ForEachBlock<T>、SwitchBlock<T> 是唯一结构协调器。生成代码只配置条件/集合/分支工厂，然后显式 Refresh；setter 不隐式构造内容。

每个分支或条目工厂创建自己的 UiConstructionScope，先登记新建对象，再执行赋值与初始化，成功后把根登记进 BlockScope 并 Complete。未成功的构造批次清理临时对象；它不是第二棵运行时所有权树。BlockScope 负责渲染动作和资源，实际根随块清理。

生成构建体用 try/catch 调用 scope.Abort(original)，清理成功后原样重抛；清理也失败时聚合构建根因与清理异常。foreach 刷新只在全部新条目构建成功后替换条目表；失败清理本轮新增条目并保留旧树。

### 4.7 服务注入（`@inject`，K37）

`@inject TypeName Name` 在生成类上发两个成员（语法见 spec/01 §4.18）：

```csharp
public TypeName Name { get; private set; } = null!;

protected override void InitializeUi()
{
    Name = (TypeName)(global::BubuUI.Application.Current?.Services
        .GetService(typeof(TypeName)) ?? throw new global::System.InvalidOperationException(
            "@inject 'Name' (TypeName) is not registered in Application.Current.Services …"));
    // ……构造作用域、base.InitializeUi()、根输入与结构内容（§4.1）
}
```

- 解析语句是 `InitializeUi` 的**最前语句**：先于 `UiConstructionScope`、`base.InitializeUi()`、
  根输入和全部动态槽（R11）。`ReloadUi`/热重载重建时重新解析，实例以容器注册为准。
- 属性 `get; private set;`：`@code`/code-behind 可读、类内可写；外部只读。
- 类型名按模板 `@using`/别名在生成文件头解析（与 `@code` 同一可见性）；类型不可解析由生成代码的
  C# 编译报权威错误，`#line` 回映 `@inject` 行（§2.4 同口径）。
- 属性声明行带 span 型 `#line` 指向类型名；属性名参与成员冲突校验（EUI045/EUI065），
  探针符号视图（目录 stub）含同一属性，模板表达式可直接引用。
- `@template` 控件模板的 `__TemplateView` 同样支持 `@inject`（视图在 `Build` 内 `EnsureInitialized`，解析时机不变）。
- 纯 C# 侧无对应语法：直接声明属性并从 `Application.Current.Services` 解析（或构造注入），
  与 `@inject` 生成形态等价——不引入第二个运行时机制。

## 5. 作用域与资源

| 对象 | 归属 |
|---|---|
| 根层绑定和块资源 | 当前 UiObject.Track |
| 块内绑定和嵌套块 | 当前 BlockScope.AddDisposable |
| 普通子控件 | 实际内容树，不另行登记长期子组件表 |
| 初始化中尚未提交的新对象 | 当前短命 UiConstructionScope |
| 样式注册 | 幂等静态注册入口；不保存实例状态 |

Render 只调用动态槽和 Refresh，不直接构造静态节点、安装绑定或增加事件处理器。块内部重建由块工厂执行。

## 6. 样式、覆盖与继承

StyledProperty 有效值优先级：local > template > class > template rule > control theme > skin > inherited > host theme default > property default。普通根属性使用 BeginTemplateValues，控件皮肤内部使用 BeginSkinValues；两者均保留 setter 校验，使调用方赋值和应用样式稳定覆盖皮肤默认值。控件主题和 `/template/` 规则的完整边界见 [05](./05-control-themes.md)。

模板内部 Class 使用 ApplyTemplateClasses，使用点使用 ApplyClasses；类集内部在前、外部在后。Style 内联转换为相同值层的静态 setter。附加属性仍调用声明宿主的 setter，不做根转发。

## 7. 命名与输出确定性

- 字段采用 `__el{N}`、`__blk{N}`、`__run{N}`、`__bind{N}`；动态函数为 `U{N}`/`R{N}`。
- `__eui` 前缀保留给初始化/样式/构建作用域辅助代码。
- 用户 foreach 迭代变量同样不得使用该前缀，报 EUI045；不会放行后再产生 CS0136。
- 根占稳定编号但不分配根变量，子项编号按源码结构确定。
- 输出按模板顺序组织 using、代码块、字段、初始化、Render、动态函数；集合遍历和目录排序必须确定。
- 用户不得声明 InitializeUi、Render、生成字段或隐藏生成代码依赖成员；冲突报 EUI045。
- 用户可以编写普通 Build 方法；Build 不是生成器入口。

## 8. 源映射与 IDE

用户语句使用 span 型 `#line` 回到 `.bui` 行列；`@code` 原文透传并保存精确映射，生成辅助语句 hidden。代码块内部 C# 错误不在生成器重造语义诊断。IDE 的诊断、补全和跳转使用同一目录和生成文档；不可嵌入类型只在子内容补全中过滤，根位置仍可选择。

IDE 补全需要交付完整编辑：主替换与可回映的附加编辑一起返回；未导入类型或标签的命名空间按语言规则生成去重的 `@using`，不把 C# using 写入 @code。`@using` 候选来自项目符号中的命名空间与类型，不限于 UI 目录。`@inject`（K37）按参数位分流：类型名参数的候选同样来自项目符号（非泛型可访问类型与命名空间，不限 UI 目录，短名按可见性过滤），属性名参数是自由标识符、无候选；指令关键字与 `@inject` 行内两处均纳入补全与悬停。override 成员声明补全使用真实基类的可覆盖成员，生成器占用的 InitializeUi/Render 不作为作者 override 候选。所有编辑必须有明确源码区间或明确的指令插入规则，不允许近似套用生成代码偏移。

## 9. AOT 与边界

运行期不扫描程序集、不反射、不按字符串分派标签和成员。类型、转换器、附加属性和事件在编译期解析，输出直接构造和调用。NativeAOT 的实际可发布性仍取决于平台/渲染原生依赖，不由生成器单独保证。

## 10. 契约测试

必须覆盖真实生成结果的 C# 编译，而不只比较文本：根=this、同项目模板继承、C# 根、别名根、引用程序集根、继承表达式、sealed/循环根、单/多内容、根参数和槽就位顺序、双向绑定、事件签名、字段提升、资源失败清理、服务注入（`@inject` 属性与解析语句序）、映射和增量确定性。

## 11. 变更记录与历史

- 2026-11-13 K35：根标签决定基类，UiObject 统一机制；Element 和 Window 分别表达可嵌入控件与窗口能力。
- 2026-11-13 K36：彻底删除旧组件类型、来源分类与兼容分支；测试和宿主全部使用新机制。
- 2026-11-26 K37：`@inject` 服务注入——生成注入属性，`InitializeUi` 最前从 `Application.Current.Services` 解析。

原始 K1–K35 和 D 台账完整保存在 [历史快照](../design/history/2026-11-13-before-legacy-removal/README.md)。历史条款与本规范冲突时不再生效。

## 12. 实现状态

根类型统一与旧层删除的验收、命令和已知像素基线问题记录于 [design/21](../design/21-root-type-unification.md)。性能与像素问题不得通过跳过测试或修改精确基线掩盖。

## 13. 变更规则

语法/行为变更先修订相关规范，再修改目录、分析器、生成器及运行时；新增诊断不得复用停用编号。每个新类型能力必须同时覆盖 ui 与纯代码路径。

## 14. 当前决策

| 决策 | 当前约束 |
|---|---|
| K35 | 根标签作为基类；实例即根；窗口独立于控件；内容类型决定可嵌入性 |
| K36 | 项目只保留上述机制；无旧组件兼容层、来源枚举、窗口专用模板分支 |
| K37 | `@inject TypeName Name` 服务注入：生成注入属性 + `InitializeUi` 最前从 `Application.Current.Services` 解析（R11）；重名 EUI065，成员冲突 EUI045；纯 C# 等价路径为手动解析 |
| K21–K34 延续项 | @code/using/alias/namespace、普通状态、显式失效、块协调、Class/Style 与源映射按现行规范继续执行；旧类型实现细节已由 K35/K36 取代 |
