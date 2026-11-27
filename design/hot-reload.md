# 开发期热重载

BubuUI 通过 .NET 的 `MetadataUpdateHandler` 接收代码更新通知，将更新排入宿主的 UI 更新队列。`.bui` 生成类自动重建自身内容；窗口/组件实例保持不变。此功能用于开发调试，不是线上 DLL 更新机制。

## 启动

在仓库根目录运行桌面 Gallery：

```powershell
dotnet watch --project samples/Gallery.Desktop/Gallery.Desktop.csproj
```

使用 Debug 配置与支持 Hot Reload 的 .NET 运行时。也可在支持 .NET Hot Reload 的 IDE 中应用代码更改。当前仓库由 `global.json` 指定 SDK。

仓库的 `Directory.Build.targets` 将 `AdditionalFiles` 加入监视列表。仓库外的应用项目需要自行包含模板并配置监视：

```xml
<ItemGroup>
  <AdditionalFiles Include="**/*.bui" />
  <Watch Include="@(AdditionalFiles->'%(FullPath)')" />
</ItemGroup>
```

同时按普通 BubuUI 应用的方式引用运行库和源生成器。仅添加 Watch 不会安装源生成器。

## Visual Studio 项目能力

BubuUI 应用不要声明 `ProjectCapability Include="Maui"` 或人为补充
`MultiPlatformSupport`，也不要通过
`_KeepLaunchProfiles` 强制 Android 保留桌面启动配置。MAUI 能力不仅影响设备菜单，
还会启用 VS 的 `MauiHotReloadSessionManager`。在没有 MAUI/XAML 诊断宿主的
BubuUI 应用中，这条通道可能在 `ApplyXamlChangesAsync` 抛出空引用，并丢弃整批更新。

VS 18.12 的该处理器还匹配 `MultiPlatformSupport + MultiPlatformAndroidSupport`，
因此只删除 `Maui` 不足以隔离桌面目标。Gallery 不再人为添加这两条能力，
平台识别交给 SDK。Android 的设备选择及热重载仍需在真实 VS 会话中单独验证。

Gallery 是共享 UI 库；`Gallery.Desktop` 和 `Gallery.Android` 是独立入口。
在 VS 中将 `Gallery.Desktop` 设为启动项目，编辑共享 Gallery 项目的 `.bui` 文件即可。
桌面入口不引用 Android 项目，使用标准 .NET 调试通道；Android 入口使用 Android SDK 的调试通道。
如果 VS 曾加载旧的能力声明，停止调试后重新加载项目（必要时重新打开解决方案），
再启动新的调试会话。旧会话不能通过再次点击热重载切换通道。

## 行为与边界

- `.bui` 对应类型发生受支持的代码更新后，重新执行 `InitializeUi`，更新 Ref 字段，清理旧的模板资源、根事件订阅和自建子控件，再触发布局与绘制。
- 保留当前组件对象及其用户字段；不重复调用 `OnInitialized`。子控件会重新创建，因此输入内容、焦点、滚动位置等子控件内部状态不保证保留。需要保留的数据应放在组件字段或外部模型中，并在模板中重新应用。
- 初始化前由调用方加入的内容不属于本次模板重建的清理范围。初始化期间通过 `Track` 注册的资源会在重建时释放；其他资源在对象释放时清理。
- 重建异常写入 `Trace`，暂停该对象的渲染更新；下一次匹配的代码更新可以重试。重建不是事务，失败时不恢复旧视图。
- 类型匹配支持基类、嵌套生成类型和泛型定义。独立工具类的修改不会自动重建所有引用它的视图。
- `@style` 和 `.bss` 的静态注册表刷新尚未支持，请重启应用应用样式修改。字段初始化器、构造函数、应用启动配置和不受 .NET 支持的结构修改也可能需要重启。
- NativeAOT 发布不在此功能范围内。Android 和各 IDE 的实际调试链路需分别验证；当前端到端验证使用桌面 .NET 的 `dotnet watch`。

## 纯 C# 控件

默认 `ReloadUi` 只请求更新，因此在 `Render` 中计算的 UI 可执行更新后的逻辑。如果控件在 `InitializeUi` 中构建子树，可显式启用重建：

```csharp
protected override void ReloadUi() => RebuildUi();
```

将可重建的界面创建放入 `InitializeUi`，通过 `Track` 管理相关订阅与资源。不要在该方法中放置只能执行一次的业务副作用。
