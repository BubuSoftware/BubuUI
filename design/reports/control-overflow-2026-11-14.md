# 控件绘制与内容溢出修复

2026-11-14，Windows。当前契约见 [布局与渲染](../05-layout-rendering-input.md#控件绘制与内容区域)。

## 修复范围

TextBlock 修复之后进一步复现了按钮、复选框、单选框、开关、下拉、菜单、页签、树节点、日历、表格、内容控件及自定义 Render 的同类问题。新增回归在修复前有 24 项失败，覆盖自身绘制溢出、过大的子内容及浮层阴影范围。

- Element.GetDrawingClip 默认约束自身绘制，内置绘制与自定义 Render 使用同一作用域；平衡的作用域缓存进 retained 段。焦点装饰独立输出。
- GetContentClip 为绘制、命中及导航焦点提供相同的区域。按钮/边框扣除内边距，列表/树/菜单/表格/页签/复合控件限制普通子内容；普通布局面板继续使用 ClipToBounds 决定显式子项溢出。
- 按钮、下拉、菜单和排序表头使用有效标签区域；NumericUpDown 为微调按钮保留文本视口；页签头、树标题和日历日期单元格独立裁剪。
- Popup 的零尺寸锚点与真实内容区域分开处理，维持顶层显示和命中；背景、阴影、缓存位置/尺寸以及 damage 使用真实内容矩形，内容移位或缩放后正确刷新，关闭后清理旧阴影。
- 本次不改变文本换行模式，不增加省略号，不通过缩小显式子尺寸规避绘制溢出。

## 验证

`dotnet build EclipseUI.slnx --no-restore --nologo -v quiet`：0 警告、0 错误。

```powershell
dotnet test tests/EclipseUI.Rendering.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet test tests/EclipseUI.Headless.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet test tests/EclipseUI.Platform.Linux.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet run --project sandbox/ControlNativeCheck --no-build --no-restore
dotnet run --project sandbox/FluentCheck --no-build --no-restore -- out/control-overflow
```

| 测试项目 | 通过 | 跳过 |
|---|---:|---:|
| Rendering | 205 | 0 |
| Headless | 648 | 0 |
| Platform.Linux | 33 | 2 |
| 合计 | 886 | 2 |

2 项跳过要求 Linux 原生桌面。上述三个项目的结果保存在各自 TestResults/control-overflow_*.trx；本轮未重新运行编译器及 IDE 测试。

ControlOverflowTests 新增 40 项回归，包括 22 类内置/自定义控件的极小尺寸、内容越界与命中、普通布局面板的原有溢出语义、图标/微调区域、页签/树/日期单元格、零尺寸、焦点外圈及 Popup 位置/尺寸缓存与阴影 damage。TextBlockOverflowTests 的 7 项回归继续通过。像素测试使用独立的文本条带替身，保证裁剪断言不依赖安装字体。

Windows 原生输入、中文/emoji、焦点、浮层 Escape、滚动条捕获与 Vello GPU 上屏自检通过：`text=A你, KeyUp=3, frames=95, backend=Vello (GPU surface)`。

深浅色完整控件目录、浮层、焦点及指针状态共 12 张截图保存在 out/control-overflow；浅色展开下拉和深色完整目录已通过图像查看工具检查。未改写像素 golden。
