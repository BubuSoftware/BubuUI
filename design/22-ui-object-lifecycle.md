# 22 · 统一 UiObject 生命周期

状态：已实现，2026-11-13。行为以 [spec/03](../spec/03-reactivity-and-binding.md#5-生命周期与所有权) 为准。

## 目标与钩子

ui 生成类与纯 C# 类型共用 UiObject 的初始化、宿主挂接、更新和最终释放。Window 不需要独立生命周期实现，也不因此成为 Element。

| 钩子 | 时机 | 次数与顺序 |
|---|---|---|
| OnInitialized | 完整 InitializeUi 继承链结束，Ref、内容和初始动态值就位 | 一次；IsInitialized=true，不保证 Parent 或宿主存在 |
| OnAttached | 接入活跃宿主树 | 每次接入；父先子，整棵接入子树 IsAttached=true 后通知 |
| OnDetached | 离开活跃宿主树 | 每次离开；子先父，同层逆内容序，整棵摘除子树 IsAttached=false 后通知 |
| OnUpdating | ShouldRender 接受本轮更新后、完整 Render 继承链之前 | 每次实际更新，不含初次构建 |
| OnUpdated | 完整 Render 成功返回后 | 更新失败或对象已释放时不调用 |
| OnDispose | 子树与 Track 资源清理之后 | 一次，最终释放钩子 |

InitializeUi、Render 继续承担生成代码入口，不新增另一条构建链。ui 作者在 @code 中 override 表中钩子；派生类型通过 base 保留基类行为。

```csharp
protected override void OnInitialized()
{
    base.OnInitialized();
    // 此时可读取模板 Ref 和初始属性。
}

protected override void OnAttached()
{
    base.OnAttached();
    // 开始依赖宿主的活动，如动画或临时订阅。
}

protected override void OnDetached()
{
    // 停止临时活动；对象仍可重新挂接。
    base.OnDetached();
}

protected override void OnDispose()
{
    // 最终释放；绑定、块、计时器等 IDisposable 可预先交给 Track。
    base.OnDispose();
}
```

OnUpdated 表示动态值刷新结束，不表示布局或像素已经呈现。更新期间再次失效会进入下一轮，因此一次 Invalidate 不承诺只触发一对更新钩子。ShouldRender/OnUpdating 中改变所属调度根时，旧宿主停止执行该对象，更新转交新根队列；OnUpdating 中销毁或移交对象时，不补发 OnUpdated。初始化或更新期间重入 ProcessInvalidations 只延后队列，不嵌套执行钩子。

## 所有权与宿主

- 离线构建真实内容树只保证初始化，不触发宿主挂接通知。
- Children.Add、Remove、Clear 与 SingleContent 替换统一传播活跃宿主状态。摘除保留内部父子关系，只有被移除子树根的外部归属被解除。
- 一个根只能由一个活跃宿主持有；宿主身份令牌防止旧宿主误摘除已移动的树。
- HeadlessRoot 构造时挂接，实现 IDisposable；宿主释放只摘除树，调用方最终 Dispose 树。其同步上下文仍由显式 scope 安装。
- Windows 帧循环先安装同步上下文再挂接，退出时先摘除再还原上下文；原生窗口的创建仍归平台，应用生命周期最终释放 Window。
- 单内容宿主更换布局根时同步输入路由、焦点、捕获与绘制缓存，旧内容摘除但不自动销毁。
- Dispose 自动解除外部父节点/内容归属、摘除活跃子树、逆序释放子树和 Track 资源，最后 OnDispose。Unmount 只是同一终止入口的便利方法；删除旧 Element.OnUnmount 与 UnmountedEvent。
- foreach 仅退休被移除/替换条目；保留节点换序不重复挂接。虚拟化条目离开窗口时最终释放，item 0 探针可离树缓存并在容器最终释放时清理。

## 异常与重入

初始化钩子失败会清理并终止对象，不允许再次初始化部分树。宿主首次挂接失败会通知其他节点，再撤销已建立的挂接；对象本身仍可复用。

挂接状态先于回调提交；通知使用快照和版本检查。OnAttached 中添加节点会正常通知新节点；移除尚未收到挂接通知的节点时，不为它补发无配对的摘除通知。已收到挂接通知的节点允许在自己的回调中释放。

摘除按逆序进行，尚未收到 OnDetached 的祖先不能被重新挂接或销毁；这类交错操作抛出明确异常，其他通知继续完成。节点自身的 OnDetached 已开始后，可处理自己的最终清理。

生命周期通知与资源清理异常均继续处理剩余节点/资源，最后聚合抛出。只保证框架状态和清理路径完整，不回滚任意用户副作用。所有钩子在 UI 线程执行；跨线程只允许 Post 和 Invalidate 标记。

## 验证

测试位于 LifecycleTests、HostLifecycleTests、BlockLifecycleTests、UnmountTests 和 VirtualizingLifecycleTests，并使用 LifecycleControl.bui / LifecycleWindow.bui 验证真实生成类型。覆盖初始化与宿主状态、通知顺序、移除重挂、更新中移动、异常清理、回调内增删/释放、双宿主拒绝、内容替换、同步上下文、块复用与虚拟化资源。

验证记录见 [生命周期验证报告](./reports/ui-object-lifecycle-2026-11-13.md)。
