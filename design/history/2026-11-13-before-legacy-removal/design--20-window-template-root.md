# 20 · 窗口模板设计（Window Template Root）

> **后续决策（2026-11-13）：** 本文 WindowComponent / CreateWindow 包装由 [21 · 根类型统一](./21-root-type-unification.md)（K35）替代。下文保留历史记录；新实现以根标签直接作为基类、内容成员类型检查为准。

> 状态：**P1+P2 落地（2026-11-13，D117；P2 当日随手落地）**——P1 起草并当日实施，
> P2（Gallery 迁移）同日完成：GalleryScene 手工树删除，UI 100% 模板化（MainWindow.bui + Section.bui），
> 渲染基线 hash 与迁移前逐字节一致；P3 仍为提案（未实施）
> 关联：D101（窗口 = 纯数据描述 + 平台材料化，`Window` 数据类）、design/14 §14.5（目录/标签解析）、
> design/15 §15.14（失效与调度）、spec/01 §1.1（单根冻结规则）、spec/01 §6.2（EUI060–063）、
> spec/02 §2（生成契约）、spec/02 §4.1（根发射）、spec/03 §5.3（组件不派生 Element）
> 代码基线：`src/BubuUI.Runtime/WindowComponent.cs`、`src/BubuUI.Runtime/UiAttributes.cs`、
> `src/BubuUI.Compiler/{UiComponentScanner,Analyzer,UiCatalogModel,Diagnostics}.cs`、
> `src/BubuUI.Compiler.Metadata/UiCatalogFactory.cs`、
> `src/BubuUI.Compiler.Generator/{TemplateGenerator,Emitter}.cs`、
> `src/BubuUI.Platform.Windows/{Win32WindowLiveUpdater,WindowsAppBuilderExtensions}.cs`

---

## 0. 一句话总纲

把「描述一个窗口」从应用代码里的三处手工样板（Mount + `Content`/`RootComponent` 同步 + `new Window{…}`）
收敛为 **`.bui` 根节点上的一对 `<Window>` 标签**：根标签决定生成类的基类（Avalonia 式「根 = 基类」，
落在 Component 家族内），窗口属性 = 基类 `[UiParameter]` 面，窗口模板退出可组合表面（不可被其他
ui 当组件使用），动态属性经既有失效循环回推到已材料化的窗口。

---

## 1. 背景与问题（现状事实）

D101 之后显示一个窗口的最短路径仍要三处样板（`samples/Gallery/GalleryApp.cs:14-44`）：

| # | 锁 | 代码位置 | 后果 |
|---|----|---------|------|
| L1 | 应用侧手工 `Mount()` 组件再取 `Root` | `GalleryApp.cs:22-23` | 组件生命周期细节泄漏给每个应用 |
| L2 | `Content`（Element）与 `RootComponent`（Component）必须手工保持同源 | `GalleryApp.cs:32-33` | 忘一个就是生命周期/失效队列断裂（运行期才炸） |
| L3 | 窗口参数（Title/尺寸/帧调节项）只能写在 C# | `Window.cs`（纯数据类，对模板目录不可见） | `.bui` 描述得了内容、描述不了窗口本身 |

窗口参数无法进模板的根因：`Window` 是 sealed 数据类，不是 `Element`/`Component`，目录扫描
（`UiCatalogFactory.OriginOf`）对它不可见；且「哪些属性允许在模板上写」没有任何声明面。

## 2. 目标与非目标

### 2.1 目标

| # | 目标 | 承载机制 |
|---|------|----------|
| G1 | `.bui` 根节点直接写 `<Window Title=… Width={W} …>` 声明窗口 | 保留根标签 + `WindowComponent` 基类（§4） |
| G2 | 应用侧收敛为一行：`MainWindow = new MainWindow { … }.CreateWindow()` | `CreateWindow()` 自动接线 Content/RootComponent（§5） |
| G3 | 根为 Window 的 ui 不能被其他 ui 当组件使用 | HiddenTag 通道（EUI060）+ 非根 `<Window>` 拦截（§6） |
| G4 | 窗口属性支持动态更新（Title/尺寸随失效循环回推已显示窗口） | 生成 U 推送 + `IWindowLiveUpdater` 平台回推通道（§7） |
| G5 | 「根 = 基类」是一般规则，第二个模板根类型（Dialog/Page…）按需启用 | `[UiTemplateRoot]` 标记（§4.2），P1 仅 WindowComponent |

### 2.2 非目标（明确不做）

- **控件根派生**（`class Counter : StackPanel`，合并 Component/Element 两层）：design/14 §14.5.3
  的来源判定、design/15 的组件级失效、spec/03 §5.3 均以其为前提——那是基座重写（框架 v2 级），
  若要探索单独立项 design/21。本设计的「根 = 基类」只发生在 **Component 家族内部**（安全且便宜：
  失效/参数/生命周期机制全部继承）。
- **`Window` 数据类改造**：保持 sealed 纯数据（D101）；手工数据路径、`AppBuilderTests`、offscreen
  快照零破坏。窗口模板是**新增**的描述生产方式，不替换既有路径。
- **多窗口**：运行时单窗口（`Application.MainWindow`），多个窗口模板文件共存合法但只有一个被材料化。
- **帧调节项动态化**：`MaxFramesPerSecond`/`AlwaysRender`/`IdleSleepMilliseconds`/`CloseOnEscape` 在
  模板上可写但为创建期一次性（`WindowHostOptions` 固化），不进 live 回推面（P3 视需要做可变 options）。

## 3. 设计总览

```
之前：Program.cs(AppBuilder) + Application 子类{ 手工 Mount + Content/RootComponent 同步 + new Window{…} } + GalleryScene.cs
之后：Program.cs(AppBuilder) + Application 子类{ MainWindow = new MainWindow { W = options.Width }.CreateWindow(); } + MainWindow.bui
```

```xml
@namespace Gallery
@using BubuUI.Elements

<Window Title="BubuUI Gallery" Width={W} Height={H}>
    <StackPanel Gap="8">…</StackPanel>
</Window>

@code {
    [UiParameter] public double W { get; set; }   <!-- 运行期值从应用侧传入（既有组件参数机制） -->
    [UiParameter] public double H { get; set; }
}
```

与 Avalonia 的两条有意差异（R 约束）：

- **R1**：Avalonia 的 `Window : ContentControl` 是控件本体，模板类派生它天经地义；BubuUI 的
  `Window` 是纯数据描述（D101），模板基类是 `WindowComponent : Component`，由 `CreateWindow()`
  产出 `Window` 描述——「类是控件」与「类产出描述」的分界即「派生必须留在 Component 家族内」。
- **R2**：spec/03 §5.3 组件不派生 Element，控件根（`<StackPanel>`）维持现有包装语义；
  「根 = 基类」只适用 `[UiTemplateRoot]` 标记的组件型根类型。

## 4. 发现与解析机制

### 4.1 保留根标签

根节点的 `Window` 标签名保留（同 `<Slot>`/`<ContentSlot>` 的保留伪标签先例）：根位置绕过
命名空间可见性（模板无需 `@using BubuUI`），属性按 `catalog.WindowRoot` def 的
`Parameters`（= `WindowComponent` 的 `[UiParameter]` 面）解析——EUI002/EUI003/EUI023 与
`Suggest` 建议全部复用。非根位置 `<Window>` 在解析前拦截 → **EUI060**（定向消息优于
「unknown tag」）。

窗口属性面（P1）：`Title`/`Width`/`Height`/`Theme`/`MaxFramesPerSecond`/`AlwaysRender`/
`CloseOnEscape`/`IdleSleepMilliseconds`，镜像 `Window` 数据类。`Theme` 无字面量转换器
（仅 `{表达式}` 形式）；`Content`/`RootComponent` 是结构位不进属性面；`MaxTicks` 是调试量不进。

### 4.2 `[UiTemplateRoot]` 标记（`Runtime/UiAttributes.cs`）

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UiTemplateRootAttribute : Attribute { }
```

标注的 `Component` 派生类可作模板根；**标记类型的任何子类同为模板根类型**并退出可组合表面。
目录侧两个进料口都拦截（`UiCatalogFactory`）：

- CLR 类型（基链含标记类型，或同名 `.bui` 声明 `IsWindowRoot`）→ 不进 `Elements`，注册
  `HiddenTag(ReasonCode = EUI060)`——使用点走既有 Unavailable 通道（`Analyzer.cs` 的
  `resolution.Hidden!.ReasonCode`）拿定向诊断，LSP 补全自动过滤；
- 纯 `.bui` 窗口文件（无 code-behind）→ `AppendTemplateOnlyComponents` 跳过合成，同样 HiddenTag。

`UiCatalog` 新增独立槽位 `WindowRoot`（`UiElementDef?`）：由**既有组件 CLR 扫描**产出
（`Describe(WindowComponent)`，[UiParameter] 面自动进 `Parameters`），不进 `_byTag`——它永远
不能按普通标签解析，只在窗口分支作属性解析依据。未引用 Runtime 的编译为 null（根 `<Window>`
回落 EUI001）。

### 4.3 声明标志（`UiComponentScanner`）

`FromFile` 已全量 `FrontEnd.Parse`——根标签 `Tag == "Window"` 时置位
`UiComponentDeclaration.IsWindowRoot`（进值相等：根从控件改为 Window 是对外表面的实质变化；
正文其余编辑仍不破坏增量缓存命中）。P1 单模板根类型，扫描器按字面 `"Window"` 判定（注释注明
第二个标记类型出现时一般化）。

## 5. 生成契约（`Emitter`）

```csharp
public partial class Shell : global::BubuUI.WindowComponent   // ← 类基切换（唯一硬编码分支）
{
    protected override global::BubuUI.Element Build()
    {
        this.Title = "BubuUI Gallery";                  // 字面量：build-once
        this.Width = (global::System.Double)(W);           // 表达式：一次性求值
        var el0 = new global::BubuUI.Elements.StackPanel(); …
        return el0;                                        // 内容 = Window 的恰一子元素
    }
    protected override void Render() { U0(); … }           // 动态属性推送 + 内容更新同列
}
```

- 窗口内容 = `<Window>` 的**恰好一个子元素**（EUI061：0/多个/文本/控制流块/槽/指令均报）；
  该子元素按模板根继续访问——组件根亦可，生成侧与普通组件根同语义（Track/Mount/Root）。
- `Class`/`Style`/`Key`/`Ref`/附加属性/`@bind-`/旧语法在 `<Window>` 根上 → **EUI062**
  （窗口没有自己的根元素可承接，组件根的元素级属性语义不适用）。
- `WindowComponent.CreateWindow()`：幂等 `Mount()` → 组装 `new Window { …, Content = Root,
  RootComponent = this }` → 重复调用抛 `InvalidOperationException`（一个模板实例只描述一个窗口）。

## 6. 诊断（spec/01 §6.2 新增四码）

| 码 | 触发 | 出口 |
|----|------|------|
| EUI060 | 窗口模板/模板根类型被用作组件标签；非根位置 `<Window>` | HiddenTag 通道 + VisitElement 前置拦截 |
| EUI061 | 窗口内容不是恰好一个子元素 | 窗口分支内容校验 |
| EUI062 | `<Window>` 根上的 Class/Style/Key/Ref/附加属性/@bind | 窗口分支属性校验 |
| EUI063 | code-behind 基类与窗口模板不一致（窗口文件基类非 WindowComponent；普通文件派生 WindowComponent） | `ProbeCodeBehindConflicts` 分流 |

code-behind 口径：窗口文件基类省略（生成侧声明 `: WindowComponent`）或显式写
`: WindowComponent` 均合法；`: Component` → EUI063。

## 7. 动态回推通道（live 面 = Title / Width / Height）

生成形态（与组件参数推送 spec/02 §4.3.3 同构，守卫基准 = 基类属性本身，无额外缓存字段）：

```csharp
private void U0()
{
    var v = (global::System.String)(TitleText);
    if (!global::System.Collections.Generic.EqualityComparer<global::System.String>.Default.Equals(this.Title, v))
    {
        this.Title = v;
        PushWindowTitle(this.Title);            // Width/Height: PushWindowClientSize(this.Width, this.Height)
    }
}
```

- Runtime：`IWindowLiveUpdater { SetTitle(string); SetClientSize(double, double); }`；
  `WindowComponent` 持 internal 安装槽（Runtime csproj IVT → Platform.Windows）+
  `protected PushWindowTitle/PushWindowClientSize` 助手（未安装 = no-op，无头/材料化前安全）。
- 平台：`Win32WindowLiveUpdater` 包装既有 `Win32Window.SetTitle/SetClientSize`（IsClosed 守卫，
  零新平台机制）；`ClassicDesktopLifetime.Run` 在 `Win32Window` 创建后对
  `RootComponent is WindowComponent` 安装。推送经既有失效循环（ProcessInvalidations → Render）
  自然触发，**WindowHost 零改动**。
- 窗口描述是创建期一次性数据：`CreateWindow` 之后描述不再随失效变化（D101），live 值经回推通道
  落到实际窗口——无头环境下 `shell.Title`（组件）与 `window.Title`（描述）的可观察差异即此边界。
- Width/Height 各自守卫但统一 `PushWindowClientSize(Width, Height)`：读当前属性成对下发，
  两路推送不会发出不一致的尺寸。

## 8. 增量缓存与 IDE 影响

- `UiComponentDeclaration` + `IsWindowRoot`（手写值相等同步）；`UiCatalog` + `WindowRoot`
  （Equals/GetHash 同步）——目录值比较口径不变。
- LSP（`ide/BubuUI.LanguageServer`）复用同一 `FrontEnd`/`UiCatalogFactory`：窗口诊断、
  HiddenTag 过滤与 `dotnet build` 同源零漂移；补全过滤继承既有 HiddenTags 行为。
- 探针：窗口属性表达式走既有 `ValidateExpressionValue`（ParamDef 已 stamp），EUI049 免费。

## 9. 测试与验收（P1 实测）

- `tests/BubuUI.Compiler.Tests/WindowTemplateTests.cs` 17 例：生成契约（基类/赋值/推送/
  无缓存字段/静态模板无 Render）、EUI060×2 / EUI061×3 / EUI062 / EUI002 建议 / EUI023 /
  EUI063×3（含合法 `: WindowComponent`）、目录装配（WindowRoot 参数面 + HiddenTag）+ 值相等。
- `tests/BubuUI.Headless.Tests/WindowTemplateTests.cs` 6 例 + `Fixtures/ShellWindow.bui`：
  CreateWindow 接线、one-shot 守卫、无头布局、live 推送（组件属性更新 + 描述一次性 + 批内取最新值）、
  AppBuilder 一行装配。
- golden：`BuiltInSurface.golden.txt` +1 行（`[hidden] WindowComponent … EUI060`，预期表面变化）。
- 全量回归：1110 → **1133 绿**（409/415/201/108，四项目 0 红、零新增警告）。
- P2 验收：`--cpu` 离屏快照 hash 与迁移前基线**逐字节一致**（74E18D1331BE373D，728 命令）；
  GalleryCheck `--cpu` 自检（checkbox → showButton、+1 → count）通过。

## 10. 落地切分

| 阶段 | 内容 | 状态 |
|------|------|------|
| P1 | 保留根标签 + WindowComponent + CreateWindow + HiddenTag/EUI060–063 + live 回推（Title/尺寸）+ 编译器/无头测试 + spec 同步 | ✅ 2026-11-13（D117） |
| P2 | Gallery 迁移：`GalleryScene.cs` 删除，新增 `MainWindow.bui`（窗口模板全页 + Ref 锚点）与 `Section.bui`（`[UiSlot]`/`<ContentSlot>` 卡片），种子数据与 Add() 收进 `TaskList.bui`；GalleryApp 收敛为一行装配 + `WireInteractions`（树内不表达的应用关切：主题切换/注册伪类掩码/CLR 事件桥/条目工厂/IsEnabled）；GalleryCheck 验证壳改用 MainWindow；离屏渲染 hash 与迁移前一致（74E18D1331BE373D）。LSP hover/补全展示；第二个模板根类型按需 | ✅（2026-11-13） |
| P3 | 帧调节项动态化（WindowHostOptions 可变）；多窗口前置；（独立立项）控件根派生探索 | 提案 |

## 11. 前提与风险

- **前提**：D101（窗口 = 纯数据描述）与 spec/03 §5.3（组件不派生 Element）维持不变——本设计
  是二者之上的增量，不是修订。
- R1（语义）：动态窗口属性的求值时机 = Render；表达式读的是失效时的最新状态，不快照入队值
  （测试 `LivePropertyPush_AppliesLatestValuePerBatch` 钉住）。
- R2（兼容）：`Window` 标签名保留只作用于**根位置**；用户自有 `Window` 组件在非根位置不受影响，
  但不能再作为模板根（spec/01 §1.1 记录）。
- R3（边界）：live 面仅 Title/尺寸；`Theme`/帧调节项在模板上修改只影响下一次材料化
  （对单窗口生命周期即「不生效」）——文档与诊断消息均已指明表达式形式。

## 12. 规范影响（实施时同步）

| 文档 | 节 | 变更 |
|------|----|------|
| spec/01 | §1.1 冻结规则 | 根 = Window（保留模板根标签）合法；不可组合 |
| spec/01 | §3.1 / §6.2 | `<Window>` 保留伪标签；EUI060–063 表行 |
| spec/02 | §2 | 生成契约：窗口文件基类 = WindowComponent、ConfigureWindow→（P1 直接 this 赋值） |
| spec/02 | §4.1 | 根发射窗口分支（Build = 内容子元素；属性赋值；U 推送） |
| spec/02 | §12 台账 | D117 行 |
| design/README | 导航表 | design/20 行 |

## 13. 开放问题（实施前拍板，均已定）

- ~~基类名~~：`WindowComponent`（`Window` 名字让给 D101 数据类；避免与 design/14 控件模板撞词）。
- ~~守卫基准~~：基类属性本身（省缓存字段；值类型字段与 `= null!` 初始化器冲突随之消失）。
- ~~live 面~~：Title/Width/Height（`Win32Window` 已有 SetTitle/SetClientSize，零平台新机制）；
  帧调节项 P3。
