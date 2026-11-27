# 21 · UI 文件与纯代码控件统一实施计划

> 决策日期：2026-11-13。状态：统一实现和旧层删除已落地，最终验证见 §7。决策：K35/K36。
> 本文替代 `proposals/unified-node-architecture.md`，修订 design/20 的窗口包装方案。
> 目标：`.bui` 与 C# 是同一种 UI 类型的两种编写方式，根标签决定生成类基类。

## 1. 类型与生成契约

```text
UiObject                 公共初始化、更新调度、作用域资源
├─ Element               可嵌入、参与布局/绘制/输入的控件
│  ├─ Panel / StackPanel / Grid
│  └─ Button / TextBlock / ScrollViewer …
└─ Window                窗口对象，Content: Element?
```

1. `.bui` 文件生成普通 partial 类，直接继承根标签解析得到的 CLR 类型。
2. 根标签描述当前实例：根属性写 `this`，内容写当前实例的内容成员，不额外 new 根对象。
3. `<StackPanel>` 根生成 `Counter : StackPanel`；`<Window>` 根生成 `MainWindow : Window`。
4. 同样的类可以完全用 C# 编写，标签目录、属性、事件、内容和初始化规则不区分来源。
5. `Window` 不继承 `Element`。窗口不得赋给 Element 类型的 Children、Content 或插槽；编译器按目标内容成员类型报错，C# 由类型系统检查。
6. 原生句柄、表面与显示仍由平台宿主负责。创建 UI 对象本身不创建原生窗口。
7. 根类型及元数据进入同一目录。新生成路径没有 WindowComponent、保留 Window 标签分支或窗口禁用标签清单。
8. 本次不引入 sealed UiNode、facet、通用生命周期事务回滚或“零成本”承诺。

## 2. 初始化与参数顺序

`UiObject.EnsureInitialized()` 是幂等入口；生成代码覆盖 `InitializeUi()`。根的普通 CLR 输入参数、默认内容参数和具名插槽先赋值，再调用 `base.InitializeUi()` 让基类消费输入，最后设置自身的模板默认样式属性和新增结构内容。控件首次进入父集合、测量或连接宿主前自动确保初始化。

顺序：`new → C# 对象初始化器 / 标签属性与内容输入 → EnsureInitialized → 更新与布局`。不在基类构造器中调用派生初始化，保证输入参数与字段初值可用。静态树只初始化一次；动态表达式和块刷新在 `Render()` 重算，首次动态值在初始化中求值。

自定义根类型可以继续派生；更新先调用基类 Render。sealed、不可见或无法合法构造的根给出诊断。code-behind 的显式基类须与根继承契约一致。初始化失败清理已登记资源和已挂接子树，保留异常并禁止重新构建半成品。

## 3. 内容和属性目录

沿用符号派生元数据，补齐单内容成员：Panel.Children 为 Element 集合；Window.Content 为 Element 单项；文本沿用 Text/TextRuns。默认内容参数和具名插槽保留 build-once 规则，目标类型同样约束可嵌入性。

公开可读写普通 CLR 属性可直接在模板中赋值，无需 UiParameter 标记；该标记保留 Required 等声明用途。void 委托沿用事件包装；Func 等有返回值委托作为普通值属性。绑定依据 StyledProperty 或匹配的 `{P}Changed` 能力判定，不能因为属性来自 ui 或 C# 而改变准入规则。

EUI060 泛化为非 Element 对象无法赋给元素内容的错误；EUI064 表示根不可继承或根继承环。新路径不发出旧窗口专用 EUI061/062/063。单内容成员可为空或接收一个动态块，多个直接内容项报普通内容模型错误。

模板清单记录根类型及 imports/aliases，解析同项目 template-only 类型的继承表面。自定义根、别名和引用程序集走同一查找机制；自身根和继承环拒绝。

样式、附加属性、Ref 直接作用于生成实例。类集合并保留“内部在前、外部在后”的使用点覆盖语义；初始化不得覆盖已设置的使用点属性。

StyledProperty 的有效值顺序为 local > template > class > inherited > default。根声明通过无分配的 BeginTemplateValues scope 写模板层，保留原 CLR setter 归一化；调用方写 local 层。后台 Invalidate 只读取已发布调度句柄入队，树移动由 UI 线程迁移队列，避免后台访问父链。

## 4. 更新、宿主和资源

更新入口下沉至 UiObject；Element 子树和 Window 内容提供实际子项遍历。宿主排空 UI 上下文并处理更新，主路径不再同时维护 Content 与独立 RootComponent。

新生成代码不得创建 Component 影子实例、提取 Root、调用组件专用 Mount 或另建组件所有权树。块资源仍由 BlockScope 和 Track 管理；子控件随真实内容树清理。异步事件保留现有 UI SynchronizationContext 机制。

窗口标题和尺寸由 Window setter 通过平台接口推送；C# 与 ui 同规则，生成器不按属性名发特殊调用。

旧 Component/WindowComponent API、RootComponent 参数和来源分类已删除。手写测试、宿主和 IDE 都使用 UiObject；不再保留兼容层。构建失败时，UiConstructionScope 清理未提交对象；它只在初始化/块工厂存活，成功后清空引用，不建立第二套运行时父链。

## 5. 实施切片

| 阶段 | 范围 | 出口 |
|---|---|---|
| P0 ✅ | 本文、spec/01–03、导航；删除原提案 | K35 与契约可追溯 |
| P1 ✅ | UiObject、Element、Window、无头与平台宿主 | 同实例、初始化幂等、对象初始化器有效 |
| P2 ✅ | 根继承、内容基数、参数/事件合并、类型检查、探针 | 同项目/跨程序集一致；窗口内容类型错误 |
| P3 ✅ | 初始化 this；统一属性/事件/插槽；动态槽和块 | 新类型生成无 Component.Root/Mount/WindowComponent |
| P4 ✅ | Gallery、fixtures、窗口入口、IDE 和测试 | 直接 new 窗口；控件直接加入树 |
| P5 ✅ 验证完成 | 编译/无头/渲染/IDE 测试、GalleryCheck、规范回写 | 初次迁移 1163/1164；清理后 1180/1181；唯一既有 golden 失败已用重构前源码复现 |

P1、P2 可并行；P3 依赖两者 API，P4 随 P3 集成。禁止将规划标成已完成。

## 6. 验收矩阵

| 编号 | 验收 |
|---|---|
| U01 | StackPanel 根生成类型可赋给 StackPanel/Element，实例本身加入 Children |
| U02 | Window 根生成类型可赋给 Window，不可赋给 Element；无 RootComponent 同步 |
| U03 | MainWindow/Window 被写进 Element 内容时报类型错误，C# 同样拒绝 |
| U04 | ui 与 C# 控件互嵌，Ref 指向真正节点 |
| U05 | 输入先于首次初始化；重复 EnsureInitialized 不增加子项 |
| U06 | 根属性/样式/附加属性直接生效，使用点覆盖不被内部初始化覆盖 |
| U07 | 动态属性、事件失效、绑定、异步续体和父子更新有效 |
| U08 | if/foreach/switch、Key 复用、默认内容及具名插槽保持行为 |
| U09 | 自定义根、别名、template-only 继承、引用程序集、sealed 与循环根有测试 |
| U10 | 卸载清理资源；超过 100 个独立脏控件不误判更新循环 |
| U11 | 无头/窗口共享更新路径；窗口属性经统一 setter 推送 |
| U12 | 渲染/IDE 回归与 GalleryCheck 验证布局、绘制和交互 |
| U13 | 运行时旧类型和属性不存在；编译器无来源分类或窗口模板专用分支 |
| U14 | 无显式内容的派生模板保留外部输入；根与槽内容在基类消费前就位 |
| U15 | 构建与清理同时失败保留根因；foreach 部分构建失败不泄漏；块释放异常不跳过其他清理 |
| U16 | IDE 无项目模式复用根类型符号视图；生成辅助名称与用户 foreach 变量不冲突 |

## 7. 实施记录

- 2026-11-13：冻结 K35；开始 P0–P4。验证结果与兼容层状态在 P5 后回写。
- P0–P5 已实现和验证。最终 `dotnet build BubuUI.slnx --no-restore` 为 0 警告 / 0 错误；串行全量测试：编译器 421/421、无头 432/432、IDE 203/203、渲染 107/108，合计 1163/1164。独立脏节点数量与真正循环轮次已分开计数；线程测试强制专用工作线程，消除了等待任务同线程内联造成的偶发误红。
- GalleryCheck CPU 路径完成勾选/计数交互并生成 1000×760 截图（733 commands，hash `FF0295A266B93621`），已人工查看截图。
- 唯一失败是既有 `solid-primitives` 精确像素 golden：期望 `FA65021E5F9A5DAD`，实际 `84BB45A4DF46D827`。已在独立临时目录导出重构前 git HEAD 的相关源码重新构建，复现完全相同失败；前后实际 PNG 的 SHA-256 均为 `9029EE788269E67EE30FF20EF4CF476B9D567000552EC9D6A7616E29B5B58305`。未更新基线或放宽容差。该场景直接绘制 DisplayList，不经过模板/控件树；具体底层差异来源未确定。
- 完整命令与证据见 [实施验证报告](./reports/root-type-unification-2026-11-13.md)。
- K36 清理后最终验证：构建 0 警告 / 0 错误；Compiler 421/421、Headless 447/447、IDE 205/205、Rendering 107/108，合计 1180/1181。结果位于 `TestResults/unification-cleanup/final-v3/*.trx`。唯一失败仍为上述既有 golden，未修改基线。
- 新增回归覆盖内容输入保留、构建/清理双失败、列表部分构建失败、块终态、生成名称冲突与 IDE 单一符号视图。目录性能测试单独运行，原 40ms/100ms 阈值保持不变。
- 最终 GalleryCheck CPU 交互通过，截图 hash 仍为 `FF0295A266B93621`。本地语言服务文件已发布并核对 DLL 哈希；用户中止了最后一次界面重载操作，编辑器需重载窗口以重新连接。

## 8. 旧层删除（K36）

用户要求彻底清理后，已删除 Component、WindowComponent、Window.RootComponent、UiTemplateRootAttribute 和相关宿主重载；HeadlessRoot 独立为文件，统一接收 UiObject。WindowHost 只有一条更新路径。

目录删除 UiElementOrigin/Origin、IsUnified、WindowRoot/IsWindowRoot 和语法近似参数表，扫描器/声明统一为 UiTemplateScanner/UiTemplateDeclaration。生成器删除独立组件构造/根转发及窗口专用分支，停用 EUI057/061–063。

手写测试改为真实内容树，补旧API缺失反向断言；当前 spec/01–04、README、运行时与窗口设计已写为新契约。旧文档快照位于 design/history，仅供追溯，不是兼容实现。

最终清理回归与本地语言服务更新结果在 §7 和验证报告记录；初次迁移的 1163/1164 数字仅代表删除旧层前的阶段结果。
