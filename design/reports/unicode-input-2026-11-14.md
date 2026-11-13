# 输入框中文与 emoji 显示修复 · 2026-11-14

## 原因与修复

输入链路已把中文和 emoji 写入 Text，但 HarfBuzzTextPainter 只使用单一 Segoe UI 字体。该字体缺少相应字形时，shaping 返回 glyph 0，绘制不能自动选择另一字体。此前原生自检仅核对 Text 内容，遗漏了实际字形覆盖。

本次在共享文本绘制管线加入逐字素字体回退：

- Windows 使用微软雅黑补齐中文，Segoe UI Emoji 提供彩色 emoji；符号等继续按配置链查找。
- 字素簇不跨字体拆分，覆盖代理对、ZWJ、肤色修饰、键帽及变体选择符。相邻同字体内容合并 shaping，主字体内的西文 kerning/连字保持原有结果。
- 缓存复用字体分段、进距与共同基线；测量与绘制使用相同产物。TextBox 根据实际字体调整行高、光标、占位和组合文字，避免回退字形被裁掉；占位文字变更也会重新测量。
- AppBuilder 和直接创建 painter 都默认启用系统回退；可通过 FallbackFontPaths 指定路径或传空列表关闭。字体按需加载，painter 释放自己创建的回退字体，保留调用方拥有的主字体。
- 原生自检增加 DrawGlyphs 的字体及非零字形检查，离屏自检在撤销前保存真正包含中文与 emoji 的输入截图。

## 回归与复现

修复前新增测试 8 项中 7 项失败，明确复现缺字与 emoji 单字体问题；西文保真测试通过。

新增测试最终共 16 项：FontFallbackTests 11 项、UnicodeTextBoxTests 5 项，覆盖默认/指定/禁用回退、中文与 emoji 实际像素、彩色输出、混合字体基线与进距、组合 emoji、文本/彩色变体共存、缓存与资源、输入框光标/高度、占位/组合文字及 AppBuilder 装配。

```powershell
dotnet build EclipseUI.slnx --no-restore -v:q
dotnet test tests/EclipseUI.Rendering.Tests --no-build -v:q --logger "trx;LogFileName=unicode-rendering-final.trx"
dotnet test tests/EclipseUI.Headless.Tests --no-build -v:q --logger "trx;LogFileName=unicode-headless-final.trx"
dotnet run --project sandbox/ControlCheck --no-restore -- out/controls.png
dotnet run --project sandbox/ControlNativeCheck --no-restore
```

结果：整仓构建 0 警告、0 错误；渲染测试 135 项、无头测试 615 项通过，合计 750 项。编译器和 IDE 实现未改动。

离屏示例在真实 ControlCatalog.ui 输入框中输入 `Unicode + 中文 😀 👩‍💻 👍🏽 ❤️ 🎉`，断言使用中文/emoji 字体，并生成：

- `out/unicode-input.png`：深色主题，900×220。
- `out/unicode-input-light.png`：浅色主题，900×220。
- `out/unicode-native.png`：原生消息输入的 `A你😀`，498×272。

已目检截图中文字、彩色 emoji、组合字形和光标均可见。原生自检经消息队列点击聚焦后输入，避免窗口启动焦点变化影响测试；验证字形后 Backspace 删除完整 emoji，保持 KeyUp 与 Escape 顺序验证，12 帧 Vello GPU surface 自检通过。

字体回退依赖安装字体或配置的文件；未安装任何支持某字符的字体时仍显示缺字字形。完整 bidi、自动字体发现和真实输入法候选窗口人工验收不在本次修复中。
