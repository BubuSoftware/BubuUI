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
> **668 个用例（Headless 332 / Compiler 241 / Rendering 95），全绿**。
>
> **S-C 最小主题资源已通过（2026-11-09，D100）**：`ThemeColor` 暗亮双色——`Visual.BackgroundResource/
> ForegroundResource`（解析优先级 resource > 静态覆盖 > 主题令牌）+ `StyleBuilder` 双色重载；
> painter 在**段构建期**按 `ElementTheme.IsLight` 解析，换主题 `ReplaceTheme` 丢段重建即随新表刷新
> （无需新增失效维度）。画廊分区卡/标题/primary/tool 样式全改双色资源——**亮暗切换整页跟随**，
> inline 静态色保留以并示「静态覆盖 vs 主题跟随」。**670 个用例（332/241/97），全绿**。
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
> 目标语法见 [spec/01 §1.5](./spec/01-template-syntax.md)，权威决策见 [spec/02 §14](./spec/02-generated-code-contract.md)，
> 偏差 D85–D88 已收敛（见 [spec/02 §12](./spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)），
> 计划见 [design/16](./design/16-blazor-alignment-plan.md)。全部样例与约 94 例 golden 已补 `@using` 迁移，
> `dotnet test EclipseUI.slnx` 用例 548 → 567（K31 别名新增 19 例：前端 15 / 生成器契约 4）。

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
  `EUI037`/`EUI040`（编译期，含值类型 item 成员与属性返回的 struct 中间段）；旧语法
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
  样式系统首批已落（D98/D99：主题 token 可替换 + 亮暗切换、Class 样式 + 有效值分层）——
  模板 Class 语法、按主题资源查找、`Style=` 内联与控件模板留后续片。
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
   ~~主题切换~~ ✅ D98、~~Class 样式 + 有效值分层~~ ✅ D99（2026-11-09）；
   余模板 Class 语法、按主题资源查找、`Style=` 内联（S-C）、控件模板（S-D）。
5. **G5 IDE P0–P1**：TextMate grammar + LSP server（表达式投影进 Roslyn）+ VS Code/Rider 壳。
