# 03 · 更新、绑定与资源契约

> 状态：统一 UiObject 机制，2026-11-13。适用于 ui 生成类型和纯 C# 类型。
> 生成形态见 [02](./02-generated-code-contract.md)，类型声明见 [04](./04-ui-type-metadata.md)。

## 1. 状态与更新模型

状态是普通 CLR 字段/属性、集合和对象；框架不订阅 INPC/INCC、不进行依赖追踪或表达式改写。UiObject 提供初始化、更新和资源管理；Element 和 Window 都复用这套机制。

`Invalidate()` 使当前对象下一轮执行 Render；父对象更新普通子属性时，生成代码在相等保护后写 setter 并使目标失效。StyledProperty 写入由属性元数据标记布局/绘制脏状态。动态表达式不得依赖求值次数或包含修改树的副作用。

### 1.1 显式状态修改入口

UiObject 为 ui 生成类型与纯 C# 类型提供三种便利写法：

```csharp
UpdateState(() =>
{
    count++;
    message = "updated";
});

using (BeginStateUpdate())
{
    count++;
    message = "updated";
}

await UpdateStateAsync(async () =>
{
    var result = await LoadAsync();
    message = result;
});
```

- `UpdateState(Action)` 在回调退出时通过 finally 调用当前对象的 Invalidate；异步回调使用 `UpdateStateAsync(Func<Task>)`，等待其 Task 完成后再通知，不能把 async void 回调传给同步入口。
- `BeginStateUpdate()` 返回实现 IDisposable 的 `UiObject.StateUpdateScope`；using 块、using 声明、提前 return 和异常退出均在 Dispose 时通知。重复 Dispose 不再通知。
- 回调失败或取消时仍通知，因为普通 CLR 状态可能已经部分修改；原异常和取消状态继续传播，不回滚状态。
- 只安排宿主下一轮更新，不同步执行 Render，不进行相等比较或自动观察。嵌套作用域与回调沿用现有失效队列去重；每个作用域独立在结束时通知，不阻止作用域内手动排空或 await 期间的其它更新。
- 不触发隐式初始化；未初始化或已释放对象的通知仍遵循 Invalidate 的 no-op 规则。
- 回调在调用线程开始执行，不自动派发到 UI 线程；await 沿用当前同步上下文和既有 UI 线程规则。这些入口只通知当前对象；其它对象的状态由各自的状态入口或 Invalidate 通知。

## 2. 初始化

EnsureInitialized 是幂等入口，在对象初始化器/标签输入赋值之后执行。InitializeUi 创建静态内容并完成首次动态求值；初始化不再额外调用一次 Render。

根的普通输入、默认内容参数和具名槽先赋值，基类初始化消费输入，再写本层模板默认值和结构。派生 Render 先执行基类更新。不得从基类构造器调用派生 InitializeUi。

初始化失败时清理已登记资源与已挂接子树；短命构建作用域清理尚未提交对象。失败对象不得再次构造出第二份部分树。

## 3. 失效与队列

- 未初始化或已释放对象的 Invalidate 为 no-op。
- 后台 Invalidate 只读取 UI 线程发布的调度根句柄并入队，不读取父链、不执行用户代码。
- 实际内容树共享根队列；不存在独立组件所有权树。
- 同一批请求按对象去重，按深度升序、同深度入队顺序更新。
- 当前批完成后若又有失效，进入下一轮；100 限制针对更新轮次，不针对独立脏对象数量。
- 初始化或更新钩子内重入 ProcessInvalidations 不嵌套执行更新；失效保留到初始化结束或当前更新结束后的批次。
- 子树移入新父时发布新调度根并迁移旧队列；入队后复查句柄，避免并发移动丢失更新。
- ShouldRender 默认 true；false 跳过本次对象重算，不改变子项身份或布局状态。
- Render 异常向宿主传播，不吞异常，也不提供任意外部副作用事务回滚。

## 4. 宿主

HeadlessRoot 接受 UiObject，经控件本身或通用单内容服务取得布局根。窗口与纯代码单内容宿主遵循同一协议。

每帧次序：排空 UI 同步上下文 → 处理 UiObject 失效 → Measure/Arrange → 构造或复用 display list → damage → 必要时 Present。无头与窗口宿主共用更新机制。

平台宿主仍负责原生窗口和渲染表面；Window 属性 setter 经 IWindowLiveUpdater 更新标题/客户区尺寸，ui 表达式和 C# 赋值使用相同 setter。窗口不是可嵌入的 Element。

## 5. 生命周期与所有权

- 生命周期统一由 UiObject 提供，ui 与纯 C#、Window 与 Element 使用相同钩子。
- InitializeUi/Render 是结构构建和动态更新入口；ui 作者在 @code 中使用 OnInitialized、OnAttached、OnDetached、OnUpdating、OnUpdated、OnDispose，不重声明生成入口。
- OnInitialized 在完整 InitializeUi 返回且 IsInitialized=true 后调用一次；此时 Ref 和初始动态值已就位，但不保证 Parent 或宿主存在。钩子失败同样清理并终止对象。
- IsAttached 表示接入活跃宿主树，不等同于拥有 Parent。离线组树只初始化；宿主挂接时先提交整棵子树的状态，再按父先子调用 OnAttached。
- 离开活跃宿主时先清除子树的挂接状态，再按子先父调用 OnDetached；内部父子关系保留，只有被移除子树的根解除外部归属。摘除不释放，重新挂接可再次调用 OnAttached，不再初始化。
- 内容集合与 SingleContent 的增删替换共用上述机制；foreach 保留节点的换序不产生摘除/挂接通知。一个根不能同时接入两个宿主。
- ShouldRender=true 时依次调用 OnUpdating、Render、OnUpdated；Render 失败不调用 OnUpdated。OnUpdating 中销毁对象或移交调度根会中止本次更新，不调用 OnUpdated。初次构建不额外执行更新钩子。
- 生命周期通知按快照遍历，状态先于回调提交；回调失败继续通知其余节点，最后聚合抛出。宿主首次挂接失败时撤销已建立的挂接。通知过程中可增删内容；尚在等待 OnDetached 的祖先不能被重新挂接或销毁，避免交错的生命周期。
- Children.Remove/Clear 只摘除不销毁。Dispose 是幂等终态：先解除外部内容归属和宿主挂接，再按子树及资源登记逆序清理，最后 OnDispose；尽量完成所有清理并聚合异常。Unmount 是同一终止入口的便利方法，不再存在 Element 专用 OnUnmount/Unmounted 生命周期。
- HeadlessRoot 实现 IDisposable；释放宿主只摘除根，不销毁调用方的树。Windows 帧循环先安装同步上下文再挂接，退出时先摘除再还原上下文；桌面应用最终释放 Window。宿主必须同步单项布局根的替换。
- Track 只登记绑定、块、计时器等资源，不登记第二套子组件所有权边。
- 块根由 BlockScope 管理；构建中的临时对象由 UiConstructionScope 管理，成功提交后清空临时引用。构建失败调用Abort保留根因，清理也失败则聚合全部错误。
- 事件声明与回调只安装一次；卸载后的对象不可继续初始化或处理更新。

## 6. 动态槽与结构块

Render 只刷新已知动态槽、绑定和块，不重新建立静态树。根动态表达式在 U 函数中重算；分支/条目动态表达式在 BlockScope 的 R 动作中刷新。

- if：选择首个为真的分支，或 default；分支不变只刷新动作，分支变化释放旧作用域并构造新内容。
- foreach：每轮枚举 IEnumerable<T>；null 为空。Key 相等且 item 身份相同才复用，引用 item 按引用、值 item 按值比较；同 Key 不同 item 重建并清理旧作用域。
- switch：按 EqualityComparer<T>.Default 顺序匹配 case；无贯穿，default 至多一个，未匹配且无 default 为空。

默认内容参数和具名槽是初始化快照，不共享父模板的动态槽作用域；需要动态内容时使用独立 ui/C# 控件或显式结构块。

foreach刷新构建失败时清理本轮全部新增条目，不提前释放旧条目；成功构建后再统一更新内容和退休旧作用域。块自身终态由UiObject管理，不另设第二个释放标志；清理异常不得使后续条目或基类释放被跳过。

## 7. 属性层与类集

StyledProperty 优先级为 local > template > class > inherited > default。SetTemplateValue/BeginTemplateValues 提供模板默认值，公开 setter 的校验仍执行；使用点赋值和普通 C# setter 写 local。

模板类集与使用点类集分开记录并按“内部在前、外部在后”合并；动态更新不累积旧类。普通 CLR 属性没有 StyledProperty 值层，其通知/回调能力按类型声明决定。

## 8. 双向绑定

### 8.1 源

源必须是可写左值，支持字段、属性和引用 item 成员。拒绝方法调用、索引器、条件访问、只读/静态成员、值 item 写入和属性返回 struct 的中间段；类型必须与目标一致（忽略可空标注）。自绑定拒绝。可判定错误为 EUI037，否则交 C# 映射错误。

### 8.2 StyledProperty 目标

UiBinding.TwoWay 首次源 → 目标，订阅 PropertyChanged；Refresh 更新目标，目标变化回写源并使所属对象失效。相等保护和同步守卫防回环。Dispose 解除订阅。

### 8.3 普通属性目标

目标 P 必须有可写 setter 和形状匹配的 PChanged：返回 void、恰一个参数、参数与 P 同一类型。先安装回写回调，再相等保护推送。控件动作改变 P 后调用回调并 Invalidate 自己；Render 不应无条件触发回写。

这两条路径按成员能力选择，与文件来源无关。内容参数、槽、事件、保留属性和附加属性不作为绑定目标。未知名字 EUI002，已知但能力不足 EUI040。

## 9. 事件与异步

路由事件和 void 委托的生成包装在调用后 Invalidate 声明它的对象。异步 void 处理器首次 await 处返回，后续更新由 UI 上下文的批量排空覆盖。

宿主安装 UiSynchronizationContext，await 默认捕获该上下文；排空一批续体后只使真实挂载子树整体失效一次。Post 可后台调用；Drain 必须在所属 UI 线程。ConfigureAwait(false) 或裸后台任务不获得 UI 写权限。

未安装上下文时，不自动猜测续体线程或监视普通 CLR 状态，调用方负责投递与失效。线程测试必须实际使用独立工作线程，不能依赖可能被等待线程内联的 Task.Run。

## 10. 表达式与身份

属性值变化不重建控件；Key/item 身份变化只影响所属动态块。Ref 始终指向实际节点，重新创建块时重新赋值，调用方不得长期保留已释放引用。样式/主题变化通过属性系统和渲染版本更新，不重新执行静态初始化。

## 11. 旧语法

P:bind/P:value 已移除并报 EUI046；模板不引入 Signal 自动解包、状态代理或可观察路径语义。旧诊断编号停用后不复用。

## 12. 测试契约

至少覆盖初始化一次与失败清理、真实父子顺序、移动后 pending 更新、后台入队与移动竞态、100 个以上独立脏对象、真实更新循环、ShouldRender、参数相等短路、事件/异步续体、绑定回环及卸载退订、块复用和分支清理。

## 13. 实施状态

统一机制和兼容层删除的命令、验证结果与已知渲染基线差异见 [design/21](../design/21-root-type-unification.md)。旧版运行时契约保存于 [历史快照](../design/history/2026-11-13-before-legacy-removal/README.md)，不作为当前 API。
