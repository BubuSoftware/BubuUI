# 20 · 窗口根类型

> 2026-11-13，当前实现。窗口使用 [spec/02](../spec/02-generated-code-contract.md) 的通用根类型与 [spec/04](../spec/04-ui-type-metadata.md) 的内容能力规则。

```xml
@namespace Gallery
@using EclipseUI.Elements
<Window Title="Gallery" Width="1000" Height="760">
    <StackPanel><Button>OK</Button></StackPanel>
</Window>
```

文件MainWindow.ui生成MainWindow : Window，根属性写this，子内容赋this.Content。应用设置`MainWindow = new MainWindow()`；宿主自动初始化。读取Ref锚点前可以显式EnsureInitialized。

Window : UiObject，Element : UiObject。Window.Content为Element?，Panel.Children为Element集合；因此窗口类型无法进入这些内容位置，模板报告通用EUI060，C#由类型检查拒绝。自定义非Element宿主遵循同一规则。

Window没有模板根专用特性、保留标签解析、目录隐藏清单或专用生成基类。根标签查找、基类一致性、单内容基数和属性写入均复用一般类型规则。

平台生命周期为Window创建原生窗口和渲染表面。Title/Width/Height setter通过IWindowLiveUpdater推送，C#与ui动态表达式相同；无需生成器按属性名插入平台调用。

旧窗口包装方案与D117原始记录在[历史快照](./history/2026-11-13-before-legacy-removal/README.md)，不再是当前实现或兼容路径。
