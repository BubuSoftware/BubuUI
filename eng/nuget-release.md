# NuGet 发布

工作流：`.github/workflows/nuget-release.yml`（**Publish NuGet packages**）。

## 首次配置

1. 在 GitHub 仓库创建名为 `nuget` 的 Environment。在 Deployment branches and tags 中选择 Selected branches and tags，添加 Branch 规则 `main`；如果也要从版本 tag 手动运行，再添加 Tag 规则 `v*`。已有配置若只允许 `v*`，需补上 `main`。
2. 在该 Environment 或仓库的 Actions secrets 中设置 `NUGET_USER`，值为创建信任发布策略的个人 NuGet.org 用户名（不是邮箱，也不是 API key）。发布组织的包时，这里仍填个人用户名，在 NuGet.org 策略的 Package owner 中选择组织，并保持该账号为组织的有效成员。
3. 在 NuGet.org 的 Trusted Publishing 中创建 GitHub 策略：

   | 字段 | 值 |
   |---|---|
   | Repository owner | `BubuSoftware` |
   | Repository | `BubuUI` |
   | Workflow file | `nuget-release.yml` |
   | Environment | `nuget` |

   选择有包发布权限的 owner，包范围需覆盖下列 `BubuUI.*` 包。新策略需在 NuGet.org 显示的激活期限内完成首次登录；过期时可重启激活窗口。

发布 job 通过 `id-token: write` 和 `NuGet/login@v1` 换取短期 API key，随后立即推送包，无需保存长期 API key。
配置字段见 [NuGet 官方信任发布文档](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)。

## 版本与触发

MinVer 配置位于根目录 `Directory.Build.props`，使用 `PrivateAssets="all"`，不会成为用户项目的 NuGet 依赖。
使用 `v` 前缀的 SemVer tag；默认版本基线由 `BubuDefaultVersion` 设置为 `0.0.1`，尚无版本 tag 时从 `0.0.1-alpha.0` 加提交高度开始。
MinVer 的最低版本选项仅支持主版本和次版本，因此 `Directory.Build.targets` 将其初始 `0.0.0` 基线调整为该默认值，同时保留预发布标识、提交高度和构建元数据。后续有效版本 tag 继续由 MinVer 推导。
未打 tag 的构建按 MinVer 规则产生带提交高度的预发布版本。CI 必须获取完整 Git 历史与 tags（`fetch-depth: 0`）。
VSIX 保留其独立的 Marketplace 版本。

工作流仅支持手动触发。在 GitHub **Actions → Publish NuGet packages → Run workflow** 中选择 `main` 后运行，会构建并发布该分支的 MinVer 版本。无需提前打 tag；尚无 tag 时发布的是 `0.0.1-alpha.0.<提交高度>` 预发布版本。

需要指定发布版本时，在要发布的提交上创建并推送 tag，例如：

```sh
git tag v0.0.1-preview.1
git push origin v0.0.1-preview.1

# 正式版使用 v0.0.1 等 tag
```

推送 tag 本身不会触发发布。可以使用 GitHub CLI 从该 tag 手动运行：

```sh
gh workflow run nuget-release.yml --ref v0.0.1-preview.1
```

工作流构建所有包；从 tag 运行时，还会逐一检查 MinVer 生成的包版本与 tag 一致，然后进入 `nuget` Environment 发布。
不要在同一提交上放置多个不同版本 tag。tag 不使用 `+build` 元数据；NuGet 包版本不包含该元数据。
`--skip-duplicate` 允许失败后重跑，已有版本不会被覆盖。修正已发布的包应新建版本和 tag。

手动运行会上传 artifacts 并发布到 NuGet.org。全部包推送成功后，工作流会创建或更新 GitHub Release，自动生成发布说明，并附上本次发布的 11 个 `.nupkg` 文件。
Release 使用包内实际版本，名称为 `BubuUI <版本>`，tag 为 `v<版本>`；包含预发布后缀的版本会标记为 Pre-release。
从分支运行且该 tag 不存在时，会在本次构建的提交（`github.sha`）上自动创建 tag，而不是分支运行结束时的最新提交。此 tag 也会参与后续 MinVer 版本推导。
发布 job 使用 `contents: write` 的内置 `GITHUB_TOKEN` 创建 Release，无需额外设置 GitHub PAT。NuGet 推送失败时不会执行 Release 步骤；Release 步骤失败后可重跑，已有 NuGet 版本会跳过，同版本 Release 会更新附件。
如只需检查打包结果，请使用下方的本地打包命令。

## 包范围

| 构建组 | 包 |
|---|---|
| Desktop | `BubuUI.Compiler.Generator`、`BubuUI.Runtime`、`BubuUI.Rendering`、`BubuUI.Text.Shaping`、`BubuUI.Rendering.Vello`、`BubuUI.Platform.Windows`、`BubuUI.Platform.Linux`、`BubuUI.Platform.MacOS`、`BubuUI.Themes.Fluent` |
| Android | `BubuUI.Platform.Android` |
| iOS | `BubuUI.Platform.iOS` |

桌面组先运行编译器测试。Android 使用 Android workload；iOS 在 macOS runner 上使用 iOS workload，runner 的 Xcode 必须与该 workload 兼容。
所有组成功后才发布共 11 个包。样例、测试、IDE 工具以及编译器内部的 Compiler / Metadata / Probe 项目不发布；这些编译器组件的源码已并入生成器。
新增公共包时，同时更新 `eng/pack-nuget.ps1` 的包列表、工作流的数量检查和本表。

源生成器 DLL 位于包的 `analyzers/dotnet/cs`，随包的 MSBuild targets 自动收集 `.bui` / `.bss`。
使用模板的应用需直接引用 `BubuUI.Compiler.Generator`（建议 `PrivateAssets="all"`），以及所需 Runtime、主题、平台包。

## 本地打包

使用 `global.json` 指定的 SDK。脚本在任何工作目录均可运行，输出目录相对仓库根目录：

```powershell
./eng/pack-nuget.ps1
./eng/pack-nuget.ps1 -Group Android
# macOS，并安装匹配的 iOS workload / Xcode
./eng/pack-nuget.ps1 -Group iOS
```

输出在 `out/nuget`。可用 `-OutputPath` 指定其他目录，或用 `-ExpectedVersion 0.0.1-preview.1` 检查某个 tag 对应的构建。
详细版本推导规则见 [MinVer 文档](https://github.com/adamralph/minver)。
