# 04 · 运行时与元素系统

> 2026-11-13，当前统一实现。API契约以 [spec/03](../spec/03-reactivity-and-binding.md) 和 [spec/04](../spec/04-ui-type-metadata.md) 为准。

```text
UiObject
├─ Element → Panel / Button / TextBlock / …
└─ Window → MainWindow（ui或C#）
```

UiObject统一初始化、更新队列和资源登记；Element提供几何、属性层、绘制、命中和路由；Window通过普通单内容成员承载Element，由平台宿主建立原生窗口。窗口不属于Element，因此不能加入控件Children。

ui根类型直接作为生成基类。InitializeUi初始化当前实例，子标签实例直接加入声明的内容成员。pure C#类实现相同InitializeUi/Render，无须包装根、转发属性或注册第二套所有权关系。

EnsureInitialized晚于对象初始化器及标签输入，幂等且不在基类构造器调用。根输入参数和槽先就位，让基类消费，随后追加模板默认值和结构。失败构建由短命UiConstructionScope与UiObject清理资源。

更新队列按真实内容树归属共享调度根，后台Invalidate仅入队，UI线程按深度和批次执行Render。结构移动迁移pending工作；循环上限针对反复更新轮次，不限制一批正常节点数量。

Element的StyledProperty有效值层为local > template > class > inherited > theme default > property default；模板值scope保留setter校验。类集按内部/外部顺序合并；继承属性沿实际Element父链处理。主题默认值由实际树上的SetThemeDefaults安装，见[Fluent皮肤](./27-fluent-theme.md)。

绑定、计时器和块资源登记到Track或BlockScope；子节点通过真实树释放。Children.Remove只摘除，Unmount摘除并释放；Dispose幂等，逆序清理并聚合异常。不存在组件根订阅卸载事件的桥接层。

HeadlessRoot统一接受UiObject；WindowHost接受布局根与统一调度根。UI同步上下文在帧布局前排空；无头和窗口宿主共用更新机制。

旧版设计已保存到[历史快照](./history/2026-11-13-before-legacy-removal/README.md)。完整切片与回归结果见[21](./21-root-type-unification.md)。
