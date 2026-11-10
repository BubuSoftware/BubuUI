# EclipseUI IDE 支持（design/06 的落地）

这里放的是 design/06 里那份「**一份共享资产，两个薄壳**」：

```
ide/
  EclipseUI.LanguageServer/   LSP server（C#/.NET 10，stdio）——诊断/补全/悬停/大纲全在这里
  vscode/                     VS Code 壳：TextMate grammar + 语言配置 + 片段 + 极薄的 LSP 客户端
  build.ps1                   发布 server 到扩展目录 + 装依赖 + 跑 grammar 断言
```

Rider 走 lsp4ij 接**同一个** server（见 §4），所以补全/诊断的行为在两个 IDE 里天然一致，
差别只在壳的接线方式。

## 1. 能力（当前切片）

| 能力 | 状态 | 说明 |
|---|---|---|
| 语法高亮 `.ui` | ✅ | 标签/属性/`@bind-`/附加属性/`@if`/`@foreach`/`@switch`/`@using`/`@code`/`@style`/两类注释；`{expr}` 内嵌 C# 词法 |
| 语法高亮 `.eustyle` | ✅ | 类选择器、伪类、`$变量`、`theme()`、hex 色、`@states` |
| 诊断（红波浪线） | ✅ | 直接跑编译器前端（`FrontEnd.Parse` + `Analyze`），EUI001/EUI002/EUI003/EUI031… 与 `dotnet build` 同码同 span |
| 标签补全 | ✅ | 来自模板目录；未导入的命名空间会提示 `needs @using …` |
| 属性 / 事件 / 组件参数 / 附加属性补全 | ✅ | 含 `Grid.Row` 形态；事件带委托签名 |
| `@bind-` 补全 | ✅ | 只列可写目标（只读属性/事件/内容参数不出现） |
| 属性值补全 | ✅ | 枚举成员、`bool`、`Style=` 的可样式属性名 |
| `@` 指令补全 | ✅ | `@if/@foreach/@switch/@using/@code/@style`，带片段 |
| `@using` 命名空间补全 | ✅ | 目录里出现过的命名空间 |
| 悬停 | ✅ | 标签 → CLR 类型/内容模型；属性 → 类型；事件 → 处理器签名；枚举 → 成员列表 |
| 大纲 / 面包屑 | ✅ | 元素树 + 控制流块 + `@code`，容器范围覆盖到结束标签 |
| 表达式悬停 | ✅ | `{expr}` 内显示推断出的 C# 类型；方法组显示处理器签名（Roslyn 投影，design/06 §6.3） |
| 表达式内 C# 补全 | ✅ | 点号后列接收者成员；裸前缀列 `@foreach` 变量 + code-behind 成员；code-behind 取 `{Name}.cs` 兄弟文件（spec/02 §2.2），`@code` 块同样计入 |
| 跳转定义 | ✅ | `{expr}` 内标识符 → `@foreach` 变量落回本文档 foreach 头；code-behind 成员跳到兄弟 `.cs` 的声明名字（`@code` 成员暂不跳，见边界） |
| 重命名 | ⛔ 下一片 | 需 WorkspaceEdit 反扫多文件 |
| 语义着色 | ⛔ | TextMate 先兜住；语义 token 在投影上可接，未做 |
| `#line` 调试映射 | ⛔ | design/06 P3 |

## 2. VS Code：跑起来

```powershell
# 一次性：发布 server 到 ide/vscode/server + 装 npm 依赖 + 跑 grammar 断言
powershell -ExecutionPolicy Bypass -File ide/build.ps1
```

然后二选一：

- **调试**：VS Code 打开 `ide/vscode` 目录，按 `F5`（Run Extension）。
- **安装**：`code --install-extension` 需要一个 `.vsix`（`npm i -g @vscode/vsce && vsce package`），
  或者临时用 `code --extensionDevelopmentPath=<repo>/ide/vscode` 启动。

开发机不发布也能用：扩展会自己找到
`ide/EclipseUI.LanguageServer/bin/{Release,Debug}/net10.0/EclipseUI.LanguageServer.dll`。

## 3. server 怎么找到模板目录

目录**不手写**：和编译器同一条符号派生路径（`UiCatalogFactory.Build(Compilation)`，
design/14 §14.10.2）。manifest 的查找顺序：

1. `--runtime <path>`（扩展设置 `eclipseui.runtime.path`）
2. 环境变量 `ECLIPSEUI_RUNTIME`
3. server 自身目录下的 `EclipseUI.Runtime.dll`（发布版随包带）
4. 从 `--workspace` 向上找仓库布局 `src/EclipseUI.Runtime/bin/{Release,Debug}/net*/`

**你自己的组件**（`Component`/`Element` 派生）也能补全：runtime 同目录下**引用了
`EclipseUI.Runtime`** 的程序集会被自动扫描（PE 元数据的 AssemblyRef 判定，不加载程序集）。
也可以显式给：

```powershell
dotnet ide/EclipseUI.LanguageServer/bin/Debug/net10.0/EclipseUI.LanguageServer.dll `
  --stdio --runtime src/EclipseUI.Runtime/bin/Debug/net10.0 --assembly bin/Debug/net10.0/MyApp.dll
```

VS Code 侧对应 `eclipseui.catalog.assemblies`（字符串数组）。

> 找不到 runtime 时**不会**报"每个标签都未知"——语义检查直接跳过，只出语法诊断，
> 并用 `window/showMessage` 说明缺什么。这是有意的：配置问题不该伪装成代码问题。
> `EclipseUI: Reload Tag Catalog` 返回 0 tags 时也会弹 Warning。

### 3.1 没有红波浪线时怎么查

高亮来自 TextMate grammar，**不需要 server**；所以「有颜色但没有诊断/补全」通常说明 server 没连上
或目录为空。按顺序查：

1. 输出面板 → `EclipseUI` 通道，看 `catalog: N tags from <path>`；`N=0` 就是目录没找到。
2. 命令面板跑 `EclipseUI: Reload Tag Catalog`：0 tags 会弹 Warning，非 0 会显示数量。
3. 编辑器右下角语言模式必须是 `eclipseui`（不是 XML/HTML）。`documentSelector` 只匹配这个
   language id，id 不对时 server 收不到 `didOpen`，自然一条诊断都不会发。
4. 确认扩展用的是最新 server：改完服务端要重跑 `ide/build.ps1` 并 **Developer: Reload Window**。
   server 是常驻进程，重载前 VS Code 一直连着旧 dll；旧进程还会锁住 `ide/vscode/server/*.dll`，
   让 `dotnet publish` 报 MSB3026/3027「文件被 .NET Host 锁定」。
5. `build.ps1` 会把 `EclipseUI.Runtime.dll` / `EclipseUI.Rendering.dll` 一起放进 `server/`
   （server 只读元数据、不加载程序集，所以 publish 默认不带它们）。打包后的扩展因此不依赖
   「工作区恰好是本仓库」。

语义诊断走的是编译器同一个 `FrontEnd`，只要目录里有标签，删掉 `@using` 后 `<StackPanel>`
一定报 `EUI001`（K22，内置控件无豁免）。**零诊断 = 目录/连接问题，不是规则漏了。**

## 4. Rider（下一步，零 server 改动）

lsp4ij（Red Hat，EPL-2.0，2024.2+）支持所有 IntelliJ 系 IDE。两种接法：

**过渡（现在就能用）**：Settings → Languages & Frameworks → Language Servers → `+`
新建 User-defined server：

| 字段 | 值 |
|---|---|
| Name | EclipseUI |
| Command | `dotnet` |
| Arguments | `<repo>/ide/EclipseUI.LanguageServer/bin/Release/net10.0/EclipseUI.LanguageServer.dll --stdio --workspace <repo>` |
| Mappings | File name pattern `*.ui` → language id `eclipseui` |

语法高亮先靠 VS Code 那份 TextMate bundle（IntelliJ 平台原生支持导入 `.tmLanguage.json`）。

**正式（后续薄插件）**：插件只做两件事——注册 `.tmLanguage.json` bundle、用
`com.redhat.devtools.lsp4ij.server` 扩展点声明 stdio server。逻辑一行都不重复，
因为协议层就是这里的 `LanguageServerHost`。

## 5. 测试

```powershell
dotnet test tests/EclipseUI.Ide.Tests     # 84 例：光标上下文 / 补全 / 悬停 / 跳转定义 / 大纲 / 诊断 / 协议帧 / 探针投影 / 端到端会话
cd ide/vscode; node test/tokenize.test.js  # grammar：用真实的 oniguruma 引擎逐 token 断言 scope
```

grammar 测试值得单说：TextMate 写错不会报错，只会"某个地方忽然没颜色"。
`node test/tokenize.test.js --dump` 会把每行每个 token 的 scope 打出来，
包括"块定界符独占一行"这类曾经把整个文件吞掉的回归。

## 6. 已知边界

- **全文同步**（`TextDocumentSyncKind.Full`）：.ui 都是几 KB，全量重解析比维护增量编辑映射可靠。
- **表达式补全的边界**：投影用探针合成树走 Roslyn（design/06 §6.3），code-behind 取
  `{Name}.cs` 兄弟文件（spec/02 §2.2）——不放兄弟文件就只有 `@foreach` 变量与 `@code` 成员；
  找不到编译运行时（`--runtime`）时表达式分支整体优雅停用，返回空候选而非报错。
- **跳转定义的边界**：`@code` 成员的声明在合成树里、没有可去的真实文件，跳转对它返回
  null（不给错位置）；code-behind 跳转要求兄弟文件在磁盘上（内存态/ untitled 文档没有路径）。
- 目录刷新是手动的：`EclipseUI: Reload Tag Catalog` 命令（`eclipseui/reloadCatalog` 请求）。
  重新编译了 app 之后需要点一下。
- 组件模板里的 `<ContentSlot>` 解析退化为按短名匹配目录（无 code-behind 符号），
  与 `FrontEnd.Analyze` 的无符号语境一致。
- server 只认 `.ui`；`.eustyle` 目前只有高亮，没有诊断（样式表编译器还没接进 LSP）。
