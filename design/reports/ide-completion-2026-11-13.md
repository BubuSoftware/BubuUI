# IDE 补全修复验证 · 2026-11-13

## 入口与结果

用户实测的三项问题：`@using` 没有命名空间补全、未导入类型提交后没有自动导入，以及
`override` 不列出可覆盖成员。

本次修复并验证了上述完整编辑流程，包括候选、LSP 主编辑/附加编辑、光标与接受后编译，
而不只比较候选名称。

| 检查 | 结果 |
|---|---|
| 解决方案构建 | 成功，0 警告、0 错误 |
| IDE 完整回归 | 280 / 280 通过，无跳过；原有 205 项，新增 75 项 |
| VS Code grammar / extension 接线 | 两组断言通过 |
| Rider 静态接线 | 12 / 12 通过 |
| Release 语言服务器发布 | 成功，已更新 ide/vscode/server |
| git diff --check | 通过 |

## 修复

- `@using` 从真实项目符号枚举命名空间，覆盖 System 和普通业务类型；修复空参数跨行、
  限定名前缀/中间位置替换、别名目标和不合法 global:: 命名空间前缀的规范化。
- 未导入控件和 C# 类型一起交付主编辑与 `@using` 附加编辑，保留 LF/CRLF 并去重。
  Roslyn 自身提供但不完成模板导入的同名候选转入明确导入路径，避免用户选到不完整编辑。
- 导入会导致现有简名类型歧义时，使用完整限定类型名；保持已有绑定与类型可访问性约束。
- override 候选及方法体继续由 Roslyn 提供；临时文档隔离生成 `#line` 和不完整声明，
  只提取真实覆盖成员，不回写临时分号、生成代码或行映射。支持生命周期方法、普通 virtual、
  泛型、ref/out 和属性，排除已覆盖、sealed/private/static 及生成器保留入口。
- 支持与不支持 snippet 的客户端均得到合法内容；支持时保留成员体内光标。
- 其他模板错误阻止生产生成时，仍通过共享根类型符号视图为有效 @code 提供编辑补全。
  不改生产快照、错误诊断或编译期短路行为；不虚构无法解析、非 UI、sealed/static 根。
- 修复项目编译资产与服务端 TPA 重复引入同名程序集造成的 CS0121；参见
  [引用上下文报告](./ide-project-reference-context-2026-11-13.md)。完整协议编译断言没有放宽。

## 验证命令

```powershell
dotnet build BubuUI.slnx --no-restore
dotnet test tests/BubuUI.Ide.Tests/BubuUI.Ide.Tests.csproj --no-build --logger "trx;LogFileName=ide-completion-full.trx" --results-directory TestResults/ide-completion/final
powershell -NoProfile -ExecutionPolicy Bypass -File ide/build.ps1 -SkipNpm
git diff --check
```

最终 TRX：`TestResults/ide-completion/final/ide-completion-full.trx`。

发布核对：Release 输出和 `ide/vscode/server/BubuUI.LanguageServer.dll` SHA256 一致；
随包 Runtime 与最新 Debug Runtime DLL 的 SHA256 一致，包含统一生命周期钩子。

## 使用与边界

已发布到仓库的扩展 server 目录。正在运行的旧进程需要重启语言服务器；VS Code 可执行
`Developer: Reload Window`。本次使用真实 LSP 会话、应用编辑后编译及薄壳接线测试，
没有操作用户正在使用的 IDE 窗口。

未保存 .cs 的客户端转发、新文件项目重新求值、通用 C# CodeFix/重构和完整 QuickInfo 等仍是
其他工作；本次结果不代表完整 C# IDE 能力已经全部接通。用户新增的 Counter.cs 未被改动。
