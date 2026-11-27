# 04 · UI 类型与元数据契约

> 状态：2026-11-13，统一类型模型。本文取代 design/14 的当前规范地位。

## 1. 类型与能力

UiObject 统一初始化、更新和资源管理。Element 派生类型可占据元素内容位置，参与布局、绘制、命中及路由事件；Window 派生 UiObject，Content 为 Element 单项，不属于 Element。

`.bui` 生成类直接继承根类型，C# 类直接声明同样的继承关系。目录不记录控件/组件来源类别，也不根据文件来源选择参数、事件或构造分支。

根可继承性、子内容可赋值性、内容基数和成员写入能力是独立元数据。sealed 类型仍可作为子项构造，但不能作为 ui 生成基类。隐藏/不可见/开放泛型/不能合法构造的类型有明确诊断。

## 2. 标签发现与名字

目录来自 Roslyn 符号；候选包含运行时与引用它的应用/库类型。同项目 ui 的 partial 类、@code 成员和根继承通过合成类型符号视图参与同一次扫描，引用程序集使用真实已编译符号。

标签名就是 CLR 类型短名，不设类型标签别名特性。命名空间查找遵循当前 @namespace 及外层链优先、随后 @using；@using Alias = Ns.Type 优先解析到指定类型。BubuUI 命名空间作为内置类型的统一隐式导入，不针对 Window 名称保留特殊解析。

同名候选报 EUI016 并建议 using alias；未知 EUI001；不可用类型保留原因，以便定向报错而非伪装成拼写错误。

## 3. 可写属性

公开实例 getter + public 非 init setter 的属性可用于模板。排除静态、索引器、UiIgnore、只读和不能合法参与生成的类型。普通属性自动暴露。

StyledProperty<T> 字段给属性额外提供值层、继承、变更通知与几何失效能力；普通 CLR 属性依靠公开 setter 和相等保护推送。两者在目录里按成员能力分别记录，不能按 ui/C# 来源分流。

UiParameter 特性及 Required 必填约束已移除；未传入的属性保留控件默认值。回调与内容角色必须在 override 继承中保持，派生 getter/setter 不得因缺少重复标记丢掉角色。

名字不存在报 EUI002 并给拼写建议；名字存在但只读等原因不可写时说明原因。缺少 StyledProperty 不能作为普通赋值失败的理由。

## 4. 事件与绑定能力

路由事件：静态 RoutedEvent 字段与已声明事件名进入目录，模板使用 OnEvent；生成 AddHandler 直接引用字段。字段不合规在声明处或引用使用点报告。

公开 void 委托属性按真实签名进入回调事件通道；有返回值的委托是普通值属性。事件和属性命名冲突有确定优先级并报告元数据声明问题。

StyledProperty 可写属性支持 UiBinding；普通属性仅在存在可写 PChanged 委托（返回 void、一个与 P 同类型参数）时支持双向回写。普通赋值与可绑定性不能混为一谈。参数 payload 形状按符号身份比较，忽略可空标注；不使用隐式转换猜测兼容。

## 5. 内容成员

| 声明 | 语义 |
|---|---|
| 无内容声明 | 不接受子内容 |
| UiContent(Children, Target=...) | 指定 Element 单项或 Element 集合成员；省略 Target 默认为 Children |
| UiContent(Text, Target=...) | 文本合成字符串写目标 |
| UiContent(TextRuns) + ITextContentHost | 静态/动态文本段由 AddRun 构造 |
| 属性 UiContent | 默认内容输入，可为 Element? 或 IReadOnlyList<Element> |
| UiSlot(name) 属性 | 具名内容输入，支持相同单项/多项形状 |

内容目标应为可读取可加入的集合，或可写 Element 单项。生成器使用真实 Target 名称，不能统一硬编码 Children。单项允许空或一个动态块，多项按源码顺序加入。

内容类型为 Element，因此任何非 Element UiObject 都不能放入该位置：EUI060。窗口使用这条通用规则，无窗口禁用标签表。自定义非窗口宿主同样生效。

默认内容参数和插槽在目标 EnsureInitialized 前就位；根继承时在 base.InitializeUi 前就位。插槽宿主 ContentSlot 内联使用已传入值，槽内填充不得再次声明宿主节点。

## 6. 附加属性

静态宿主声明 AttachedProperty<T> 及合法 public getter/setter；模板 Owner.Name 编译为直接 setter 调用。存储在目标 Element 值系统内，root scope 写模板层，使用点写 local 层，不存在模板根转发。

## 7. 字面量与样式目录

内置 primitive/enum/几何值按固定转换规则处理。第三方值通过 UiLiteralParser 声明 static Parse(string)、可选 Pattern/Candidates；编译时不执行 Parse，运行时发直接调用。无转换器 EUI023，格式错误 EUI003。

StyleBuilder 内建属性与 UiStyleProperty 标注字段合并成样式目录。扫描验证 public static readonly StyledProperty<T>、类型形状、规范名及冲突；声明/冲突 EUI058/EUI059。Class 与 Style 编译器、IDE 共用该目录。FocusVisualMode 通过 Element 上的 UiStyleProperty 枚举通道进入目录；FocusRingWidth / FocusRingOffset / FocusRingColor 通过 StyleBuilder 通道发布。focus / focus-visible / focus-within 为平台伪类；对应元素状态只读，不提供可赋值或可绑定入口。

## 8. 增量与诊断

目录为纯数据不可变记录，所有新增字段参与值相等和 hash；源码声明错误携带纯数据位置，引用程序集问题落使用点。不能保存 Symbol、Location 或 SyntaxTree 破坏缓存与可比较性。

EUI061–063 为停用的旧窗口专用码，不复用、不再发出；根不可继承/根继承环为 EUI064，普通内容成员错误为 EUI021，绑定能力不足为 EUI040。

## 9. 验收

目录快照验证公开模板表面；真实生成编译验证继承属性、默认内容、具名槽、plain CLR 值、void/非void委托、样式、附加属性与引用库。验证同项目 ui 无 code-behind 时功能完整，不能建立独立的语法近似参数表。
