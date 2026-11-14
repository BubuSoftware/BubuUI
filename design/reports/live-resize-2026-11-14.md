# Windows 实时缩放验证

2026-11-14，Windows。修复期间始终保持 UI 逻辑、布局和渲染在同一线程。

## 原因与修改

Windows 拖动标题栏或尺寸边框时进入[内部移动/缩放消息循环](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove)。旧实现只在外层 PumpMessages 返回后布局与 Present；WM_SIZE 处理器只调整 GPU 表面，因此拖动期间缺少新内容帧。

WindowHost 现将尺寸、重绘和原生缩放定时器接到完整帧流程，实时处理续体、状态更新、动画、Measure/Arrange、表面尺寸及 Present。尺寸采用最新客户区物理像素和 DPI 换算后的逻辑尺寸。DXGI 对消息/呈现的线程要求参考[微软最佳实践](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/dxgi-best-practices)。

Win32Window 的 IsInSizeMove、LiveResizeFrameRequested 表达内部循环状态和帧机会。定时器间隔 16 ms，仅在移动/缩放时存在；退出、销毁和 Dispose 都会清理。无变化的定时帧继续跳过 GPU 上屏。

重入时只标记待处理尺寸；表面纹理释放后合并最新尺寸，防止 Resize 或 Render 递归。原生帧异常保存为 ExceptionDispatchInfo，返回外层托管循环后重新抛出并清理 UI 上下文及挂载关系。

## 构建与自动化回归

```powershell
dotnet build EclipseUI.slnx --no-restore --nologo -v quiet
dotnet test tests/EclipseUI.Platform.Windows.Tests --no-restore
dotnet test tests/EclipseUI.Headless.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet run --project sandbox/ControlNativeCheck --no-build --no-restore
dotnet run --project sandbox/ResizeNativeCheck --no-build --no-restore
```

构建 0 警告、0 错误。Windows 新增 4 项原生窗口回归全部通过，无头测试 648 项通过。本轮未重新运行其余测试项目。

Windows 测试使用真实 HWND 与验证呈现器，覆盖：

- 原生尺寸消息返回前已重新布局和呈现；换行数量变化，固定按钮宽度与字号保持不变。
- 外层帧循环暂停时，真实 Win32 定时器仍处理 UI Post 和动画。
- Render 中同步触发的尺寸变化会在退出后处理，无递归渲染或持有纹理期间 Resize。
- 原生帧错误返回托管循环，保留错误信息并恢复同步上下文、释放挂载关系。

测试在非 Windows 系统显式跳过；每个用例清理自己的 WM_QUIT，防止前一个原生窗口生命周期终止下一个用例。

## 真实内部循环与 GPU

ResizeNativeCheck 通过 WM_SYSCOMMAND/SC_SIZE 进入真实 DefWindowProc 尺寸循环。整个三次调整期间，外层 host.Ticks 始终为 1；在该内部循环的定时器回调中改变尺寸，逐次确认尺寸消息返回前已经重新布局和 Vello GPU Present。

| 客户区宽度（像素） | 按钮行数 | 文本行数 | 外层 tick | GPU 已呈现帧数 |
|---|---:|---:|---:|---:|
| 320 | 8 | 6 | 1 | 2 |
| 640 | 4 | 3 | 1 | 4 |
| 420 | 6 | 5 | 1 | 6 |

按钮宽度保持 96 DIP，标题字号保持 18 DIP。使用 Escape 正常结束内部循环，然后关闭验证窗口。backend 为 Vello (GPU surface)。截图保存在 out/live-resize-320.png、out/live-resize-640.png、out/live-resize-420.png；320 与 640 两张已经通过图像查看工具确认，内容按宽度重排，文字和按钮保持同一尺寸。

既有 Windows 原生输入、中文/emoji、焦点、滚动条与 GPU 自检继续通过：text=A你、KeyUp=3、frames=95。
