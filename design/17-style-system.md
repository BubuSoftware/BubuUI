# 17 · 样式系统完整设计（Styling System）

> 状态：**D98–D102 已落地 + E1–E5 提案**（2026-11-09）
> 关联：design/03（模板引擎，§3.3.9 `Class`/`Style` 保留）、design/09 §9.3 G4（样式/主题行）、
> design/04（Runtime 属性引擎）、design/05（绘制与段缓存）
> 台账：已落部分见 spec/02 §12 D98–D102；提案部分实现时逐片登记（E1–E5）

---

## 0. 一句话总纲

**外观三问，各答各的**：谁来写值（有效值四层级联，D99）、状态怎么变（伪类位掩码，D102 + E3/E5 扩展）、
作者怎么写（`.eustyle` 样式表，类 SCSS 语法，编译期编译为 `Define` 调用——E1/E2/E4）。
全程**无运行时选择器引擎**：字符串只允许出现在「定义/注册」时刻，绝不进入「每帧匹配」路径。

---

## 1. 设计目标与非目标

### 1.1 目标

| # | 目标 | 承载机制 |
|---|------|----------|
| G1 | 类 CSS/SCSS 的作者体验 | `.eustyle` 样式表（E1+），嵌套/伪类/变量 |
| G2 | 编译期校验（CSS 做不到的核心卖点） | 共享样式目录：模板类名（EUI031 解禁）、属性名、状态名编译期报错 |
| G3 | AOT / trimming 零反射 | 样式表 → 生成 C# `Define` 序列；运行期 O(setter) 直配 |
| G4 | 主题双色一等公民 | `theme(深色, 亮色)` 函数 → `ThemeColor`（D100） |
| G5 | 伪类匹配 O(1) 且与段缓存共存 | `PseudoStates` 位掩码进段键（D102），状态来源受控 |
| G6 | 可预测的性能 | 无选择器重匹配；类变化 = O(类内 setter) 直写 + 段级重建 |

### 1.2 非目标（明确不做清单）

- **运行时样式表加载 / 动态选择器引擎**（0.x 不做；热重载走「重编译 + 重放 Define」，远期）
- CSS specificity 演算（用「声明序后者胜」替代，语义可预测）
- `em/rem/%`、`calc()`、`@media`、transition/animation
- CSS `var()` 运行时自定义属性——**该角色由「属性层缺失 → painter 回落主题令牌」天然承担**
  （不写 `background` = 令牌穿透，见 §6.2）
- 控件模板（S-D：换视觉树的结构定制，与样式正交，1.0 后独立片）

---

## 2. 分层模型（语义核心，D99 已落）

### 2.1 有效值四层

```
local（inline / 属性直赋）
  > class（样式类写入 _styleValues）
    > inherited（FontSize 等经属性系统继承）
      > default（StyledProperty 默认值）
```

- class 层读写：`SetStyleValue` / `ClearStyleValues`；local 清除：`ClearLocalValue`
  （`SetValue(prop, null)` 留 null 值盒占层——「命中」与「值为 null」是两回事）。
- 失效：层变化走 `ApplyValueChange` → 属性失效链路；同值写入短路，但**伪类状态位变化**
  额外 bump 渲染版本，段键携带状态掩码兜底（见 §3）。

### 2.2 冲突解析总表

| 冲突场景 | 解析规则 | 实现点 |
|----------|----------|--------|
| local vs class | local 胜 | D99 值盒分层 |
| 多类叠加（`Classes=["a","b"]`） | 数组序后者胜 | D99 ApplyClass 顺序 |
| base 值 vs 伪类变体 | 变体胜 | D102 变体在 base 后应用 |
| 变体 vs 变体（同时命中多个） | **声明序后者胜**（≈ CSS 同优先级源序） | D102 |
| 嵌套派生类 vs 父类 | 派生类后应用 = 胜（更具体） | E4 注入序 |
| 静态覆盖 vs 主题资源 vs 令牌（painter 内） | resource > 静态覆盖 > 主题令牌 | D100 |

> 注意最后一行是**同属性内的 painter 解析序**（`Visual.BackgroundResource` vs `Visual.Background`），
> 与值盒四层是正交的两维：值盒决定「谁写这个属性」，D100 序决定「painter 读取时如何合成」。

---

## 3. 伪类系统（D102 已落 + E3/E5 扩展）

### 3.1 位掩码模型

```csharp
[Flags]
public enum PseudoStates { None = 0, Hover = 1, Pressed = 2, Focused = 4, Disabled = 8 /* E3 ✅ D103 */ }
```

- 元素持有三 bool（`IsHovered/IsPressed/IsFocused`，setter 为 **internal**——写入者受控），
  `PseudoStates` 为合成掩码。
- 状态变化 = `InvalidateRender`（版本 bump）+ `ReapplyStyles`（class 层按新掩码重选变体）。
- **段键携带状态掩码**：状态翻转即使值被短路，段也重建——hover 帧间正确切换。
- 焦点判定同源：retained 复用键与全量重画统一为「元素状态 ∪ focused 参数」。

### 3.2 状态的三种来源（写入者受控，全平台共识）

| 来源 | 状态位 | 维护者 | 落地 |
|------|--------|--------|------|
| 平台通用位 | `:hover` `:pressed` `:focus`（`:focus-within` 远期） | InputRouter / FocusManager | ✅ D102 |
| 平台通用位 | `:disabled` | `IsEnabled` 属性 + 属性系统（有效值沿树继承） | ✅ D103（E3） |
| 控件语义位 | `:checked` `:indeterminate` `:valid`… | **控件自身**（属性变化回调里翻转自己的位） | 需要样式化时逐控件加 |
| 第三方注册位 | 任意（`:loading` 等） | `PseudoStates.Register(":loading")` → 返回掩码；注册者经 `SetRegisteredState(mask, bool)` 翻转 | E5 |

设计守则：**枚举位是引擎，不是枷锁**。字符串只存在于注册时刻（启动期，正好接编译期目录校验），
运行期全是掩码测试。注册位直接 OR 进 `PseudoStates` 掩码与段键，段缓存机制零改动
（int 掩码 32 位充足；不够升级 long，段键 record 同步扩字段）。

### 3.3 变体声明

C# 链式（D102 已落）：

```csharp
registry.Define("primary", s => s.Background(new ThemeColor(Dark: ..., Light: ...)))
    .Hover(s => s.Background(...))
    .Pressed(s => s.Background(...));
```

- 变体按声明顺序应用，同时命中多个时**后声明胜**（CSS 源序语义）。
- 组合态（`:hover:pressed` 同时）不设独立语法：靠声明序表达（`Pressed` 声明在 `Hover` 后即按压优先）。
  E5 起如有真实需求，再加 `.State(Hover | Pressed, ...)` 精确掩码重载。
- `:not()`（排除位）远期：变体声明加「排除掩码」，匹配条件 `(states & need) == need && (states & excluded) == 0`。

### 3.4 与 Web / Avalonia 的对照（写入哲学一致）

| | Web 标准 | Avalonia | EclipseUI |
|---|---|---|---|
| 自定义状态 | `:state(x)`（仅自定义元素） | 任意控件子类 | 注册位（E5） |
| 写入权 | `ElementInternals`（组件私有） | `protected`（继承才能写） | `internal` + 注册者句柄 |
| 载体 | 字符串集合 | 字符串（Classes 的 `:` 条目） | 位掩码 |
| 匹配 | 浏览器引擎 | 运行时选择器重匹配 | O(1) 位测试 + 段键 |
| 校验 | 无 | `[PseudoClasses]` 仅 IDE 提示 | 注册目录 → 编译期校验（E5） |

---

## 4. 样式表语法 `.eustyle`（E1/E4 核心提案）

### 4.1 文件与入口

- 扩展名：`*.eustyle`（与 `*.ui` 平行的独立文件，随程序集编译）。
- 编译产物：`StyleBootstrap.g.cs`（生成 `Apply(StyleRegistry)` 静态方法）。
- 接入：`AppBuilder.Configure<App>().UseStyles(StyleRegistry.Instance, StyleBootstrap.Apply)`——
  **与现有 API 完全一致，运行时零改动**。
- 手写 C# `Define` 与样式表可共存（迁移期），同名重复注册报 `EUI052`；约定样式表为唯一来源。

### 4.2 选择器子集

| 阶段 | 语法 | 编译产物 |
|------|------|----------|
| E1 | `.class { }` | `Define("class", ...)` |
| E1 | `.class:hover { }` `:pressed` `:focus`（`:disabled` E3 后） | `.Hover(s => ...)` 变体链 |
| E4 | `.panel > Button { }` | 派生类 `panel__button`（塌缩，见 §4.5） |
| E4 | `.panel TextBlock.title { }` | 派生类 `panel__text-title` |
| E4 | `.panel > Button:hover { }` | 派生类的伪类变体 |
| 不做 | `*`、属性选择器、兄弟组合 `~`/`+`、多类交集 `.a.b` | —（报 `EUI048`「已保留未实现」口径） |

### 4.3 属性表（属性名目录 = StyleBuilder 公共 setter 集，单一来源）

| 样式表属性 | setter | 值类型 |
|------------|--------|--------|
| `background` | `Background` | color / theme 双色 |
| `foreground` | `Foreground` | 同上 |
| `padding` | `Padding` | length（逻辑单位 double） |
| `corner-radius` | `CornerRadius` | length |

属性集随 StyleBuilder 扩容同步扩容（编译器目录从 setter 枚举生成，不手维护两份表）。

### 4.4 值语法

```scss
$primary: theme(#22c55e, #16a34a);     // SCSS 编译期变量（E1 就支持：纯常量替换）

.primary {
  background: $primary;                 // ThemeColor 双色
  background: #22c55e;                  // 字面量（三/六/八位 hex）
  background: theme(#2a3040, #ffffff);  // 双色函数 → new ThemeColor(Dark, Light)
  padding: 16;                          // 逻辑单位裸数字
  corner-radius: 12;
}
```

- **不做**：运行时变量（CSS var）、`lighten()/darken()` 等颜色函数（E5 可选：编译期算好写死常量，
  兼容直配模型）、单位后缀（`px` 等——逻辑单位即唯一单位）。
- **主题令牌引用**：不提供 `token(accent)` 语法——「不写该属性」即令牌穿透（§6.2），
  与 CSS var() 的角色等价且零成本。

### 4.5 嵌套与编译期塌缩（E4——无引擎获得结构选择器表达力的关键）

**核心思想：结构选择器在编译期塌缩为 BEM 命名的派生类**，运行时仍只有「类名直配」一种机制。

```scss
.panel {
  background: theme(#2a3040, #ffffff);
  padding: 16;

  > Button {                      // 自动命名 → 派生类 panel__button
    padding: 8 16;                // （多值 padding 支持与否随 StyleBuilder 形态，E1 落定）
  }

  TextBlock.title {               // 后代 + 类约束 → panel__text-title
    font-size: 20;
  }

  > Button:hover {                // 派生类的伪类变体
    padding: 8 16;
  }
}
```

塌缩规则：

1. 命名：`{父类名}__{子类型名或子类名}`（全小写 kebab）；自动命名冲突报 `EUI051`，
   第一版不提供显式改名（重名时拆两个中间类，人工成本低）。
2. **注入**：`.ui` 编译器解析模板时，对「挂 `panel` 类的元素」的静态子树做匹配
   （`.ui` 树编译期已知，含 `@foreach`/`@if` 分支内的模板），给匹配子元素的 `Classes`
   **追加**派生类（排在原类之后 → §2.2 派生类胜）。
3. **边界（明示）**：C# 代码动态建树拿不到注入——此类场景手工写 BEM 名
   （`Classes = ["panel__button"]`），诊断不覆盖、文档不承诺。
4. 派生类同样进样式目录 → 模板里直接写 `Class="panel__button"` 也能校验。

### 4.6 完整示例（源 → 生成代码）

```scss
// app.eustyle
$accent: theme(#22c55e, #16a34a);

.card {
  background: theme(#2a3040, #ffffff);
  corner-radius: 12;
  padding: 16;

  &.primary {                      // 不做 .a.b 交集——E1 报保留；用双类叠加替代
    background: $accent;
  }

  &:hover   { background: theme(#30d158, #1aad52); }
  &:pressed { background: theme(#1e8e4e, #128a40); }

  > Button { padding: 8 16; }
}
```

```csharp
// <auto-generated/> StyleBootstrap.g.cs
public static class StyleBootstrap
{
    public static void Apply(StyleRegistry r)
    {
        r.Define("card", s => s
            .Background(new ThemeColor(new(42, 48, 64), new(255, 255, 255)))
            .CornerRadius(12)
            .Padding(16));
        r.Define("card__button", s => s.Padding(24 /*padding 多值已定案：单值，见 §8*/));
        r.Define("primary", s => s.Background(new ThemeColor(new(34, 197, 94), new(22, 163, 74))));
        // `&:hover`/`&:pressed` 编进同名 Define 的变体链（方案 B，已定案）：
        r.Define("card", s => s /*…*/)
            .Hover(s => s /*…*/)
            .Pressed(s => s /*…*/);
    }
}
```

> **变体物理形态（已定案）**：**方案 B 变体链**——`&:hover` 编进同名 `Define` 的变体链，
> 类名面不膨胀、与手写 C# 产物同构；方案 A（物理类）仅当未来出现「模板强制状态类」需求再议。

### 4.7 SCSS 兼容范围总表

| SCSS 特性 | 状态 |
|-----------|------|
| 变量 `$x`（编译期替换） | ✅ E1 |
| 嵌套（类/类型/伪类） | ✅ E4（塌缩语义，非运行时匹配） |
| `&` 父引用（`&.primary` 形态保留报错，`&:hover` 支持） | ✅ E1/E4 |
| `theme()` 自定义函数 | ✅ E1（内置，非用户函数） |
| `@mixin` / `@use` / `@each` | ⛔ 不做（重复用 C# 侧 `Define` 或后续按需） |
| 颜色函数 `lighten/darken/rgba` | ⛔ E1，远期可作编译期常量求值 |
| 插值 `#{}`、注释 `//`、`/* */` | `//` 与 `/* */` ✅ E1；插值 ⛔ |

### 4.8 内联形态：`@style` 组件块与元素级 `Style=` 属性（E1 扩展 + E2 转正）

「样式内联在 .ui 里」拆成**两个正交机制**，各有 Web 先例对应：

**① 组件级 `@style` 块**（HTML `<style>` / Vue SFC scoped 的对应物）——E1 扩展：

```html
<StackPanel Gap="10">
    <Button Class="card primary">go</Button>
</StackPanel>

@style {
    $card-bg: theme(#2a3040, #ffffff);

    .card {
        background: $card-bg;
        corner-radius: 12;

        &:hover { background: theme(#30d158, #1aad52); }  // 变体同 .eustyle
    }
    .primary { foreground: theme(#e8ecf4, #1a1f2e); }
}

@code { /* ... */ }
```

- 语法形态与既有 `@code`/`@using` 顶层指令**同构**（Lexer/Parser 已有顶层指令块链路，
  新增一种指令；Analyzer 同款「仅顶层允许」诊断）。
- **作用域规则（默认 scoped）**：块内类名编译期自动加组件前缀（`Showcase__card`）注册——
  物理上仍是全局 `StyleRegistry` 的普通类名（运行时零新机制），但前缀含组件名 ⇒
  **跨文件碰撞不可能发生**，EUI052（双通道冲突）对组件块天然不适用。
- **解析顺序**：本文件 `Class="…"` 先查本文件 `@style` 块、再查全局样式目录；
  两处皆无 → EUI031。定义与引用在同一编译单元内闭环校验——类名拼错直接编译不过，
  这是外部样式表（甚至 CSS）都拿不到的作者体验。
- 与 `.eustyle` **共享同一解析核心**（词法/属性表/变体/`$var`/`theme()`）——E1 落地解析器后，
  块形态只是 UiCompiler 侧的接入薄层（解析产物→生成 `Define` 调用挂到组件初始化）。
- 分工约定：**组件私有样式进 `@style`，跨组件复用进 `.eustyle`**（SCSS 的局部/全局惯例同款）。

**② 元素级 `Style="…"` 属性**（HTML `style=""` 的对应物）——E2 转正（EUI031 原保留语义之一）：

```html
<Button Style="background: #c45499; corner-radius: 18">inline</Button>
```

- 语义：**local 层直赋**（§2.1 最高优先级）——生成代码直译为 `Visual.SetXxx` 系 setter 调用，
  运行时零新机制。
- **只允许静态值**：不支持变体（元素属性没有「状态」语境——CSS 同样不允许 `style=""`
  里写 `:hover`），状态外观一律走类名通道；`theme()` 双色允许（local 层存 `ThemeColor`）。
- 值校验复用 EUI048–050，诊断定位到属性值 span。

**取舍记录**：不做「块内类名不加前缀直进全局表」的文件级全局注入形态——作用域是内联样式的
核心价值；要全局复用请写 `.eustyle`（同一语法，挪个位置而已）。

---

## 5. 编译管线与校验

```
app.eustyle ──► EustyleCompiler ──► StyleBootstrap.g.cs ─┐
                 │（解析产物：样式目录）                   │
                 │  { 类名 → 属性集/变体/派生类名 }        ├─► C# 编译 ──► 程序集
                 ▼                                       │
             样式目录（内存/中间文件）                     │
                 │                                       │
*.ui ──► UiCompiler ──► *.g.cs ──────────────────────────┘
            │  ① Class="…" 逐个查目录 → 未知类名 EUI031（解禁）
            │  ② E4：静态子树匹配 → 子元素注入派生类
            └ ③ 注册位（E5）同目录校验 :state 名
```

诊断码分配（EUI048 起，表接 design/03 诊断总表）：

| 码 | 语义 | 级别 |
|----|------|------|
| `EUI031` | **转正**：模板 `Class="…"` 引用样式目录中不存在的类（原「保留未实现」语义终止） | Error |
| `EUI048` | 样式表语法错误 / 不支持的选择器形态 | Error |
| `EUI049` | 未知属性名（不在 StyleBuilder setter 目录） | Error |
| `EUI050` | 值类型不匹配（color/length/theme 形态不符） | Error |
| `EUI051` | 嵌套派生类自动命名冲突 | Error |
| `EUI052` | 样式表类与手写 `Define` 重复注册（双通道冲突） | Warning（运行时后者覆盖前者的既有语义不变） |

级别口径沿用 design/03：Error 短路生成、Warning 生成但提示（对齐 EUI037/EUI024 的先例讨论）。

---

## 6. 运行时执行模型（全部沿用 D98–D102，零新增引擎）

### 6.1 生命周期

1. **启动期**：`UseStyles` 执行 `StyleBootstrap.Apply` —— `Define` 序列求值一次，setter 固化
   （对应 CSS 的「样式表解析完成」时刻）。
2. **装配期**：元素 `Classes` 赋值 → Provider 应用；Provider 未装配则记 `PendingClasses` 晚装配补应用。
3. **每帧**：painter 读元素有效值（值盒分层）+ `PseudoStates` 掩码选变体；段键
   `(Version, Position, Size, PseudoStates)` 未变 → 段复用（retained 红利）。
4. **换主题**：`ReplaceTheme` 丢段重建 → `ThemeColor` 资源色随新表自动刷新（D100 红利）。

### 6.2 「不写 = 令牌穿透」——var() 的零成本等价物

CSS 里 `var(--accent)` 的角色，在我们的模型里由**层的缺席**天然实现：
元素某属性无 local/class 值 → painter 回落 `ElementTheme` 令牌（`Accent`/`Surface`/`Text`…）。
换主题 = 换令牌表 + 丢段 = 全部「未写死」的属性统一跟随。**这是架构红利，不需要 var 机制。**

---

## 7. 落地路线图（E1–E5，每片独立可交付）

| 片 | 内容 | 验收口径 | 依赖 |
|----|------|----------|------|
| **E1** | EustyleCompiler：词法/语法解析、属性值绑定、伪类变体、`$变量`、生成器 + EUI048–050；**`@style` 组件块（§4.8①，共享解析核心）** | 画廊样式表化（GalleryStyles → app.eustyle）；`@style` 块编译+作用域前缀用例；测试 +15 左右 | 无 |
| **E2** | 类名目录接入 UiCompiler + 模板 `Class` 语法转正（EUI031）+ 元素级 `Style=` 属性（§4.8②） | Showcase.ui 用 `Class`，未知类名编译期报错用例；`Style=` 属性生成 local 直调用例 | E1 |
| **E3** | `:disabled`：`IsEnabled` 属性 + 第 4 枚举位 + painter 置灰兜底 + `.Disabled(...)` / `:disabled` | 全控件可禁用；TextBox 顺路受益；InputRouter 对 disabled 元素跳过命中 | 无（可与 E1 并行） |
| **E4** | 嵌套塌缩 + `.ui` 注入 + EUI051 | 画廊分区卡派生样式走塌缩链路；注入用例（含 @foreach 分支） | E1+E2 |
| **E5** | 注册制伪类（`PseudoStates.Register`）+ `@states` 声明 + EUI052 + 可选颜色函数 | 第三方状态端到端用例 | E1 |

**执行顺序（已批准，2026-11-12 定案）：E3 → E1 → E2 → E4 → E5**。
依据：E3 补齐 0.1 控件集缺口最急（`:disabled` 是 TextBox 的前置）；E1/E2 是作者体验主线；
E4 依赖 E1+E2；E5 收尾开放性。0.1 alpha 出口只需 E3（+ 可选 E1）。
每片独立提交并逐片登记台账（E3 = D104 起，E1 起沿 E 编号在 design/03 §12 双侧登记）。

| 片 | 状态 |
|----|------|
| E3 | ✅ 完成（2026-11-09，D103）：Disabled 位 + IsEnabled 继承 + 路由/焦点门控 + 置灰令牌 + `.Disabled()` 变体 + FocusRingWidth；699 例全绿 |
| E1–E5 | ⏳ 排队（E1 下一片） |

---

## 8. 风险与开放问题

| 风险 | 应对 |
|------|------|
| 塌缩注入对动态树失效 | 明示边界（§4.5-3）+ 手工 BEM 兜底；不做运行时补匹配 |
| 双通道治理（手写 Define × 样式表） | EUI052 Warning + 约定样式表唯一来源；生成器覆盖手写场景逐步收敛 |
| SCSS 兼容蔓延 | §4.7 明确不做清单；新特性先过「编译期直配」一致性门槛 |
| 注册位上限 | 32 位掩码充足；不够升 long（段键 record 同步扩字段，一次性迁移） |
| 焦点环宽度等主题参数不可类覆盖 | StyleBuilder 暴露 `outline-width` setter（E3 顺手项，painter 读 class 值回落令牌） |
| `padding: 8 16` 多值 | **已定案（2026-11-12）**：E1 单值（StyleBuilder `Padding` 为 `double`）；四边独立/多值待属性系统引入 Thickness 支持后扩，此前样式表多值一律报 `EUI050`，不做运行时兜底 |

---

## 9. 已落地 ↔ 本设计对照

| 已落 | 设计条目 |
|------|----------|
| D98（S-A 主题令牌化） | §6.1-4 换主题丢段链路 |
| D99（S-B Class 样式 + 四层） | §2 全节 |
| D100（S-C ThemeColor） | §4.4 `theme()`、§2.2 末行、§6.2 |
| D102（伪类三位 + 链式变体 + 段键） | §3.1–3.3、§6.1-3 |
| D101（AppBuilder/DI） | §4.1 接入点 `UseStyles` |
| 提案 E1–E5 | §4、§5、§7 |
