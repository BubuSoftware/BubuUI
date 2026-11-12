# EclipseUI

模板引擎优先的自绘 UI 框架。设计文档见 [design/](./design/README.md)，执行计划见 [开发计划](./design/09-development-plan.md) 与 [G1 渲染 Gate 执行清单](./design/10-g1-render-gate.md)。
`.ui` 语法与生成代码契约的规范正本见 [spec/](./spec/README.md)（[语法](./spec/01-template-syntax.md)、[生成代码](./spec/02-generated-code-contract.md)）。

> 状态：M1（托管部分 + HarfBuzz shaping spike）+ M2 编译器 MVP + M3 无头垂直切片 + M4 输入与焦点（无头形态）已落地；
> **G1 渲染 Gate 已出结论：Go（有条件）**——Vello 后端在 Windows GPU 上 200 元素 1280×800 ≈ 6.4 ms/帧，
> 功能矩阵 8/9，CPU/GPU 像素一致。报告见 [render-gate-2026-11](./design/reports/render-gate-2026-11.md)。
> **G2 第一切片已通过**：Win32 窗口 + wgpu(Dx12) surface 上屏，resize 冒烟通过，见
> [11 G2 第一切片](./design/11-g2-surface-slice.md)。
> **G2 第二切片已通过**：元素树→display list、输入路由、文本 shaping 接入布局与绘制；
> CounterApp/TaskList 在真实窗口可交互，1000×760/25 元素稳态帧成本 ≈ 4.4 ms（p95 5.8 ms），见
> [12 G2 第二切片](./design/12-g2-interactive-slice.md)。
> **G2 第三切片已通过**：元素级 damage（`RenderVersion` + 快照 diff）与空闲跳帧——
> 空闲 400 tick 仅首帧上屏，交互帧按脏区提交；跟踪开销 ≈ 0.1 µs/元素，见
> [13 G2 第三切片](./design/13-g2-damage-v0.md)。
> **S1 失效重算切片已通过**：普通 CLR 状态 + `Invalidate`/`Render`，根级属性/文本重算、
> 控件事件自动失效；规范正本 [spec/01–03](./spec/README.md)（v4）。
> **S2 块 v4 切片已通过**：`@if`/`@foreach` 协调器 + `R{N}`、任意 `IEnumerable<T>` item 推断、
> Keyed 复用、`UiBinding` 原语与 `EUI033`。
> **S3 绑定切片已通过（控件侧）**：`@bind-P` 新语法、item 绑定与条目释放、`EUI037`/`EUI040`
> （旧 `:bind`/`:value` 已于 S6/K27 移除，现报 `EUI046`）。
> **C1 目录派生切片已通过**：运行时五个声明特性（`[UiContent]`/`[UiIgnore]`/`[UiParameter]`/
> `[UiSlot]` + `UiContentKind`；`[UiTag]` 已于 K32 退役，标签恒为类型短名）、`Compiler.Metadata` Roslyn 符号扫描器；派生目录 ⊇ 手写目录
> （模板属性项 6→17，差集逐条评审见 [14 §14.12.1](./design/14-control-template-contract.md)）。
> **C2 模板表面切片已通过（2026-11-02）**：分析器/生成器切到派生目录、手写目录删除（`CreateDefault()` 不存在）；
> `InputEvents` 7 个事件迁入 `Element`（模板事件 1 → 9 个事件名，任何标签可写输入/焦点处理器）、
> 补 `CheckBox.CheckedChanged`；`Metadata` 源码链接进生成器程序集；golden 快照落成
> `TestData/BuiltInSurface.golden.txt`；`@foreach` 生成代码全 `global::` 限定（D22 修复）。
> 落地记录 [14 §14.12.2](./design/14-control-template-contract.md)，台账 D22/D65/D69/D72 关闭。
> 257 个测试全绿（Headless 103 / Compiler 93 / Rendering 61）。
> **C3 转换器与使用点诊断切片已通过（2026-11-03）**：内置转换器补齐到 spec/01 §5.2 全表
> （float/decimal/long/short/byte/char/枚举/TimeSpan，编译期换算、含量程/转义校验）；
> 使用点诊断 `EUI023`（无字面量转换器，提示 `{expression}`）、`EUI025`（纯普值重复属性；
> 同名含绑定仍走 `EUI033`）、`EUI031`（`Class`/`Style`/附加属性/`<Slot>`/`<ContentSlot>` 保留语法）落地。
> 落地记录 [14 §14.12.3](./design/14-control-template-contract.md)，台账 D2/D3/D6/D14/D19 关闭。
> 281 个测试全绿（Headless 127 / Compiler 93 / Rendering 61）。
> **C4 组件标签切片已通过（2026-11-05）**：自定义组件可写入 `.ui`——`[UiParameter]` 参数表（含
> `Required`/委托参数即事件/`{P}Changed` 约定链接）、`@bind-P` 组件双向、默认内容（`Element?` 单根 /
> `IReadOnlyList<Element>` 多根）与冻结生命周期（登记 → 参数推送 → 内容 → `Mount()`）落地；
> 诊断 EUI021/027/028/040 覆盖组件声明与使用点。落地记录 [14 §14.12.4](./design/14-control-template-contract.md)，
> 台账 D17/D52/D54/D56 关闭（新增 D73 残余风险）。
> 320 个测试全绿（Headless 145 / Compiler 114 / Rendering 61）。
> **C5 Ref 与使用点诊断切片已通过（2026-11-05）**：`Ref={field}` 落地——元素/组件实例在 Build 时
> 赋给 code-behind 字段（不进 `Render()`、不重算），诊断分层：非简单标识符由分析器报 `EUI029`（语法级），
> 字段缺失/类型不符/只读/静态由生成器探针报 `EUI029`（语义级，引用 C# 诊断 CS0103/CS0029/CS0191）；
> `EUI019`/`EUI020`/`EUI021` **使用点**口径落地——引用程序集控件的元数据问题在使用点暴露（消息带完整类型名），
> 源项目控件仍落 `EUI002`/`EUI006`（声明处归 `EUI026`）。落地记录 [14 §14.12.5](./design/14-control-template-contract.md)，
> 台账新增 D74（① 使用点 ✅ / ② `EUI026` 声明处出口开放）。
> 347 个测试全绿（Headless 160 / Compiler 126 / Rendering 61）。
> **Tokenizer 切片已通过（2026-11-05）**：`.ui` 编译器内两处正则形态判定换 Roslyn 解析/深度感知扫描——
> 控件事件的委托类型（`Action` vs `Action<RoutedEventArgs>`）按表达式语法形态判定（括号/引号内的
> `=>`、嵌套 lambda、匿名方法不再误判），`@foreach` 头按 spec/01 §4.11 冻结形式扫描（集合内带引号的
> `" in "`、括号内 `in` 修饰符不再撕裂表达式；类型化条目/解构给出针对性 `EUI100` 消息）。
> 358 个测试全绿（Headless 167 / Compiler 130 / Rendering 61）。
> **C6 具名插槽切片已通过（2026-11-05）**：消费侧 `<Slot>` 与宿主侧 `<ContentSlot>`、
> EUI022 家族、两级插槽解析与基链同名成员胜出规则（D75/D76），落地记录
> [14 §14.12.6](./design/14-control-template-contract.md)；附加属性实拆留 G4，泛型标签暂缓实现（K19，不排期）。
> **G3 七个小切片已通过（2026-11-05 – 11-08）**：块布局透明（D77，条目按最近布局祖先的 `Gap` 落位）、
> `Adopt` 接管失效迁移（D78，修掉 GalleryApp 空 Tasks 卡）、按文件增量生成（单 `.ui` 变化只重跑该文件）、
> `#line` 列映射（D79，span 型指令 + `CS8939` 防护，类型错误可落回 `.ui` 行列）、
> `EUI045` code-behind 冲突诊断（D57/D80，诊断落在 C# 声明处，生成器不改名也不跳过输出）、
> 诊断出口补全（D74 ②/D81，元数据问题落在 C# 声明处；标签冲突/不可用分流 EUI016/017/018/030）、
> **EUI024 事件处理器签名匹配（D82，2026-11-08）**——借 Roslyn 把处理器表达式放进 code-behind
> 符号上下文，仅「表达式自身语义干净 + 对目标委托的转换确定失败」时报 Error 于 `.ui` 属性值并
> 短路生成输出，符号未解析/重载失败/`dynamic`/表达式内部错误静默交回生成代码经 `#line` 由 C# 报错；
> `EventHandlerSignatureTests` 16 例，G3 至此全部收敛。
> **567 个用例（Headless 274 / Compiler 228 / Rendering 65），566 绿**——唯一红是既有像素 golden
> `PixelGoldens_MatchBaseline`（`solid-primitives`/`transform-clip` 整图 hash 不符，探针 ±2 全过，与 S6 无关）。
>
> **S4/S5 运行时硬化与 G2 CPU 侧部分落地（2026-11-08，D83/D84）**：运行时 `IValueBox`/`ValueBox<T>`
> 让重复赋值只改泛型字段、消除值类型装箱，`Component.Dispose` 锁内清 pending 队列（`RuntimeHardeningTests` 7 例）；
> CPU 侧新增 `PixelGoldenTests` 4 例（纯图元、无字体依赖的像素 golden）+ `RetainedFrameCompositor.cs`
> （按 `FrameDamage` 只 blit 脏区的 retained 合成原型）+ `tools/bench-60fps.ps1` 与目标机报告模板。
> **边界**：合成原型不减少光栅化，真正局部重绘需 `IRenderBackend.Render` 脏区/scissor 重载
> （VelloSharp 0.5.0-alpha.3 无公开 API）；GPU retained layer 依赖 HWND + wgpu device，本机无法验证；
> 「稳定 60fps」结论依赖物理目标机——本机（60Hz 虚拟链）已完成帧节奏修复与复测（
> [报告](./design/reports/target-machine-60fps-2026-11-08.md)）：`Immediate` 模式墙钟 59.7–59.8fps 稳定，
> `Fifo` 上屏受虚拟链 DWM 合成波动（32–60Hz）主导；
> 像素探针 DSL 后随 D91 落地。余项：S4/S5（Element.Unmount 属 G4、AOT/裁剪、旧语法清理、最终审计、
> 帧预算/分配预算门槛、CI）、G2（GPU retained layer/真正局部重绘、目标机稳定 60fps 结论、
> CPU/GPU 交叉一致性门禁）、G4（样式/主题；附加属性首批 ✅ 2026-11-09；泛型标签暂缓 K19）、IDE/DevTools。

> **G2 像素探针 DSL 已落地（2026-11-09，D91）**：`PixelProbe` 流式断言门面——点 `At`/`AlphaAt`、
> 区域 `DominantColor`（覆盖率 + 直方图诊断）/`Contains`/`NotContains`（首命中坐标）/`CountIn`/`Count`、
> 帧间 `SameAs`/`SameAsOutside`（`RenderTarget` 与 byte[] 快照重载，逐字节精确）；失败聚合一次性报告。
> `PixelGoldenTests` 升级双层门禁：语义探针（期望值自描述于场景定义，区域/计数/禁色断言）先于基线比对，
> `ECLIPSEUI_UPDATE_GOLDEN=1` 写基线前先过语义探针；Damage/Retained 手写像素双循环收敛为 DSL。
> 既有像素 golden 红（`solid-primitives`/`transform-clip` 整图 hash 漂移）定位为 VelloSharp AA 舍入差 1
> （`4,32` 描边半覆盖像素 `#FFFFFF80`→`#FFFFFF7F`）并随基线重生成清零。
>
>
> **G2 CPU/GPU 交叉一致性门禁已通过（2026-11-09，D92）**：`CrossBackendGateTests` 场景矩阵 14 项
> （实色/圆角/椭圆/路径/三种渐变/裁剪栈/变换/图层透明度/混合模式/阴影/1:1 图像/虚线）×
> 四级容差策略（Tight ±2/0.05%、Edges ±2/0.5%、Gradient ±4/2%、Loose ±16/5%），超阈值即失败
> （聚合首违点双侧颜色）；无 GPU 自动跳过并写 TestResults 报告留痕。首跑校准：
> 14 场景全部 0 违例、最大通道差 ≤1。
>
> **G2 retained 显示列表已通过（2026-11-09，D93）**：`ElementPainter` 段级 retained——每个元素的
> 自绘命令（类型绘制 + 卡片 + 焦点环）按 (RenderVersion, 绝对位置, 尺寸, 焦点态) 缓存，树未变的
> 元素整段复用、帧间命令构造分配趋零；失效契约与 DamageTracker 一致，滚动平移由段键位置捕获；
> clip 作用域与滚动条每帧生成（thumb 位置不进 Version）。金标准 retained 渲染 == 全量渲染
> 逐像素（`RetainedPaintTests` 8 例，含 GPU 后端分支 retained 合成等价）。VelloSharp 无 scissor
> API，光栅化仍整帧、构建层 retained 先行。GalleryCheck 全链路 PASS。
> **60fps 复测（2026-11-09，机器已换 RTX 2070）**：GalleryCheck 帧成本 avg 3.00ms / p95 3.43ms
> （预算 ~18%）、`Immediate` 墙钟 60.0fps（软限速修复 + D93 保持）；`bench-60fps.ps1` 刷新率采集
> 改为逐适配器列出并优先物理适配器；`Fifo` 上屏仍受虚拟链合成波动与上游 `frame latency=2`
> 双重阻塞，稳定 60fps 结论待物理目标机——操作指引见报告 §9。
> **S5 预算门槛扩展（2026-11-09，D94）**：`RuntimeHardeningTests` +3——附加属性重复赋值不重分配
> 值盒、布局热路径分配常数级（Grid 轨道解析缓存 + cells/`double[]` 缓冲复用 + `ElementCollection`
> struct enumerator 消除 foreach 装箱）、retained 帧构建 ≤512B/帧（锁 D93 段复用收益）。
> **AOT/裁剪试点（2026-11-09，D95）**：Gallery.Desktop **full trimming 0 警告通过**、发布产物
> 实跑出图（源生成器架构无运行时反射，天然裁剪友好）；AOT ILC 通过但运行阻塞于上游
> `VelloSharp.NativeLibraryLoader`（`Assembly.GetCallingAssembly()` AOT 不支持，待上游修复）；
> 试点配置固化 csproj（`PublishTrimmed`/`TrimMode=full` + `EnableAot` 开关——注意 `-p:`
> 全局属性会传染源生成器报 NETSDK1124，必须放 csproj）。
> **657 个用例（Headless 332 / Compiler 241 / Rendering 84），全绿**。
>
> **S5 最终审计与文档同步已通过（2026-11-09，D96）**：全量构建/测试终验（657 例全绿、0 警告）。
> 审计修复两处——① `RuntimeHardeningTests` A1 去装箱测试的 `[Fact]` 被注释行吞掉、自 D83 起
> 从未运行（恢复后实测通过）；② spec/03 §2.6 死循环上限契约补测试（`Component.MaxRenderIterations`
> = 100 次 Render，超限 fail-fast + 上限内恰好收敛两例，实现本已存在）。README/design/09/spec/03
> 验收清单与数字全面同步——S5 出口核对：基准/预算 ✅ D94、AOT/裁剪 ✅ D95、死循环上限 ✅ D96、
> 弃用清理 ✅ S6/K27、最终审计 ✅ D96；正式性能吞吐基准表随 G8。
>
> **G2 retained 标签回归修复 + 画廊布局复位（2026-11-09，D97）**：按钮/勾选框文字消失定位为
> `PaintButton`/`PaintCheckBox` 的 `_text.Paint` 写主帧列表而非段缓冲（D93 重构漏改）——首帧
> 背景后画盖字、复用帧段缺文字；两条路径同错使 retained 金标准抓不到，补 2 例探针回归测试。
> GalleryCheck `add task` 失败经 bisect 排除渲染回归——G4 showcase 把 add 按钮/任务卡推出
> 760px 视口，交互演示复位到 showcase 之前，selfcheck PASS。
> **659 个用例（Headless 332 / Compiler 241 / Rendering 86），全绿**。
>
> **G4 样式系统首批已通过（2026-11-09，D98/D99）**：**S-A 主题令牌化**——`ElementTheme` 变可替换
> token 表（virtual + Dark/Light 预置），`ReplaceTheme` 换表丢段 + 整帧重绘，`WindowHost.SetTheme`
> 运行时切换，Gallery「Toggle theme」双宿主接线（亮暗一键切换）。**S-B Class 样式 + 有效值分层**——
> 属性系统落分层（local > class > 继承 > 默认，失效语义同 SetValue）；`Element.Classes` +
> `Styling.Provider` 注入，`StyleRegistry` 类名直配 setter 序列（无运行时选择器引擎，AOT 友好）；
> `Visual` 覆盖属性（Background/Foreground/CornerRadius）+ painter 读覆盖回落主题（未设置零漂移）；
> Gallery primary/tool 双 class 演示。模板 `Class` 语法与编译期类名校验、按主题资源查找留下一片；
> `Style=` 内联（S-C）与控件模板（S-D）远期。
>
> **Eustyle 样式表编译器已通过（2026-11-09，D104，样式系统 E1 片）**：`.eustyle` 全局样式表 +
> `@style` 组件块（`{Component}__` 前缀注册、静态幂等 `__RegisterStyles`）——SCSS 子集（类规则 +
> `&:hover`/`.card:hover` 双形态变体、`$变量`、`theme(d,l)`、hex 三形态）编译期编译为
> `StyleRegistry.Define` 调用，运行时零引擎；诊断 EUI050/051/052（EUI048/049 已占用顺延修正）。
> 画廊 GalleryStyles.cs → app.eustyle 全量迁移。
>
> **模板 `Class`/`Style` 转正已通过（2026-11-09，D105，样式系统 E2 片）**：类名目录
> （`.eustyle` + `@style` 块）combine 进模板管道——`Class="a b"` 编译期查目录（本文件 `@style`
> 优先解析），未知类名 **EUI031** 转正报错（原「保留未实现」终止）；`Class={expr}` 支持 string
> （Split）与 `IEnumerable<string>`（ToArray）经 U/R 定向推送；`Style="prop: value; ..."` 只允许
> 静态声明序列（Eustyle 解析，EUI050–052 定位值 span），生成 local 层直赋（`Visual.SetXxx`/
> `SetValue`，theme() 升级 Resource 属性）。Showcase.ui 演示本文件 `@style` 闭环 +
> local>class 层级覆盖。**739 个用例（Headless 352 / Compiler 281 / Rendering 106），全绿**。
>
> **嵌套塌缩 + `.ui` 注入已通过（2026-11-09，D106，样式系统 E4 片）**：`.panel { > Button { … } }`
> 编译期塌缩为 BEM 派生类（`panel__button`，类型名 kebab 化）——同名段合并（CSS 同选择器
> 多块语义）、派生名撞显式类名报 EUI053；`.ui` 编译器把派生类注入挂根类元素的静态子树
> （直接子穿透 @if/@foreach/@switch，插槽不穿透；派生类排原类之后 = 派生类胜），运行时
> 零选择器引擎；`Class={expr}` 表达式形态跳过注入（明示边界，手工 BEM 名兜底且可校验）。
> 画廊 app.eustyle `.card` 塌缩链路 + Showcase 分区卡（含 @foreach 行注入）演示。
> **752 个用例（Headless 352 / Compiler 294 / Rendering 106），全绿**。
>
> **注册制伪类已通过（2026-11-10，D107，样式系统 E5 收尾片——五片全部落地）**：
> `PseudoStatesRegistry.Register("loading")` 启动期注册自定义状态位（幂等、平台保留名拒绝、
> 位从 0x10 起）——掩码即凭证：`Element.SetRegisteredState(mask, bool)` 只接受已注册位，
> 注册位 OR 进 `PseudoStates` 合成掩码（变体匹配/段键零特殊处理）；`ClassStyleVariants.State(mask, …)`
> 变体链与平台位共用声明序语义。样式表侧 `@states { loading; }` 顶层声明（预扫两遍、声明序
> 无关、撞平台名 EUI050）+ 三处状态解析统一「平台位 → 本文件声明 → 全局目录」，产物是
> 幂等 Register 行 + `.State(Register("name"), …)` 变体链；**EUI054** 双通道冲突（样式表类 ×
> 手写 `Define`）Warning 不短路。painter 补齐 TextBlock class 背景绘制（状态芯片形态）。
> 画廊 `@states { spinning; }` + spinner 双芯片演示（idle 灰 / 翻位绿）。**769 个用例
> （Headless 359 / Compiler 304 / Rendering 106），全绿**。
>
> **样式属性单一来源 + CLR 原名规范名（2026-11-10，design/17 §4.3）**：删掉编译器侧手写的
> 6 项属性字典、诊断名数组与 `EmitLocalSetters` switch——`UiCatalog.StyleProperties` 由 Roslyn
> 从 `StyleBuilder` 公开 setter + `StyledProperty` 静态字段派生，Parser 校验、EUI051 支持列表、
> `Style=` 目标字段与 theme() 准入共用一份；声明漂移以 `EUI026` 报出。属性名一律用控件原始
> CLR 名（`CornerRadius`/`FontSize`），**大小写敏感、精确匹配**：无别名、无归一化，
> `corner-radius` 这类旧写法不再兼容，直接报 `EUI051`；`Style=` 统一发
> `el.SetValue(<目录字段>, <值>)`。
> `StylePropertyCatalogTests` 用反射把目录与运行时 setter 集对死防漂移；画廊 `app.eustyle` /
> `Showcase.ui` 已改用规范名。全量 784 例（359/319/106），除本机
> `PixelGoldens_MatchBaseline` 的像素哈希差异（改动前既有的环境差异）外 0 回归。
>
> **样式属性自定义化已通过（2026-11-12，D115，design/19 P1 / E6 片）**：属性目录进料口扩为
> 「`StyleBuilder` setter ∪ `[UiStyleProperty]` 标注字段」——应用/第三方库给自己的
> `public static readonly StyledProperty<T>` 字段加标注即可写进 `.eustyle` / `@style` /
> `Style=`（属性名 `Name` ?? 去 `Property` 后缀；`double`/`Color`/枚举/`string` 四种值形态，
> 枚举成员表扫描期派生）；声明不合规 **EUI058**、同名冲突 **EUI059**（用户 vs 内建：内建胜；
> 用户 vs 用户：双双拒绝）。自定义属性发射走 `s.Set(<目录字段>, <值>)`，内建仍 `s.Name(...)`；
> LSP 在 `Prop: |` 值位置给枚举成员候选、值悬停给形态文案。画廊 `BadgeStyles`（Glow/Tone/
> BadgeLabel）端到端演示：画廊**第 5 分区**把类通道收到的值读回成可见文案（hover 翻 Glow 6→12
> 经 `PropertyChanged` 即时刷新），Showcase 第 3 分区以内联 `Style=` + `{InlineDesc}` 读回
> local 层三值，`app.eustyle` 注释覆盖全部 P1 写法（keyword 直通 / `$var` 限 number/color /
> theme 拒绝 / 显式 `Name`）。新用例 17 例；全量 **1095 例全绿（392/394/201/108）** 0 警告。
>
> **布局契约已通过（2026-11-13，D116）**：`Element` 级统一布局面（Avalonia `Layoutable` 对齐）——
> `Margin` / `Width` / `Height` / `Min*/Max*`（宽高 NaN 缺省）+ `HorizontalAlignment/VerticalAlignment`
> （默认 **Stretch**，既有布局零回归）+ `TextBlock.TextWrapping`（Wrap 贪心断行：空格换行、
> 行首行尾空白折叠、超宽长词字符硬切）+ `ScrollViewer` 方向开关（Horizontal 默认 **Disabled**
> 溢出裁剪不出条、Vertical Auto；Auto 按需出条——测量约束保持视口宽，可收缩内容继续自适应，
> 与 Avalonia 的差异登记在 design/05）。契约 10 属性进模板表面（快照 122 项）；画廊窄窗口下
> 文案随视口换行、不再横向溢出（宽窗渲染 hash 与改动前一致）。新用例 15 例；全量
> **1110 例全绿（392/409/201/108）** 0 警告。
>
> **G5 IDE 第一片已通过（2026-11-10）**：`ide/` 落地 design/06 的「一份共享资产、两个薄壳」——
> **LSP server**（`EclipseUI.LanguageServer`，net10.0 stdio，手写 JSON-RPC 帧）诊断直接跑编译器前端
> （EUI 码与 `dotnet build` 同码同 span），补全覆盖标签/属性/事件/组件参数/附加属性/`@bind-`/
> 枚举值与 `Style=` 属性名/`@` 指令/`@using` 命名空间，另有悬停与大纲；目录仍由
> `UiCatalogFactory` 符号派生（runtime 同目录下引用 `EclipseUI.Runtime` 的程序集自动纳入，
> 用户组件因此可直接补全）。**VS Code 壳**含 `.ui`/`.eustyle` 两份 TextMate grammar、
> 语言配置与片段，客户端只有百行。用例 **64 例**（光标上下文/补全/悬停/大纲/诊断/协议帧/端到端会话）
> + grammar 的 oniguruma 逐 token 断言（`node test/tokenize.test.js`）。表达式内 C# 补全
> （Roslyn 投影）、跳转/重命名、语义着色、`#line` 调试留后续片。
>
> **G5 IDE 表达式投影已通过（2026-11-10，L2）**：design/06 §6.3 落地——生成器的探针类抽取为共享项目
> `src/EclipseUI.Compiler.Probe`（生成器源码包含 + LSP 直接引用，**单一实现**），LSP 侧
> `DocumentAnalysis.Probe` 懒建投影编译：基编译 + `{Name}.cs` 兄弟文件（spec/02 §2.2）+ `@code` 成员树，
> `@foreach` 变量经 `CursorScanner` 作用域栈 + `InferType`/`ItemTypeOf` 推断入环境。
> `{expr}` 内：点号补全列接收者成员（基类链遍历）、裸前缀列 foreach 变量 + 自身成员，
> 悬停显示推断类型（流调整可空性）与处理器签名；无运行时/无 code-behind 时优雅停用。
> 新增 **13 例**（探针原语/补全/悬停/兄弟文件/缓存失效），IDE 用例 64 → **77**，全绿；
> 生成器回归 319 例不变（探针抽取行为等价）。
>
> **G5 IDE 跳转定义已通过（2026-11-10，L3 第一片）**：探针新增符号反查 `Locate`——同一探针语句里
> 定位光标所在标识符（表达式内偏移映射到合成树，光标在 token 中间也取**完整表达式**，
> `CursorContext` 因此增加 `ExpressionEnd`）→ `GetSymbolInfo` 得符号。落点两类：
> `@foreach` 迭代变量落回**本文档**的 foreach 头（`CursorScanner` 给变量名的文档偏移 `NameOffset`）；
> code-behind 成员跳到兄弟 `.cs`（spec/02 §2.2）的**声明名字**（属性/方法/字段/类型/参数的
> 标识符区间，不是整个声明）。`@code` 成员的声明在合成树里、无可去文件——返回 null 不给错位置。
> server 声明 `definitionProvider`，VS Code 客户端零改动（标准 LSP 自动接通）。
> 新增 **7 例**（探针定位/token 中点/内存态边界/点号与未知名/服务级属性与方法/协议会话），
> IDE 用例 77 → **84**，全解决方案 868 例全绿、0 警告。
>
> **G5 IDE 重命名已通过（2026-11-10，L3 第二片）**：`prepareRename` + `textDocument/rename`
> 落地。范围口径 v1——**宁可拒绝也不给改不全的编辑**：`@foreach` 变量作用域就是本文档，
> 头声明 + 表达式内引用完备改名；code-behind 类自身成员扫 `.ui` 表达式 + `@code` 块引用 +
> 兄弟 `.cs` + 工作区其余 `.cs`（`--workspace`，VS Code/Rider 都传）。其他类型的成员
> （`task.Name` 的 Name、工具类静态）、`@code` 声明的成员、无工作区根的内存态——一律拒绝。
> 嵌套同名 foreach 的遮蔽语义按「绑定者 = 栈内最内层同名作用域」精确处理。
> 踩坑记录：跨编译符号比较不能用 `SymbolEqualityComparer.Default`——探针派生编译里
> merged partial（`@code` 树）的 identity key 与单 site 版不同，实测 `Gallery.ProbeDoc`
> 不等于 `Gallery.ProbeDoc`；改用全限定签名字符串比较（含 containing 类型与参数签名）。
> `CollectExpressions` 还暴露并修掉 `CursorScanner` 一个既有 bug：`@foreach` 指令分支的
> Prefix 在范围检查前构造，offset 被跳过区间（`@code` 块）越过时负长度 `Substring` 崩溃。
> 新增 **7 例**（变量改名/遮蔽/成员三文件联动/拒绝场景/内存态边界/prepare/协议会话），
> IDE 用例 84 → **91**，全解决方案 875 例全绿、0 警告。
>
> **G5 IDE §6.9 重构已通过（2026-11-11，P0–P8 全量）**：按 design/06 §6.9 把「dll 元数据目录 +
> 手写启发式」的 v1 换成**项目系统驱动的单一 Compilation**。L1 `MSBuildLocator` +
> `Microsoft.Build.Evaluation.Project` 求值（归属判据 = 求值后 `AdditionalFiles` 命中）；L2 每项目
> 一份真 `CSharpCompilation`（`Compile` 源码树、`Using` items、`obj/project.assets.json` 精确引用）；
> L3 用 `extern alias euiGen` 在 IDE 内跑真 `TemplateGenerator`（生成 5 棵树、编译错误 10 → 0、
> 目录 10 → 14 标签）；L4 `UiCatalogFactory.Build(outputCompilation)` 成为唯一事实源。L5 诊断双通道
> （Tier 1 每次按键 / Tier 2 真生成器 + `#line` 回映，400ms debounce）、L6 标签与属性/事件/参数/
> 附加属性一律走 Roslyn 符号查询（成员级反编译兜底）、L7 watcher 自动失效、L8 signature help /
> 语义着色 / 代码操作 / 格式化全部挂在同一份 Compilation 与同一套偏移回映上。`.eustyle` 进
> `documentSelector`，样式表诊断接入 LSP。**不接管 `.cs` 文件**，Rider 仍走同一 server。
> 落地实测修掉三处真问题：`ExcludeAssets=runtime` 下 MSBuildLocator 注册晚于 JIT 导致启动即
> `FileNotFoundException`（注册提前到构造函数 + `NoInlining`）；共用 `ProjectCollection` 重复加载
> 同一 csproj 抛「已有等效项目」，19 个工程只求值出 10 个（`GetOrLoad` 复用 `LoadedProjects`）；
> `Location.Create(path,…)` 不是 `IsInSource`，`.eustyle` 的 EUI050–052 被整批丢掉
> （统一用 `GetMappedLineSpan()`）。另补：框架类型的跨项目源码符号回退用轻量源码树编译，
> 发现阶段向上扩到仓库根（只扩大已知项目集合，不改归属判据）。
> `ide/build.ps1` 已重发布；对发布版做 stdio LSP 端到端冒烟：19 个项目全部求值、Gallery 得
> 14 标签、`StackPanel.Gap` 的 F12 实测落 `src/EclipseUI.Runtime/Panels.cs:46`。
> 五条验收夹具与协议级回归落在 `ProjectSystemTests.cs` / `ProtocolTests.cs`，
> **IDE 用例 105 → 119，全绿**；P9（`SymbolFinder` 改造）按原计划不做。
>
> **S-C 最小主题资源已通过（2026-11-09，D100）**：`ThemeColor` 暗亮双色——`Visual.BackgroundResource/
> ForegroundResource`（解析优先级 resource > 静态覆盖 > 主题令牌）+ `StyleBuilder` 双色重载；
> painter 在**段构建期**按 `ElementTheme.IsLight` 解析，换主题 `ReplaceTheme` 丢段重建即随新表刷新
> （无需新增失效维度）。画廊分区卡/标题/primary/tool 样式全改双色资源——**亮暗切换整页跟随**，
> inline 静态色保留以并示「静态覆盖 vs 主题跟随」。**670 个用例（332/241/97），全绿**。
>
> **:disabled 已通过（2026-11-09，D103，样式系统 E3 片）**：`PseudoStates.Disabled` 第 4 位
> （**有效值**——`IsEnabled` 沿祖先链继承合成进掩码，父禁用则子禁用，变体匹配/段键零特殊处理）；
> `InputRouter` 对禁用元素吞按下/滚轮/移动（不悬停不聚焦）、按住中途禁用时释放吞 Click；
> `FocusManager` 拒绝禁用聚焦、Tab 遍历跳过；painter `Resolve*` 置灰令牌回落
> （`DisabledForeground/DisabledBackground`，class 变体优先）；`StyleRegistry` 增 `.Disabled(...)`
> 变体链；`Visual.FocusRingWidth` class 覆盖（环宽进样式，回落令牌）；画廊样式区加 `:disabled`
> 演示按钮。执行计划见 design/17（E3 → E1 → E2 → E4 → E5）。`StyleTests` +5、`PseudoStateTests` +6；
> **699 个用例（352/241/106），全绿**；selfcheck PASS；深浅双主题出图核对通过（disabled 置灰正确）。
>
> **样式表编译器已通过（2026-11-09，D104，样式系统 E1 片）**：`.eustyle` 全局样式表与
> `@style` 组件块（design/17 §4.8）编译为生成代码——运行时零引擎。解析器支持类规则 +
> 伪类变体两种形态（`.card:hover` 与块内 `&:hover`，`:active` 是 `:pressed` 别名）、
> `$变量` 编译期替换、`theme(dark, light)` 双色、`#rgb/#rrggbb/#rrggbbaa`、注释；
> 诊断 EUI050/051/052（语法 / 未知属性 / 值类型不符）。`.eustyle` → 单 `StyleBootstrap.g.cs`
> （`Apply(StyleRegistry)` 供 `UseStyles` 直连）；`@style` 块 → 类名加组件前缀
> `{Component}__{class}` + 组件初始化时静态幂等注册（作用域由命名隔离实现）。画廊
> `GalleryStyles.cs` 已迁移为 `app.eustyle`，`Showcase.ui` 带 `@style` 块演示。
> `EustyleTests` +23（含**生成产物语法级校验**）。**722 个用例（352/264/106），全绿**；
> selfcheck PASS；深浅双主题出图核对通过（外观与迁移前一致）。
>
> **伪类状态样式已通过（2026-11-09，D102）**：CSS `:hover/:active/:focus` 对应物——`PseudoStates`
> 掩码由 InputRouter/FocusManager 维护（hover 跟随命中、捕获冻结、pressed 先路由后清保 Click 门控），
> 状态变化 = 渲染失效 + class 层重应用；定义侧链式语法
> `Define("primary", s => s.Background(...)).Hover(s => ...).Pressed(s => ...)`（变体按声明序应用，
> 同开时后声明胜，与 CSS 同优先级按源顺序一致）；绘制段键含状态位——状态翻转段重建、复用帧正确。
> 画廊 primary/tool 全部带 hover 提亮/active 压暗变体；顺带修复焦点环脏区覆盖不足（1×→1.5×+1，
> GPU 逐像素比对抓出环外缘泄漏）。`StyleTests` +4、`PseudoStateTests` +6；**688 个用例
> （346/241/101），全绿**；selfcheck PASS；静态出图 hash 与 D101 一致（无状态变化零漂移）。
>
> **AppBuilder + 依赖注入已通过（2026-11-09，D101）**：Avalonia 式启动形态——`AppBuilder.Configure<TApp>()`
> 链式装配（`.UseWindows().UseVello().UseHarfBuzzText().UseTheme(...).UseStyles(...).ConfigureServices(...)`
> `.Start(args)`）；`Application` 基类（`Services` 容器 / `MainWindow` 描述式 `Window` / `OnFrameworkInitializationCompleted`），
> `ClassicDesktopLifetime`（Platform.Windows）把窗口描述材料化为 Win32 + 表面 + `WindowHost` 帧循环；
> 依赖注入用 Microsoft.Extensions.DependencyInjection（平台 setup 先注册、用户 `ConfigureServices` 后注册可覆盖）；
> Runtime 不依赖任何平台/渲染包——主题经 `IHostTheme` 标记、样式经 `IStyleProvider` 泛型接线，
> 平台能力全走 `IPlatformSetup` 扩展片（`UseWindows`/`UseVello`/`UseHarfBuzzText`）。
> 画廊改造为 `GalleryApp` + `GalleryLaunchOptions`（CLI→DI），`--cpu/--shot` 走 `Build()` 离屏对照
> （同一套装配：样式/主题/文本一致）。`AppBuilderTests` 8 例；**678 个用例（Headless 340 /
> Compiler 241 / Rendering 97），全绿**；GalleryCheck selfcheck PASS。
>
> **画廊完全重构（2026-11-09）**：根 `ScrollViewer` 包整页——内容超出窗口即可滚动（窗口根有限高度
> 使视口生效）；分区卡片陈列框架全部能力：模板组件（@bind/@if/@foreach/@switch）、Button/CheckBox/
> TextBlock、StackPanel/WrapPanel/Grid（附加属性）、ScrollViewer、VirtualizingStackPanel（1000 项）、
> 三层样式按钮（theme/class/inline）+ Visual 覆盖卡片。selfcheck 升级为 9 步（新增 EnsureVisible
> 滚动后点击，目标按 Label 查找不再依赖树序）；`--light` 参数验证浅主题；帧成本 avg 2.99ms /
> p95 3.40ms（与重构前持平，retained 段缓存复用帧不受内容增长影响）。
>
> **表达式语义预检已通过（2026-11-09，EUI049 Warning）**：属性 `{expr}`、`@if` 条件、插值文本段
> 经探针编译在使用点直接提示「拼错成员/类型不匹配」（CS0103/CS0029），不再等 C# 编译才发现；
> Warning 不短路生成——类型错误仍经 `#line` 由 C# 报权威错误（spec/02 §2.4 规则 11）。
>
> **G4 列表虚拟化切片已通过（2026-11-09）**：`VirtualizingStackPanel`——`ItemCount` + `ItemFactory`
> 只实例化视口窗口（含上下各 1 缓冲；10 万行 RealizedCount=22），extent = 均匀行高 × 行数；
> 滚动在 ArrangeCore 同步窗口（滚动只触发 Arrange 失效、Measure 被约束短路）；无滚动祖先退化全量堆叠。
> 容器复用（pooling）、变高行、item 模板编译 RenderUnit 留后续片。表面 +1 标签（9 标签 / 38 属性项 / 74 事件项）。
>
> **G4 ScrollViewer 切片已通过（2026-11-09）**：`ScrollViewer : Panel` 滚动视口——首子元素为内容，
> 测量纵给无穷量出 Extent、视口 = min(extent, 可用)（可用无限 = 不滚动）；`VerticalOffset`/`HorizontalOffset`
> + `ScrollTo*/ScrollToEnd`（使用点钳制）；滚轮经 `PointerWheelChanged` 冒泡路由（Win32 `WM_MOUSEWHEEL`
> 已接线，一格 = 48px），容器在冒泡路径上消费并置 Handled；绘制侧强制视口 PushClip + 叠加滚动条拇指。
> 模板表面 +1 标签（ScrollViewer，自带 5 属性项）/ 全元素 +1 滚轮事件（33 属性项 / 66 事件项）。
>
> **G4 Grid 布局切片已通过（2026-11-09）**：`Grid : Panel` 行列布局——轨道紧凑串
> `RowDefinitions="Auto,*,2*,120"`（未设置退化单星）、`Gap` 轨道间距；固定轨精确约束、
> Auto 取格内最大期望、星号按权重分剩余（可用无限时退化按内容）；`Grid.Row`/`Grid.Column`
> 落格（越界钳制、同格叠放）；空透明块不占格（D77）；非法定义 fail-fast。表面 +1 标签
> /+6 属性项；模板全管线端到端（`GridTests` 14 例 + fixture 用例）。
>
> **G4 附加属性切片已通过（2026-11-09）**：模板 `Grid.Row="1"`/`Grid.Column={expr}` → 生成代码
> 直调静态 setter（`global::EclipseUI.Elements.Grid.SetRow(el, 1)`）；`AttachedProperty<T>` 以
> `ConditionalWeakTable` 弱表存值（元素卸载随 GC 回收，K33 口径）、重复赋值复用值盒；目录新增
> `UiAttachedPropDef` 与扫描规则（顶层宿主、Get/Set 配对、值类型一致、同 (Owner,Name) 冲突先声明胜出）；
> EUI031 对附加属性解禁——已注册的正常解析，未知点号名按未知附加属性报 EUI002（带最近名建议）；
> 组件标签同样可携带；表达式值走 `U{N}`/`R{N}` 动态调用语句；内置表面 +2 附加属性项（14 §14.5.5、
> spec/02 §4.19，`G4AttachedPropertyTests` 9 例 + 扫描规则 7 例）。
>
> **G4 首批切片已通过（2026-11-09，K33/D89 + D90）**：① `Element.Unmount` 卸载链路——
> `UnmountedEvent`（Direct 路由，`[UiIgnore]` 退出模板事件表面）、子树级 `OnUnmount` 钩子、
> 组件根订阅自 `Dispose`（作用域/绑定随之解除；Dispose 退订防泄漏回归）、焦点离树修剪
> （`UnmountTests` 9 例）；手工 `Children.Remove` 保守不触发（契约锁定）。② `WrapPanel`
> 流式布局（水平/竖直换行、Gap、越界独占一行，`WrapPanelTests` 5 例）——内置表面 5→6 标签、
> 17→22 属性项、37→44 事件项。
> **635 个用例（Headless 327 / Compiler 241 / Rendering 67），全绿**——既有像素 golden 红
> 已随 D91 定位为 AA 舍入差 1 并随基线重生成清零。
>
> **S6 语法对齐（✅ 已实现 2026-11-08，K23 + K24–K31）**：`.ui` 语法表面向 Blazor/Razor 对齐——
> 模板 `@using`（内置控件也须导入、生成代码保留 using，K22）、`@using Alias = Ns.Type` 类型别名
> （同时可当标签用，用于解决第三方控件与内置控件的 tag 重名，K31）、`@code` 单文件「代码 + 模板」（位置不限，K21）、
> `else`/`else if`/`@switch` 流程控制、`@* *@` 注释；**不做** `@page`/`@rendermode`/`@inject` 等 Web 指令，
> **不复用 Razor 编译器与 Blazor 运行时**，K1–K18 保留元素树 + 失效重算模型不变。
> **`@namespace Dotted.Name` 必填**（2026-11-11）：生成文件的命名空间只由它决定，不再跟随
> code-behind / `RootNamespace` 推导；缺失 `EUI055`、与 code-behind 不一致 `EUI056`。
> **没有保留值**——`global` 只是普通名字，全局命名空间的 code-behind 必须搬进命名空间。
> **`.ui` 独立成组件**（2026-11-11，spec/02 §2.7）：code-behind **不强制**，只有 `.ui` 也能
> 被别的模板 `<Counter />` 引用；想要完整 C# IDE 能力或对外参数时再建同名 `.cs`
> （参数写在 `@code` 里只对该模板内部可见）。见 [spec/01 §4.2](./spec/01-template-syntax.md)、
> [spec/02 §2.2](./spec/02-generated-code-contract.md)、[§2.7](./spec/02-generated-code-contract.md)。
> 目标语法见 [spec/01 §1.5](./spec/01-template-syntax.md)，权威决策见 [spec/02 §14](./spec/02-generated-code-contract.md)，
> 偏差 D85–D88 已收敛（见 [spec/02 §12](./spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)），
> 计划见 [design/16](./design/16-blazor-alignment-plan.md)。全部样例与约 94 例 golden 已补 `@using` 迁移，
> `dotnet test EclipseUI.slnx` 用例 548 → 567（K31 别名新增 19 例：前端 15 / 生成器契约 4）。
>
> **UI 线程调度切片已通过（2026-11-12，D114）**：按 [design/15 §15.14](./design/15-state-and-reactivity.md)
> 落地单线程 `UiSynchronizationContext`（队列 + `Post`/`Send`/`Drain` 快照批 + `UiThreadId`、
> `CreateCopy` 共享单队列），`Component.InvalidateSubtree()` 沿 `Adopt` 所有权树按批整体失效
> （块内组件不在 `_tracked` 里，只走所有权树才不会漏）；`WindowHost.Run` 安装/还原上下文并把
> tick 顺序改为 PumpMessages → Drain → 自动失效 → ProcessInvalidations → 布局 → present，
> `HeadlessRoot` 提供显式安装/排空入口。生成器事件包装 `h(); Invalidate();` **不变**——
> `async void` 处理器第一次 await 后，续体回 UI 线程，下一批 drain 写入状态并自动重渲染，
> 「后台干活、回 UI 更新」成为合法默认；`ConfigureAwait(false)` / 裸线程池写入仍是 UB，
> Debug 下 `Element.SetValue`/`Component.Render` 抛 `UiThreadViolationException`，`async void`
> 异常经上下文收回 UI 线程、以 `AggregateException` fail-fast。契约正本同步到
> [spec/03 §9.3](./spec/03-reactivity-and-binding.md#93-异步与后台线程)。新增 16 例
> （`UiSynchronizationContextTests` 15，含 `Fixtures/AsyncProbe.ui` 真生成模板端到端；
> + `UiSynchronizationContextPaintTests` 1，后者用记录型绘制器断言 retained 段以新文字重建，
> 锁住 D113 盲区）；`dotnet build` 0 警告，全量 1066 例中 1064 绿——2 条红来自工作区里
> 本片之前就存在的 `Counter.ui` 手工验证改动
> （`OnClick={Test}` 且 `count+=5`，端到端用例仍断言 +1），与本片无关。
>
> **G5 Rider 壳已落地（2026-11-12）**：`ide/rider` 按 [design/06 §6.5](./design/06-ide-support.md)
> 走 lsp4ij 0.21.0（Rider 2024.2+）——Language/FileType 两对 + TextMate bundle provider
> （grammar 仍由 `ide/vscode/syntaxes` 单一来源经 Gradle 组装）+ `LanguageServerFactory`
> 拉起同一份 stdio server，`languageId` 与 VS Code 一致。为让 lsp4ij 的 watcher 生效，
> server 在 `initialized` 后发 `client/registerCapability` 动态注册六项 glob（VS Code
> 静态 watcher 保留，通知幂等）；IntelliJ 的 `file:/D:/…` / `file://D:/…` URI 形态补进回归夹具。
> `node ide/rider/test/plugin.test.js` 12 项静态比对扩展点/类名/grammar/watcher/
> 启动参数/wrapper，IDE 侧 199 例全绿；Temurin 17 + Gradle 9.0.0 wrapper 下
> `compileJava` 与 `buildPlugin` 真实通过（产物 `ide/rider/build/distributions/*.zip`），
> **仅剩** Rider 内人工冒烟未做；`eui-decompiled:` 虚拟文档的 content provider 留后续片。
> 见 [ide/README.md §4](./ide/README.md)。

## 核心理念

**模板不是"字符串拼 UI"的外壳，而是编译单元**——编译期完成类型检查、字面量转换、
更新路径分析，运行时只执行生成好的指令：无运行时 diff，无虚拟 DOM，
绑定点由编译器生成定向更新函数；状态是普通 CLR 成员，失效驱动定向重算。

## 目录结构

```
design/                          设计文档（架构 8 篇 + 计划、切片报告、控件契约）
src/
  EclipseUI.Runtime/             运行时：失效队列与所有权、StyledProperty 属性引擎、
                                 元素树、measure/arrange 布局、块协调器（v4：`Refresh`/`BlockScope`）、
                                 路由事件、InputRouter 输入/焦点、Panel.Padding/ClipToBounds、无头宿主、
                                 Application/AppBuilder/Window（启动形态，D101）、
                                 控件契约声明特性（`UiAttributes.cs`：`[UiContent]`/`[UiIgnore]`/
                                 `[UiParameter]`/`[UiSlot]` + `UiContentKind`）
  EclipseUI.Text.Shaping/        HarfBuzz shaping（HarfBuzzSharp）：先 shaping 再测量，
                                 HarfBuzzTextPainter 让布局与绘制共用同一 shaping 缓存；
                                 UseHarfBuzzText() AppBuilder 扩展（字体候选/回落，D101）
  EclipseUI.Compiler/            .ui 编译器前端：Lexer / Parser / AST / 字面量转换与建议 / 诊断（netstandard2.0）
  EclipseUI.Compiler.Metadata/   控件契约目录（C1–C2）：声明式元数据模型 + Roslyn 符号扫描器，
                                 从符号派生模板目录（标签/属性/事件/内容/参数/插槽），不执行程序集
                                 （netstandard2.0；C2 起由分析器与生成器同源消费，源码内嵌进生成器）
  EclipseUI.Compiler.Generator/  Roslyn 增量源生成器：code-behind 发现、@foreach item 类型推断、
                                 @bind 源可写性校验、代码发射（U/R 定向更新、#line 映射回 .ui）
samples/Gallery/                 画廊共享 UI（平台无关，Avalonia 式 App 本体）：GalleryApp
                                 （Application 子类）+ GalleryLaunchOptions（CLI→DI）+ 展示页
                                 Counter（计数器/条件/双向绑定）、TaskList（@foreach Key 对齐）、
                                 Prefs；主场景 GalleryScene + 截图/字体辅助
samples/Gallery.Desktop/         画廊 Windows 平台入口：AppBuilder 链式装配（UseWindows/
                                 UseVello/UseHarfBuzzText）+ --cpu/--shot 离屏对照；
                                 验证/自检在 sandbox/GalleryCheck
tests/EclipseUI.Headless.Tests/  340 个无头测试：属性引擎、布局（Grid/WrapPanel/ScrollViewer/
                                 列表虚拟化，含 padding 与块布局透明）、块协调、输入/焦点路由、
                                 卸载链路、shaping、编译器、字面量转换与使用点诊断（C3）、
                                 失效队列/所有权（含接管迁移与死循环上限）、具名插槽、附加属性、
                                 AppBuilder 装配契约（`AppBuilderTests` 8 例，D101）、
                                 端到端（含泄漏回归）、运行时硬化（`RuntimeHardeningTests`：
                                 分配预算/去装箱/泄漏回归）
tests/EclipseUI.Compiler.Tests/  241 个测试：生成器契约（确定性 hint、U/R 形态与 Render、
                                 块/绑定生成、@bind 源与目标诊断、集合与嵌套 item 类型推断、
                                 `#line` span 映射、按文件增量、`EUI045` code-behind 冲突、
                                 元数据声明处与标签码分流出口、`EventHandlerSignatureTests`
                                 EUI024 签名匹配 16 例）
                                 与目录扫描（发现规则、属性/事件/
                                 内容/参数/插槽派生、`StyledPropertyRegistry` 交叉一致、
                                 失效级别守卫、跨程序集标签冲突、表面快照与差集）
tests/EclipseUI.Rendering.Tests/ 84 个渲染测试：图元/作用域/文本/功能矩阵/确定性/CPU-GPU 交叉一致
                                 门禁（`CrossBackendGateTests` 14 场景矩阵）、场景图绘制语义、
                                 damage 脏区检测、retained 段缓存等价（`RetainedPaintTests`）、
                                 `PixelGoldenTests` 双层像素门禁 + `PixelProbe` 探针 DSL
src/EclipseUI.Rendering/         渲染契约：DisplayList、DrawCommand、Brush、Transform、PngEncoder、
                                 SceneGraph/ElementPainter（元素树→display list）、FrameDamage/DamageTracker、
                                 IRenderBackend、ISurfacePresenter（上屏目标）、
                                 ISurfaceFactory（AppBuilder 表面扩展点，D101）
src/EclipseUI.Rendering.Vello/   Vello 后端：VelloSceneBuilder（CPU/GPU 共用）、CPU 离屏、wgpu surface、
                                 UseVello() AppBuilder 扩展（D101）
src/EclipseUI.Platform.Windows/  Win32 窗口宿主：消息循环、DPI、resize、鼠标/键盘事件、
                                 WindowHost（布局+绘制+damage 跳帧上屏循环）、UseWindows() +
                                 ClassicDesktopLifetime（AppBuilder 桌面生命周期，D101）
ide/                              IDE 支持（design/06）：一份共享 LSP server + 两个薄壳
  EclipseUI.LanguageServer/       stdio LSP：诊断/标签·属性·枚举·指令补全/悬停/大纲，
                                  目录由 UiCatalogFactory 符号派生（与编译器同源）
  vscode/                         TextMate grammar（.ui / .eustyle）+ 语言配置 + 片段 + 薄客户端
  rider/                          Rider 壳：lsp4ij 插件（Language/FileType + TextMate bundle + server 启动命令）
sandbox/RenderSpike/             G1 像素管线 spike：PNG 输出 + 性能 bench（不入解决方案）
sandbox/WindowSpike/             G2 上屏 spike：动画场景 + 帧时间分段 + resize 自检（不入解决方案）
sandbox/GalleryCheck/            画廊验证壳：--selfcheck 六步自动化、damage 覆盖率、帧成本测量、
                                 CPU 离屏链路（bench-60fps.ps1 的窗口测量跑这里）
tools/capture-window.ps1         截取真实窗口客户区 PNG，用于像素核对
```

## 快速开始

```bash
dotnet build EclipseUI.slnx
dotnet test  EclipseUI.slnx

# G1：离屏 PNG 与性能表（sandbox，不入解决方案，可单独 dotnet run）
dotnet run --project sandbox/RenderSpike -c Release
dotnet run --project sandbox/RenderSpike -c Release -- bench

# G2：真实窗口（Esc/关闭按钮退出；--frames N 自动退出）
dotnet run --project sandbox/WindowSpike -c Release -- --frames 300
dotnet run --project sandbox/WindowSpike -c Release -- --selfcheck   # resize 冒烟

# 画廊（展示）：Counter + TaskList 真窗口交互（事件 → 输入路由 → 失效重算 → 重绘）
dotnet run --project samples/Gallery.Desktop -c Release
dotnet run --project samples/Gallery.Desktop -c Release -- --shot out/gallery-cpu.png
dotnet run --project samples/Gallery.Desktop -c Release -- --cpu --shot out/gallery-cpu.png

# 画廊验证（测试壳，sandbox）：6 步自检 + damage 覆盖率 + 帧成本
dotnet run --project sandbox/GalleryCheck -c Release -- --selfcheck --no-vsync
dotnet run --project sandbox/GalleryCheck -c Release -- --no-vsync --frames 400  # 空闲跳帧：rendered=1
dotnet run --project sandbox/GalleryCheck -c Release -- --always-render --no-vsync --max-fps 60 --frames 1200  # 帧成本
```

模板（`samples/Gallery/Counter.ui`）：

```xml
<StackPanel Gap="8">
    <TextBlock FontSize="30">You pressed {count} times</TextBlock>
    <CheckBox @bind-Checked={showButton}>Button visible</CheckBox>
    @if (showButton) {
        <Button OnClick={() => count++}>+1</Button>
    }
</StackPanel>
```

code-behind（同名 `.cs`，普通 CLR 成员，无继承样板）：

```csharp
public partial class Counter
{
    public int count;
    public bool showButton;
}
```

`.ui` 作为 AdditionalFile 进入编译，由增量源生成器生成 `Build()`：
结构创建指令 + 根级 `U{N}`/块内 `R{N}` 定向更新函数 + 块协调器接线。
表达式就是 C# 表达式（不重写、不补 `.Value`）；控件事件处理器末尾自动 `Invalidate()`。

## 已实现的关键语义

- **属性引擎**：`StyledProperty<T>`（默认值 / 继承 / 失效分级 Measure–Arrange–Render），
  setter 值短路无装箱；变更只标脏，布局在 `HeadlessRoot.UpdateLayout()` 帧内增量执行。
- **文本一等公民**：`<TextBlock>You pressed {count} times</TextBlock>` 编译为多个
  TextRun（经 `ITextContentHost`），各自独立定向更新；静态段不建 run 变量。
- **块协调器（v4）**：`@if` 的 setter 只保存条件与模板，`Refresh()` 首次挂载；条件翻转卸载并
  逆序释放旧 `BlockScope`，条件不变只重跑渲染动作。`@foreach` 每次重新枚举任意
  `IEnumerable<T>`（数组/`List<T>`/字典/LINQ），null 视为空；`Key` 相同 **且** item 身份相同
  才复用，不同 item 重建；条目移除/块 `Dispose` 释放作用域资源，根序列不变时不改 `Children`。
- **资源放置随上下文**：绑定与嵌套块登记到正确作用域——根级 `Track`、条件内容
  `scope.AddDisposable`、item 模板条目作用域；从机制上杜绝 toggle/增删条目时的泄漏。
- **双向绑定**：`@bind-Checked={showButton}` 生成 `UiBinding.TwoWay`；构造时源→目标，
  目标变化回写源并 `Invalidate`，相等短路防回环；源必须是可写左值，非法源/非法目标分别报
  `EUI037`/`EUI040`（编译期，含值类型 item 成员与属性返回的 struct 中间段）；目标必须是
  「支持绑定」的成员——控件是 `{P}Property` 支撑的可写属性、组件是带 `{P}Changed` 的参数；
  **名字存在但不可绑**（只读、没有 `{P}Property`、组件上不是 `[UiParameter]`）报 `EUI040` 并
  带原因，拼错才报 `EUI002`；补全只列会通过的目标。旧语法
  `:bind`/`:value` 已移除（S6/K27，报 `EUI046`），迁移到 `@bind-P`/`P=`；组件级 `@bind-P` 已随 C4 落地
  （`{P}Changed` 回调 + 父推送 `U{N}`/`R{N}`）。
- **输入与焦点**：`InputRouter` 统一 hit test、按下捕获、指针移动/释放与键盘路由；
  无头宿主（`HeadlessRoot`）与 Win32 窗口宿主共用同一条路径。点击保持最深层命中、
  键盘焦点交给最近可聚焦祖先；`FocusManager` 负责 Tab 环形遍历与 Got/LostFocus，
  空格/回车激活（键盘可交互 demo）。按下即 Win32 `SetCapture`，拖出窗口释放不会丢状态。
- **场景图绘制（G2 第二切片）**：`ElementPainter` 遍历元素树，按 `FinalRect` 累加根坐标，
  输出卡片/按钮/勾选框/文本/焦点环/裁剪指令；DPI 作为一次根变换。
  块协调器视觉透明（布局透明留给 G3）。详见 [12 G2 第二切片](./design/12-g2-interactive-slice.md)。
- **文本 shaping**：`ITextShaper` 契约 + `HarfBuzzTextShaper`（HarfBuzzSharp），
  RTL 簇序、比例字宽、行盒高度均有真实字体（arial.ttf）测试；`HarfBuzzTextPainter`
  同时实现 `ITextPainter` 与 `ITextMeasurer`，布局与绘制共用同一个 shaping 缓存。
- **诊断即生命线**：未知标签/属性给最近似名建议；字面量按目标类型编译期转换，
  格式错=编译错误（EUI001–EUI015），`@foreach` 集合不可枚举报 EUI007，同一目标重复写入报 EUI033，
  绑定源/目标非法报 EUI037/EUI040，旧 `P:bind`/`P:value` 报 EUI046（已移除）；
  表达式经 `#line` 映射，Roslyn 报错直接落在 `.ui` 上。
- **无头测试**：不开窗口断言元素树、布局结果、失效重算、块生命周期、事件冒泡、命中与焦点（设计文档 07 的落地）。
- **GPU 上屏（G2）**：`DisplayList` → `VelloSurfaceBackend` → wgpu(Dx12) surface → 窗口像素；
  `WindowHost` 把 Measure/Arrange → `ElementPainter` → present 串成帧循环，
  纯 Win32 宿主（无 WinForms/WPF），`WM_SIZE`/`WM_DPICHANGED` 触发 `surface.Configure`。
  踩过的坑与数据见 [11 G2 第一切片](./design/11-g2-surface-slice.md)。
- **damage v0（G2 第三切片）**：`Element.RenderVersion` 记录一切可能改变像素的变更，
  `DamageTracker` 对比上一帧 `(绝对矩形, 版本)` 快照产出合并后的 `FrameDamage`；
  无脏区的 tick 跳过 present（`WM_PAINT`/resize/DPI 变化强制整帧）。
  空闲 400 tick 仅上屏 1 帧；跟踪开销 ≈ 0.1 µs/元素。真正的局部重绘需要 retained layer，
  尚未实现。详见 [13 G2 第三切片](./design/13-g2-damage-v0.md)。

## 当前边界（已知限制）

- ~~`@switch` 未支持；组件标签、组件参数与组件级 `@bind`（`{P}Changed` 父推送/子回调）未实现~~
  **组件标签已随 C4 落地（2026-11-05）**：参数（含 `Required`/委托事件/默认内容）与组件 `@bind` 可用；
  **`Ref={field}` 已随 C5 落地（2026-11-05）**：元素/组件实例赋给 code-behind 字段，
  `EUI029` 分层（分析器语法级 + 生成器探针语义级）；`EUI019`/`EUI020`/`EUI021` 使用点诊断
  仅对引用程序集控件生效（源项目控件落 `EUI002`/`EUI006`，声明处 `EUI026` 暂无编译期出口，D74② 开放）；
  **具名插槽（`<Slot>`/`<ContentSlot>`）已随 C6 落地（2026-11-05）**；**`@switch` 已随 S6 落地（2026-11-08，K29/K30）**：
  多 section + `default`、常量标签（否则 `EUI047`）、无贯穿语义（`break;` 单独结束该行，多标签形态 `EUI100`）。
- 文本：无头测试默认用确定性 naive 测量；窗口样例接入 HarfBuzz。多行断行、bidi 混排、字体 fallback 未实现。
- 输入：无头注入与 Win32 鼠标/键盘已共用 `InputRouter` 并路由进元素树；字符输入（`WM_CHAR`）、
  IME、Shift+Tab 未接（暂无文本编辑控件）。
- 生成代码不入库（内存生成）；`Generated/` 落盘仅作检视。
- 渲染：`DisplayList` → Vello CPU（PNG/CI）与 GPU surface（真实窗口）两条路径已通；
  scene graph 与元素级 damage（空闲跳帧）已落地（G2 第二/三切片）；
  **retained 构建层已落地（D93 段级缓存，帧间命令构造趋零）**；真正的局部重绘（脏区光栅化）
  等 VelloSharp 提供 scissor API，合成器仍未实现；
  样式系统已落（D98/D99 主题 token + Class 样式分层、D100 ThemeColor、D102 伪类、D103 :disabled、
  D104 样式表编译器 `.eustyle`/`@style`、D105 模板 `Class`/`Style` 转正、D106 嵌套塌缩 + `.ui` 注入、
  D107 注册制伪类 `PseudoStatesRegistry`/`@states`/EUI054）——**E1–E5 五片全部落地**；
  控件模板（S-D）留后续片。
- 字体 fallback 未实现（缺字显示 `.notdef`）；CPU 光栅化每次约 60 ms 固定开销，仅用于 CI/离线导出。
- 3D：**不提供 3D 引擎**。规划为 WebGPU 视口控件——应用自带渲染器，框架负责布局、
  裁剪、DPI、输入与纹理合成（见 [05 布局、渲染与输入](./design/05-layout-rendering-input.md) 与 G7）。
- 测量机（RTX 2070 物理 59Hz + GameViewer 虚拟链）2026-11-09 复测：GalleryCheck 帧成本
  avg 3.00ms / p95 3.43ms（预算 ~18%），`Immediate` 墙钟 60.0fps；`Fifo` 上屏受虚拟链 DWM
  合成波动与上游 `frame latency=2`（VelloSharp 硬编码，C ABI 无字段）双重阻塞，稳定 60fps
  结论待物理目标机复测——操作指引见 [60fps 复测报告 §9](./design/reports/target-machine-60fps-2026-11-08.md)。

## 下一步（按开发计划）

G1 已出结论（Go），G2 前三切片已通过：`CounterApp` / `TaskList` 已在真实窗口可交互，
damage v0 让空闲帧不再提交 GPU。G3 的 C1/C2 目录派生切片、C3 转换器/使用点诊断切片、
**C4 组件标签切片与 C5 `Ref`/使用点诊断切片已全部通过**（手写目录删除，模板表面 17 属性项 /
9 个事件名；转换器补齐到 spec/01 §5.2 全表；自定义组件可写入 `.ui`；`Ref={field}` 与
EUI019–021/EUI029 使用点收敛，落地记录见
[14 §14.12.2–§14.12.5](./design/14-control-template-contract.md)）与 **tokenizer 切片**（事件委托形态与
`@foreach` 头的正则判定换 Roslyn 解析/深度感知扫描）。**C6 具名插槽与 G3 七个小切片
（块布局透明 / 接管失效迁移 / 按文件增量 / `#line` 列映射 / `EUI045` 冲突诊断 / 诊断出口补全 /
EUI024 事件处理器签名匹配 ✅ D82）也已全部通过**，落地记录见
[14 §14.12.6](./design/14-control-template-contract.md) 与 [09 开发计划](./design/09-development-plan.md)。
当前优先级：**G2 仅剩目标机稳定 60fps 结论**（像素探针 DSL ✅ D91、CPU/GPU 交叉一致性门禁 ✅ D92、
构建层 retained ✅ D93；局部重绘光栅化待上游 scissor API）；**S4/S5 已收敛**（去装箱 ✅ D83、
Unmount ✅ G4/K33、分配预算门槛 ✅ D94、AOT/裁剪试点 ✅ D95、死循环上限与最终审计 ✅ D96；
CI 单列未做）；**S6 语法对齐（K23，✅ 已实现 2026-11-08）**；G4 已落地首批切片（Unmount/WrapPanel/
Grid/ScrollViewer/附加属性/列表虚拟化，2026-11-09），余样式/主题与泛型标签（K19 暂缓）、
IDE/DevTools 随后。
完整路线图见 [09 开发计划](./design/09-development-plan.md)，G1 数据见
[render-gate-2026-11](./design/reports/render-gate-2026-11.md)，G2 进度见
[11 G2 第一切片](./design/11-g2-surface-slice.md)、[12 G2 第二切片](./design/12-g2-interactive-slice.md)
与 [13 G2 第三切片](./design/13-g2-damage-v0.md)。

1. **G2 剩余**：目标机稳定 60fps 结论（~~像素探针 DSL~~ ✅ D91、~~CPU/GPU 交叉一致性门禁~~ ✅ D92、
   ~~retained 显示列表~~ ✅ D93——构建层先行，脏区光栅化等 VelloSharp 上游 API）。
2. **G3 语义/编译器硬化**（与 G2 并行）——七个小切片全部收敛：~~块布局透明化~~ ✅ D77（2026-11-05）、
   ~~接管失效迁移~~ ✅ D78（2026-11-07）、~~表达式形态判定的正则替代（tokenizer）~~ ✅（2026-11-05）、
   ~~按文件增量~~ ✅（2026-11-08）、~~`#line` 列映射~~ ✅ D79（2026-11-08）、
   ~~`EUI045` code-behind 冲突诊断~~ ✅ D57/D80（2026-11-08）、
   ~~诊断出口补全~~ ✅ D74 ②/D81（2026-11-08）、~~EUI024 事件处理器签名匹配~~ ✅ D82（2026-11-08）、
   ~~组件标签与组件级绑定（C4）~~ ✅（2026-11-05）、
   ~~`Ref` 与剩余使用点诊断收敛（C5）~~ ✅（2026-11-05）。
3. **S6 语法对齐**（K23，✅ 已实现 2026-11-08）：`@using` / `@code` 单文件 / `else`+`@switch` / `@* *@`，
   出口标准 = D85–D88 关闭 + 样例与 golden 迁移 + 用例基线全绿（497 → 548）；计划见 [16](./design/16-blazor-alignment-plan.md)。
4. **G4 样式/控件/虚拟化**：~~`VirtualizingPanel`~~ ✅（2026-11-09，窗口虚拟化 + 均匀行 extent）；
   基础控件集首批 ✅（WrapPanel/Grid/ScrollViewer/附加属性/Unmount，2026-11-09）；
   ~~主题切换~~ ✅ D98、~~Class 样式 + 有效值分层~~ ✅ D99、~~按主题资源查找~~ ✅ D100、
   ~~伪类~~ ✅ D102/D103、~~样式表编译器~~ ✅ D104（E1）、~~模板 Class/Style 转正~~ ✅ D105（E2）、
   ~~嵌套塌缩 + .ui 注入~~ ✅ D106（E4，2026-11-09）、~~注册制伪类~~ ✅ D107（E5，2026-11-10）——
   **样式系统 E1–E5 全部落地**；余控件模板（S-D）。
5. **G5 IDE**：~~P0 语法高亮 + 诊断通道~~ ✅ 2026-11-10（`ide/EclipseUI.LanguageServer` + `ide/vscode`，
   见 [ide/README.md](./ide/README.md)）、~~P1 补全/悬停/大纲~~ ✅（标签/属性/事件/参数/附加属性/
   `@bind-`/枚举/指令）、~~表达式投影（L2 悬停/补全）~~ ✅ 2026-11-10（design/06 §6.3：探针合成树复用
   生成器的 `ExpressionProbe`，code-behind 取 `{Name}.cs` 兄弟文件；`{expr}` 内成员补全 + 类型悬停，
   `@foreach` 变量入环境）、~~跳转定义 + 重命名（L3）~~ ✅ 2026-11-10（探针符号反查 `Locate`：
   foreach 变量落回本文档、code-behind 成员跳到兄弟 `.cs` 声明名字；重命名 `prepareRename` +
   WorkspaceEdit，foreach 变量完备改名、code-behind 成员扫 `.ui`+`@code`+兄弟+工作区 `.cs`）——
   **Rider 壳 `ide/rider` 已落地（lsp4ij 插件，接同一个 server，server 侧零分叉）**；
   余语义着色，以及 P3 `#line` 调试映射。
