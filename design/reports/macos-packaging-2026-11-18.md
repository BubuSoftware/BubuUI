# macOS 应用包验证（2026-11-18）

环境：Apple Silicon arm64、macOS 26.6.2、.NET SDK 11.0.100-rc.2.26504.105，应用目标 net10.0 / osx-arm64。使用 Xcode Command Line Tools 和本机 Homebrew SDL3 / HarfBuzz 作为打包输入。

## 已执行

```bash
python3 eng/test_macos_bundle.py
dotnet publish samples/Gallery.Desktop/Gallery.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o out/framework-audit/macos-publish
eng/package-macos.sh --publish-dir out/framework-audit/macos-publish --output out/framework-audit/Gallery.app --rid osx-arm64
eng/verify-macos-package.sh out/framework-audit/Gallery.app osx-arm64
out/framework-audit/Gallery.app/Contents/MacOS/Gallery.Desktop --cpu --shot out/framework-audit/gallery-packaged-cpu.png
```

- 打包回归 14 项通过。原生 fixture 编译 executable → middle dylib → base dylib，打包后移走全部原始 dylib，包内启动器仍能正常运行。另覆盖错误架构、绝对路径、未解析 `@rpath`、逃逸/缺失依赖、坏符号链接、签名依赖顺序和失败替换保留旧包。
- Release 自包含发布成功。发布过程中出现 3 条既有 IL2075 裁剪警告：`DataGridColumn.GetValue` / `TrySetValue` 使用对象运行时类型反射属性，无法静态保证属性元数据保留；该次验证不能证明任意用户 DataGrid 行类型在裁剪后仍可反射。SDK 另提示使用预览版本。
- `.app` 构建成功，29 个 Mach-O 均通过 arm64 校验，所有非系统链接均指向包内文件；`codesign --verify --deep --strict` 通过。
- 外部依赖收集为 10 个 dylib：SDL3、HarfBuzz、FreeType、GLib、Graphite2、gettext intl、PCRE2、libpng、Brotli decoder/common。完整依赖保存在包内 `Contents/Resources/native-dependencies.json`。
- 验证脚本清除开发用 DYLD 库路径后，通过标准 `Contents/MacOS/Gallery.Desktop` 启动器运行 Gallery。SDL3 Cocoa 窗口、Vello Metal / Bgra8Unorm / Fifo 创建成功，5 个更新 tick 完成并正常退出；该次日志记录 7 次呈现、Retina scale=2。
- 同一包生成 CPU 截图成功：720×960、279 commands、hash `4DEE8BE1AC4F9331`。已查看截图，控件文本、边框和状态正常。输出位于 `out/framework-audit/gallery-packaged-cpu.png`。

本地日志位于 `out/framework-audit/macos-packaging-tests.log`、`macos-release-publish.log`、`macos-package-final.log`、`macos-package-verify.log`、`macos-package-cpu.log`。这些生成物不提交 Git。

## 尚未执行的外部验收

本次只使用 ad hoc 本地签名，没有使用 Developer ID 证书，没有上传公证或验证 Gatekeeper 分发路径。Intel 真机运行、另一台未安装开发依赖的干净机器、真实输入法候选窗口/死键交互和第二显示器跨屏缩放，仍需对应设备验收。依赖 fixture 移走原始库后的运行证明重定位有效，但不能代替这些真机验收。

CI 已增加同样的打包测试、严格签名验证、包内 GPU 启动、CPU 截图与 `.app` 压缩包归档；本次未触发或观察远端工作流执行。
