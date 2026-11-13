# 统一生命周期验证 · 2026-11-13

实现说明：[design/22](../22-ui-object-lifecycle.md)。规范：[spec/03 §5](../../spec/03-reactivity-and-binding.md#5-生命周期与所有权)。

## 结果

| 检查 | 结果 |
|---|---|
| 完整解决方案构建 | 成功，0 警告、0 错误 |
| Headless | 496 / 496 通过 |
| Compiler | 421 / 421 通过 |
| IDE | 205 / 205 通过 |
| Rendering | 107 / 108 通过 |
| 合计 | 1229 / 1230 通过，无跳过 |

Headless 相比变更前的 447 项新增 49 项，涵盖 ui / C# / Window 共用钩子、状态先于通知、父子通知顺序、移除重挂、资源逆序释放、更新中移动宿主、初始化/更新重入、通知异常、回调增删/自释放、双宿主拒绝、线程守卫、根内容替换、输入清理、同步上下文嵌套释放、动态块复用与虚拟化条目清理。

## 命令与产物

```powershell
dotnet build EclipseUI.slnx --no-restore
dotnet test EclipseUI.slnx --logger trx --results-directory TestResults/lifecycle/full
dotnet test tests/EclipseUI.Headless.Tests/EclipseUI.Headless.Tests.csproj --no-build --logger "trx;LogFileName=headless-final.trx" --results-directory TestResults/lifecycle/final
dotnet test tests/EclipseUI.Rendering.Tests/EclipseUI.Rendering.Tests.csproj --no-build --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type mini --logger "console;verbosity=normal" --logger "trx;LogFileName=rendering-final.trx" --results-directory TestResults/lifecycle/final
git diff --check
```

Compiler 与 IDE 结果取完整解决方案测试的 TRX；修复临时布局工具的宿主释放后，Headless 与 Rendering 以 final 目录最新结果为准。全量测试中曾因这些工具重复挂接产生 7 项失败，均已通过修正调用方的宿主释放解决，没有放松双宿主约束或改动绘制断言。

一次单独渲染复跑停滞约 90 秒后被取消；随后启用 60 秒停滞诊断，108 项在约 14 秒内全部完成，未触发停滞诊断或生成转储。未对没有确定根因的停滞修改产品代码。

## 唯一失败

`PixelGoldenTests.PixelGoldens_MatchBaseline` 的 `solid-primitives` 场景：

- 期望 hash：`FA65021E5F9A5DAD`
- 实际 hash：`84BB45A4DF46D827`
- 实际 PNG：`TestResults/PixelGolden-actual-solid-primitives.png`
- 实际 PNG SHA256：`9029EE788269E67EE30FF20EF4CF476B9D567000552EC9D6A7616E29B5B58305`

该 PNG 与[上次迁移报告](./root-type-unification-2026-11-13.md)中变更前独立构建的产物完全相同；这是已经复现的基线差异。本次未更新基线、容差或跳过测试。

## 范围

真实 ui 夹具与纯 C# 单内容对象在无头宿主中覆盖通用生命周期；Windows 宿主、桌面入口和清理路径已通过完整构建。本次未追加原生窗口的人工交互验收。
