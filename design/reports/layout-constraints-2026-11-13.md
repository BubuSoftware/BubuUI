# 布局约束实施验证 · 2026-11-13

关联：[design/05](../05-layout-rendering-input.md#布局)，落实 `1d46a88` 明确的测量、排列和滚动契约。

## 实施结果

- `Element.Measure` 校验输入与有限测量产物；扣 Margin、应用本地 Width/Height/Min/Max 后才进入控件测量，内部内容需求与受 available 限制的对外 `DesiredSize` 分开保存。
- 排列统一处理显式尺寸、Min/Max、Stretch 及其他对齐；Stretch 受限时居中。非 Stretch 不直接采用未约束内容需求；局部显式溢出不扩大祖先槽。
- 最终宽度改变时受控重测，文本断行、嵌套 StackPanel 与后续兄弟位置同步；自然尺寸已经容纳内容时保留原测量缓存。字体、文字和尺寸属性变化仍沿树失效。
- `ScrollViewer` 各轴独立按 Disabled/Auto/Hidden/Visible 构造有限或无穷约束；模式变化使测量失效。最终视口决定范围及有效偏移，偏移读取时钳制，不在排列中回写属性。无界空内容也保持有限结果。
- Grid 先解列宽，再重测单元格，最后解行高；排列使用最终列宽更新行高。同时修正轨道减少后的缓冲尾部参与求和、零权重星轨道残留旧值，以及接受无限轨道定义的问题。
- NoWrap 的内部 run 保留自然宽度，避免多个 run 的绘制位置因外层约束被错误压缩；TextBlock 向父级报告的尺寸仍受约束。
- 命中与显式裁剪一致，允许未裁剪祖先中的局部溢出；ScrollViewer 的绘制与命中均以扣 Padding 的内容视口为界。零尺寸裁剪仍保留。Visible 模式在无溢出及空内容时绘制满轨道滑块。

## 自动验证

| 检查 | 结果 |
|---|---|
| 解决方案构建 | 0 警告、0 错误 |
| Compiler | 421/421 |
| Headless | 539/539 |
| IDE | 280/280 |
| Rendering | 116/116，含精确像素 golden |
| `git diff --check` | 通过 |

按各程序集最后一次验证结果统计，共 1,356 项通过。本次增加 43 项无头用例及 8 项渲染用例，迁移旧契约下的超宽期望及对齐断言；没有更新像素基线。

```powershell
dotnet build EclipseUI.slnx --no-restore -v quiet
dotnet test EclipseUI.slnx --no-restore -m:1 -v quiet --logger trx --results-directory TestResults/layout-contract/final
dotnet test tests/EclipseUI.Rendering.Tests --no-restore -v quiet --logger trx --results-directory TestResults/layout-contract/final-rendering
```

全量验证后补齐 Visible 无溢出边界，并重跑整个 Rendering 程序集；最新结果在 `final-rendering`。其他程序集的结果在 `final`。测试覆盖有限约束、非法输入/产物、尺寸优先级、Margin、透明块、缩放与受控重测、字体和内容失效、滚动模式与有效偏移、无界父级、虚拟化范围及剪裁命中。

## Gallery 实测

使用本机 Segoe UI 字体和 HarfBuzz 布局/绘制、Vello CPU 渲染，连续调整宽度 `1000 → 360 → 700 → 360`，高度均为 760。使用原有模板及文案，启动时调用 `GalleryApp.WireInteractions` 装配虚拟列表工厂和交互。

| 视口宽 | 第三张 Section 宽 | BodySlot 宽 | 纵向 Extent | 横向 Scrollable |
|---|---|---|---|---|
| 1000 | 952 | 920 | 2374.83 | 0 |
| 360 | 312 | 280 | 2988.88 | 0 |
| 700 | 652 | 620 | 2390.80 | 0 |
| 再次 360 | 312 | 280 | 2988.88 | 0 |

所有五张 Section 都跟随内容宽度；第三张卡片标题正常换行，背景不随长按钮标签扩大。两次 360 宽截图的内容哈希相同：页面顶部 `134B29E558AAB145`，第三张卡片 `61282A70FD915D95`。截图已查看。滚动至目标后，通过 `InputRouter` 点击复选框和 `+1` 按钮，得到 `showButton=True`、`count=1`。

本机证据保存在 Git 忽略目录，未提交生成的图片或临时验证工程：

- [360 宽第三张卡片](../../TestResults/layout-contract/gallery-section3-360.png)
- [1000 宽第三张卡片](../../TestResults/layout-contract/gallery-section3-1000.png)
- [360 宽页面顶部](../../TestResults/layout-contract/gallery-360.png)
- `TestResults/layout-contract/final/*.trx`、`final-rendering/*.trx`
- `out/layout-contract-check/`：本机离屏缩放、截图与窄窗交互验证器，运行命令为 `dotnet run --project out/layout-contract-check -v quiet`。

## 范围边界

Button/CheckBox 标签仍采用已有单行绘制能力，局部文字绘制可以超出控件边界，由显式或祖先裁剪处理；本次保证这些内容需求不会扩大受约束卡片。变高虚拟化、容器池、标签换行或截断、滚动条拖拽属于后续能力。
