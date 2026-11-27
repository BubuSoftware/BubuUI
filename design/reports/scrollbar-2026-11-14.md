# ScrollViewer 滚动条交互修复 · 2026-11-14

## 问题与修复

原实现虽然命中条带宽 14 DIP，却用绘制滑块的 6 DIP 矩形判断是否开始拖动。点击滑块旁边的有效区域会误判为轨道翻页；轨道点击只翻一页，没有按住重复逻辑。绘制器与运行时还分别计算滑块几何。

- TryGetScrollBarGeometry 统一提供局部轨道与滑块范围，绘制、命中和拖动共用；横向滚动条在右下角重叠处按绘制顺序优先命中。
- 滑块沿滚动方向的范围对应整个 14 DIP 命中条带。支持在条带两侧按下拖动，并在拖出视口或窗口后继续拖动，偏移钳制到边界。
- 左键点击轨道立即翻一页；按住 400 ms 后每 60 ms 继续翻页。滑块到达鼠标位置时停止，鼠标移出轨道暂停、返回后继续。右键不启动滚动。
- 重复逻辑使用 IFrameAnimation / FrameAnimations，限制单帧追赶次数，不建立后台计时器。
- 松手、取消捕获、摘除和释放清理交互；无滚动范围、禁用或滚动条隐藏时停止推进。原滚动条手势仍消费释放，避免误点击外层 Button。
- InputRouter 增加取消入口，并拒绝向禁用、隐藏、释放或已离开本根的捕获目标路由移动/释放。TextBox 与 Slider 的拖动状态一起接入取消。
- Win32 WM_CAPTURECHANGED / WM_CANCELMODE 通知取消；正常鼠标释放先路由 PointerReleased，再 ReleaseCapture，避免取消通知抢先清掉按钮的有效点击。

## 验证

修复前先新增四项回归，横向/纵向宽命中区拖动及按住重复全部失败；修复后全部通过。

```powershell
dotnet build BubuUI.slnx --no-restore -v:q
dotnet test tests/BubuUI.Headless.Tests --no-build -v:q --logger "trx;LogFileName=scrollbar-final.trx"
dotnet test tests/BubuUI.Rendering.Tests --no-build -v:q --logger "trx;LogFileName=scrollbar-final.trx"
dotnet test tests/BubuUI.Compiler.Tests --no-build -v:q --filter "FullyQualifiedName~CatalogScannerTests.Surface_MatchesGoldenSnapshotFile"
dotnet run --project sandbox/ControlNativeCheck --no-restore
```

| 项目 | 通过 | 失败 / 跳过 |
|---|---:|---:|
| Headless.Tests | 648 | 0 / 0 |
| Rendering.Tests | 146 | 0 / 0 |
| 模板表面目录快照 | 1 | 0 / 0 |

构建 0 警告、0 错误。新增 17 项测试：14 项无头交互、3 项绘制。覆盖两个方向、嵌套偏移/内边距、拖出视口、按住重复与停在指针处、暂停/恢复、右键、捕获取消、禁用/隐藏、摘除/释放、无滚动范围和父按钮误点击。实际绘制出的滑块可命中并拖动；滚动后的 retained 合成与 CPU 整帧渲染哈希一致。像素 golden 未修改。

原生自检通过：95 帧，Vello GPU surface。真实 Win32 消息验证滑块边缘按下不翻页、拖出窗口到滚动边界、松手停止、轨道按住重复，以及直接调用系统 ReleaseCapture 后 WM_CAPTURECHANGED 立即清理运行时捕获并停止滚动。原有按钮点击、键盘焦点、弹窗恢复、中文/emoji 输入与 KeyUp 同时通过。

最终截图：out/scrollbar-native.png（498×422）。进度条包含动画，截图哈希不作为确定性基线。测试明细位于各测试项目的 TestResults/scrollbar-final.trx。
