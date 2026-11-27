# 根类型统一实施验证 · 2026-11-13

关联：[design/21](../21-root-type-unification.md)、K35。

## 验证结果

| 检查 | 结果 |
|---|---|
| `dotnet build BubuUI.slnx --no-restore -v quiet` | 0 警告、0 错误 |
| Compiler | 421/421 |
| Headless | 447/447 |
| IDE | 205/205 |
| Rendering | 107/108；1 项既有精确像素基线失败 |
| GalleryCheck CPU 交互 | 勾选后 showButton=True，点击后 count=1 |
| GalleryCheck 截图 | 1000×760、733 commands，hash FF0295A266B93621；已查看 |
| `git diff --check` | 通过 |

最终全量命令：

```powershell
dotnet test BubuUI.slnx --no-build -m:1 -v quiet --logger trx --results-directory F:/BubuUI/TestResults/unification-cleanup/final-v3
dotnet run --project sandbox/GalleryCheck/GalleryCheck.csproj --no-restore -- --cpu --selfcheck --shot F:/BubuUI/TestResults/unification-cleanup/gallery-final.png
```

旧层删除后最终串行全量共 1181 项，1180 项通过，进程因下面的既有 golden 失败返回非零。目录性能测试已放入独立的非并行测试集合，保留原有轮数及 40ms/100ms 门槛。初次迁移阶段为 1163/1164，通过数变化来自清理旧诊断测试及新增边界回归。

## 既有像素基线差异

失败：`BubuUI.Rendering.Tests.PixelGoldenTests.PixelGoldens_MatchBaseline` 的 `solid-primitives` 场景，期望 hash `FA65021E5F9A5DAD`，实际 `84BB45A4DF46D827`。语义探针、颜色采样和其余场景通过。

通过 `git archive HEAD` 将重构前相关源码导出至独立临时目录，重新 restore/build 后运行相同测试，得到相同失败和相同实际 hash。前后实际 PNG 的 SHA-256 完全相同：

```text
9029EE788269E67EE30FF20EF4CF476B9D567000552EC9D6A7616E29B5B58305
```

该场景直接构造 DisplayList 并经 VelloCpuBackend 绘制，不经过 UiObject、Element 或模板。本次未修改 Rendering/Vello 实现、golden、hash 或容差。底层差异原因仍未确定。

本机证据（位于 git 忽略的 TestResults 目录）：

- `TestResults/unification-cleanup/final-v3/*.trx`：清理后最终四个测试程序集结果。
- `TestResults/unification/evidence/golden-before/before.trx`：重构前源码复现结果。
- `TestResults/unification/evidence/pixel-golden-investigation.md`：隔离验证过程。
- `TestResults/unification-cleanup/gallery-final.png`：清理后的画廊交互截图。

## 范围说明

Component、WindowComponent、RootComponent、来源分类、窗口专用目录/生成分支和 IDE 独立成员树均已删除。生成代码、手写控件、宿主、测试与当前规范统一使用 UiObject/真实根实例。旧设计仅保留历史文档快照，不存在运行时兼容层。

语言服务最终发布文件已更新并核对 DLL 哈希；用户通过 Esc 停止最后一次界面自动操作，需重载 VS Code 窗口使语言服务重新连接。本报告不将文件发布等同于当前编辑器进程已经重连。
