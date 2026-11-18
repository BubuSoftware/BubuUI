# macOS emoji 间距（2026-11-18）

连续 Apple 彩色 emoji 仍会紧贴。原 HarfBuzz 进距在 14 / 24 号下约为 13.984 / 23.984，而本机 CoreText 分别为 19 / 25；14 号还使用 17.5 的光学绘制尺寸。仅修复 Retina 字形位置变换不能解决该度量差异。设置 HarfBuzz ptem、ppem 及 OpenType 函数的 56 组实验未改变结果。

修复在整形结果中统一应用 CoreText 的进距、光学尺寸与行盒，保留组合 emoji、文本簇及零进距的连接符/变体选择符。光栅化、布局测量、换行和编辑光标由同一结果驱动。普通字体和其他平台保持原 HarfBuzz 路径。

原生度量按 PostScript 名读取，并核验字体文件 URL 和 collection face，避免代入另一份同名字体；字体句柄缓存上限 16，随 shaper 释放。AppKit 在任何 CoreText 字体创建前加载，框架库句柄保留到进程结束，修复先创建字体再启动 Cocoa 窗口时的桥接初始化错误。

回归与独立 CoreText 对照覆盖 8、13.4、14、16、17.25、18、24、40 号，普通 emoji、职业/肤色/爱心/国旗/键帽序列，以及 1 / 1.25 / 1.5 / 2 倍缩放下的可见间隔、完整字素换行和 TextBox 光标。依赖全局 TextMeasurer 的测试独立串行执行，避免并行测试读取已经释放的临时 measurer。

测试及截图日志位于忽略目录 `out/emoji-spacing/`，独立系统字体与 HarfBuzz 诊断位于 `out/emoji-spacing-review/`。Vello 与 CoreText 的位图 strike 和边缘像素仍可能不同；本次保证系统度量和可见间距，不声称两条光栅化路径逐像素一致。既有图元 golden 基线没有修改。

最终本机验证：

- 整个解决方案构建通过，0 警告、0 错误。
- 绘制测试 297 / 298 通过，包含全部 36 个 emoji 用例；唯一失败仍为既有 `PixelGoldens_MatchBaseline`（solid-primitives 与 transform-clip），未更新基线。
- macOS 平台测试 36 / 36、Headless 测试 749 / 749 通过。
- 重新生成 Release `Gallery.app`，包结构、签名及 osx-arm64 校验通过；实际 Cocoa / Metal、Retina 2 倍启动完成 5 ticks、绘制 7 帧。Release 保留既有 3 条 DataGrid 反射裁剪警告。
- 独立像素检查：14 号在 2 倍缩放下字形宽 35、进距 38；24 号 🎉 宽 48、进距 50；40 号 😀 宽 75、进距 80，均存在可见间隔。
- `git diff --check` 通过。
