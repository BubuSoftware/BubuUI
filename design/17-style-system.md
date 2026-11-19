# 17 · 样式系统完整设计（Styling System）

> 状态：**全部五片落地**——D98–D102 + E3(D103)/E1(D104)/E2(D105)/E4(D106)/E5(D107)（2026-11-10）
> 关联：design/03（模板引擎，§3.3.9 `Class`/`Style` 保留）、design/09 §9.3 G4（样式/主题行）、
> design/04（Runtime 属性引擎）、design/05（绘制与段缓存）
> 台账：已落部分见 spec/02 §12 D98–D102；提案部分实现时逐片登记（E1–E5）

---

## 0. 一句话总纲

**外观三问，各答各的**：谁来写值（有效值级联，D99；后续增加template层和[主题默认值](./27-fluent-theme.md)）、状态怎么变（伪类位掩码，D102 + E3/E5 扩展）、
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
- `em/rem/%`、`calc()`、`@media`、关键帧 animation（样式过渡已支持，见 [design/29](./29-animation-system.md)）
- CSS `var()` 运行时自定义属性——**该角色由「属性层缺失 → painter 回落主题令牌」天然承担**
  （不写 `background` = 令牌穿透，见 §6.2）
- 控件模板（S-D：换视觉树的结构定制，与样式正交，1.0 后独立片）

---

## 2. 分层模型（语义核心，D99 已落）

### 2.1 当前有效值层

```
local（inline / 属性直赋）
  > template（模板根默认值）
    > class（样式类写入 _styleValues）
      > inherited（FontSize 等经属性系统继承）
        > theme default（沿实际 Parent 查找的皮肤默认值）
          > property default（StyledProperty 默认值）
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
> 与属性值层是正交的两维：值层决定「谁写这个属性」，D100 序决定「painter 读取时如何合成」。

---

## 3. 伪类系统（D102 已落 + E3/E5 扩展）

### 3.1 位掩码模型

```csharp
[Flags]
public enum PseudoStates { None = 0, Hover = 1, Pressed = 2, Focused = 4, Disabled = 8, FocusVisible = 16, FocusWithin = 32 }
```

- 元素持有受控状态：`IsHovered/IsPressed` 由输入路由写入；只读 `IsFocused/IsFocusWithin/IsFocusVisible` 由 FocusManager 批量提交，
  `PseudoStates` 为合成掩码。
- 状态变化 = `InvalidateRender`（版本 bump）+ `ReapplyStyles`（class 层按新掩码重选变体）。
- **段键携带状态掩码**：状态翻转即使值被短路，段也重建——hover 帧间正确切换。
- 焦点判定同源：绘制只读取运行时状态；焦点视觉描述符同时进入 retained 段键和 damage。绘制器不再接受 focused 参数，详见 [design/25](./25-focus-visuals.md)。

### 3.2 状态的三种来源（写入者受控，全平台共识）

| 来源 | 状态位 | 维护者 | 落地 |
|------|--------|--------|------|
| 平台通用位 | `:hover` `:pressed` `:focus` `:focus-visible` `:focus-within` | InputRouter / FocusManager | ✅ D102 |
| 平台通用位 | `:disabled` | `IsEnabled` 属性 + 属性系统（有效值沿树继承） | ✅ D103（E3） |
| 控件语义位 | `:checked` `:indeterminate` `:valid`… | **控件自身**（属性变化回调里翻转自己的位） | 需要样式化时逐控件加 |
| 第三方注册位 | 任意（`:loading` 等） | `PseudoStatesRegistry.Register("loading")` → 返回掩码（幂等）；注册者经 `Element.SetRegisteredState(mask, bool)` 翻转（掩码即凭证——拒绝平台位/未注册位） | ✅ E5（D107） |

设计守则：**枚举位是引擎，不是枷锁**。字符串只存在于注册时刻（启动期，正好接编译期目录校验），
运行期全是掩码测试。注册位直接 OR 进 `PseudoStates` 掩码与段键，段缓存机制零改动
（int 掩码 32 位充足；不够升级 long，段键 record 同步扩字段）。

E5 落地（D107）：`PseudoStates` 是 enum 不能挂静态方法——注册表落 `PseudoStatesRegistry` 静态类；
样式表侧 `@states { name; }` 顶层声明块（预扫两遍解析，声明序无关），声明的名字在本表内可作
伪类选择器、跨文件经全局目录兜底校验；产物是 `StyleBootstrap.Apply` 顶部的幂等 `Register` 行 +
变体链 `.State(Register("name"), …)`。撞平台保留名（hover/pressed/active/focus/focus-visible/focus-within/disabled）编译期
报 EUI050、运行时 `Register` 抛 ArgumentException（同名单双通道拦截）。

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
| E1 | `.class:hover { }` `:pressed` `:focus` `:focus-visible` `:focus-within` `:disabled` | `.Hover(s => ...)` 变体链 |
| ✅ E4 | `.panel > Button { }` | 派生类 `panel__button`（塌缩，见 §4.5） |
| ✅ E4 | `.panel TextBlock.title { }` | 派生类 `panel__text-block-title`（类型名 kebab 化） |
| ✅ E4 | `.panel > Button:hover { }` | 派生类的伪类变体 |
| ✅ E4 | 同名段重复（`.panel > Button` ×2） | **合并**（CSS 同选择器多块 = 声明合并，声明序后者胜） |
| 不做 | `*`、属性选择器、兄弟组合 `~`/`+`、多类交集 `.a.b` | —（报 `EUI048`「已保留未实现」口径） |

### 4.3 属性表（符号派生目录，单一来源）

✅ 落地（2026-11-10）：**编译器侧不再有任何手写的属性表**。一次 Roslyn 符号扫描
（`StylePropertyCatalogFactory`，随 `UiCatalog` 走同一条 `CompilationProvider` 管道）产出
`StylePropertyCatalog`，Parser 的名字/值形态校验、EUI051 的支持列表、`EmitLocalSetters`
的目标字段、theme() 准入全部读这一份数据：

- 从 `StyleBuilder` 的 public 实例 setter（排除泛型 `Set<T>`）推出**属性集**与值形态
  （`Color`/`ThemeColor` → color，`double` → number）；同名 `Color`/`ThemeColor` 重载合并，
  并决定是否接受 `theme()`。
- 按约定找 `public static StyledProperty<T>` 字段 `{Name}Property` / `{Name}ResourceProperty`，
  校验 `T` 与 setter 形态一致，产出 `global::` 全限定字段名供 `Style=` 发射
  `el.SetValue(<字段>, <值>)`。
- 声明不合规（缺字段、字段歧义、形态不符）进目录 `Problems`，构建期以 `EUI026` 报出；
  `StylePropertyCatalogTests` 用反射把目录与 `StyleBuilder` 的公开 setter 集对死，防漂移。
- **标注通道（design/19，✅ E6/D115）**：带 `[UiStyleProperty]` 的 `public static readonly
  StyledProperty<T>` 字段（候选程序集全局扫描，显式 opt-in——不扫全部 `StyledProperty` 字段，
  避免内部属性污染目录）与内建 setter **取并集**进同一份目录：属性名 = 显式 `Name` ?? 字段名去
  `Property` 后缀；值形态从 `T` 派生（`double` → number、`Color`/`ThemeColor` → color、枚举 →
  enum keyword、`string` → string），枚举成员表在扫描期算好存进 def（发射期零符号依赖）。
  声明不合规报 **EUI058**（字段非 `public static readonly` / 非闭包 `StyledProperty<T>` /
  `T` 含泛型形参 / `Name` 解析为空 / 值类型不支持），不进目录、不抛异常。冲突规则
  （design/19 §7）：用户与内建同名 → **EUI059**、内建胜出、目录取内建（字段解析时未标注候选
  优先——用户同名声明不把内建字段挤成「多程序集歧义」）；两个用户声明同名 → EUI059、双双拒绝。

当前目录（自动派生，非手写；另有应用侧 `[UiStyleProperty]` 标注属性并集进入）：

| 规范名（CLR 原名） | 值形态 | 目标字段 | theme() |
|---|---|---|---|
| `Background` | color | `Visual.BackgroundProperty` | ✅ `BackgroundResourceProperty` |
| `Foreground` | color | `Visual.ForegroundProperty` | ✅ `ForegroundResourceProperty` |
| `CornerRadius` | number | `Visual.CornerRadiusProperty` | — |
| `FocusRingWidth` | number | `Visual.FocusRingWidthProperty` | — |
| `FocusRingOffset` | number | `Visual.FocusRingOffsetProperty` | — |
| `FocusRingColor` | color | `Visual.FocusRingColorProperty` | ✅ `FocusRingColorResourceProperty` |
| `FocusVisualMode` | enum | `Element.FocusVisualModeProperty`（UiStyleProperty 标注） | — |
| `Padding` | number | `Panel.PaddingProperty` | — |
| `FontSize` | number | `TextBlock.FontSizeProperty` | — |

**命名**：属性名就是控件/属性对象的原始 CLR 名（`CornerRadius`、`FontSize`），与 `.ui` 模板属性
的书写一致，**大小写敏感、精确匹配**：`StylePropertyCatalog.Find` 按 `Ordinal` 比较，没有别名、
没有大小写或 `-`/`_` 归一化。因此 `corner-radius` / `cornerradius` / `CORNERRADIUS` 一律未命中，
报 **EUI051**（消息提示使用大小写敏感的 CLR 名并列出目录支持项）。不设旧写法兼容层。
大写开头的属性名与嵌套类型选择器由前瞻到 `{` / `;` 区分（选择器一定开块）。

加新样式属性两条路：① 框架内——运行时注册 `StyledProperty` 字段 + 加 `StyleBuilder` setter
（+ painter 读取点）；② 应用/第三方库——给自己的静态 `StyledProperty<T>` 字段加
`[UiStyleProperty]` 即可（design/19 P1，✅ E6/D115），**不动 `StyleBuilder`**。两条路都
**Compiler/Generator 零改动**，属性名自动成为样式表可写名；旧手写四处表已删除。

待办：目录尚未带出 `OwnerType` / `Inherits` / `Invalidate` 与「适用标签类型」，因此
「`Padding` 写在 `Button` 上静默无效」仍只靠约定，未升级为编译期诊断。

### 4.4 值语法

```scss
$primary: theme(#22c55e, #16a34a);     // SCSS 编译期变量（E1 就支持：纯常量替换）

.primary {
  Background: $primary;                 // ThemeColor 双色
  Background: #22c55e;                  // 字面量（三/六/八位 hex）
  Background: theme(#2a3040, #ffffff);  // 双色函数 → new ThemeColor(Dark, Light)
  Padding: 16;                          // 逻辑单位裸数字
  CornerRadius: 12;
  Align: Center;                        // 自定义枚举属性 → keyword（成员表编译期校验，E6）
  Label: "inline";                      // 自定义 string 属性 → 字面量 / keyword 直通（E6）
}
```

值形态分派（design/19 §5.2，✅ E6/D115）——形态由目录 `Kind` 决定，编译期校验，错值报
**EUI052**；自定义属性的发射走泛型逃生口 `s.Set(<目录字段>, <值>)`，内建属性不变：

| 目标形态 | 接受的值 | 备注 |
|---|---|---|
| color | `#hex`、`theme(dark, light)` | theme() 仅当属性有 ThemeColor 资源字段；自定义属性 P1 不支持 theme（design/19 §13） |
| number | 裸数字 | — |
| enum | 枚举成员 keyword | 成员表扫描期派生存进 def（发射期零符号依赖）；未命中报 EUI052 并列期望成员 |
| string | `"字面量"`（含转义）、keyword | keyword 直通为字符串 |

- **theme 准入先行于形态检查**：`theme()` 写在不收 theme 的属性上（含全部自定义属性），
  报「does not accept theme(dark, light)」而不是「期望 number」这类类别不符消息。
- **`$变量` 仍限 number/color**——keyword/string 不进变量声明（**EUI050**）。

- **不做**：运行时变量（CSS var）、`lighten()/darken()` 等颜色函数（E5 可选：编译期算好写死常量，
  兼容直配模型）、单位后缀（`px` 等——逻辑单位即唯一单位）。
- **主题令牌引用**：不提供 `token(accent)` 语法——「不写该属性」即令牌穿透（§6.2），
  与 CSS var() 的角色等价且零成本。

### 4.5 嵌套与编译期塌缩（E4——无引擎获得结构选择器表达力的关键）

**核心思想：结构选择器在编译期塌缩为 BEM 命名的派生类**，运行时仍只有「类名直配」一种机制。

```scss
.panel {
  Background: theme(#2a3040, #ffffff);
  Padding: 16;

  > Button {                      // 自动命名 → 派生类 panel__button
    Padding: 8 16;                // （多值 padding 支持与否随 StyleBuilder 形态，E1 落定）
  }

  TextBlock.title {               // 后代 + 类约束 → panel__text-title
    FontSize: 20;
  }

  > Button:hover {                // 派生类的伪类变体
    Padding: 8 16;
  }
}
```

塌缩规则（✅ E4 落地，2026-11-09）：

1. 命名：`{父派生名}__{kebab(类型名)}{-类约束}`（全小写；`TextBlock.title` →
   `text-block-title`）；同一派生名的多个嵌套段**合并**（CSS 同选择器多块 = 声明合并，
   声明序后者胜；同名子嵌套递归聚合）；派生名与**显式类名**撞车报 `EUI053`，
   第一版不提供显式改名（拆两个中间类规避，人工成本低）。
2. **注入**：`.ui` 编译器解析模板时，对「挂 `panel` 类的元素」的静态子树做匹配
   （`.ui` 树编译期已知，`@foreach`/`@if`/`@switch` 分支内的模板编译期展开、运行时就是
   直接子——直接子匹配穿透块结构；插槽填充不穿透——内容运行时挂进组件内部），给匹配
   子元素的 `Classes` **追加**派生类（排在原类之后 → §2.2 派生类胜）。
3. **边界（明示）**：C# 代码动态建树与 `Class={expr}` 表达式形态拿不到注入
   （运行时类列表编译期未知）——此类场景手工写 BEM 名
   （`Classes = ["panel__button"]`，派生类在目录里可校验），诊断不覆盖、文档不承诺。
4. 派生类同样进样式目录 → 模板里直接写 `Class="panel__button"` 也能校验。

### 4.6 完整示例（源 → 生成代码）

```scss
// app.eustyle
$accent: theme(#22c55e, #16a34a);

.card {
  Background: theme(#2a3040, #ffffff);
  CornerRadius: 12;
  Padding: 16;

  &.primary {                      // 不做 .a.b 交集——E1 报保留；用双类叠加替代
    Background: $accent;
  }

  &:hover   { Background: theme(#30d158, #1aad52); }
  &:pressed { Background: theme(#1e8e4e, #128a40); }

  > Button { Padding: 8 16; }
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
        Background: $card-bg;
        CornerRadius: 12;

        &:hover { Background: theme(#30d158, #1aad52); }  // 变体同 .eustyle
    }
    .primary { Foreground: theme(#e8ecf4, #1a1f2e); }
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

**② 元素级 `Style="…"` 属性**（HTML `style=""` 的对应物）——✅ E2 落地（EUI031 原保留语义之一）：

```html
<Button Style="Background: #c45499; CornerRadius: 18">inline</Button>
```

- 语义：**local 层直赋**（§2.1 最高优先级）——生成代码直译为
  `el.SetValue(<目录里的 StyledProperty 字段>, <值>)`，目标字段由 §4.3 的符号派生目录给出，
  运行时零新机制。
- **只允许静态值**：不支持变体（元素属性没有「状态」语境——CSS 同样不允许 `style=""`
  里写 `:hover`），状态外观一律走类名通道；`theme()` 双色允许（local 层存 `ThemeColor`）。
- 值校验复用 EUI050–052，诊断定位到属性值 span。

**取舍记录**：不做「块内类名不加前缀直进全局表」的文件级全局注入形态——作用域是内联样式的
核心价值；要全局复用请写 `.eustyle`（同一语法，挪个位置而已）。

### 4.9 `.eustyle` 命名空间查找

`.eustyle` 模块可声明一次 `@namespace My.Controls`，把该命名空间及逐级外层命名空间设为
控件、模板和 `ThemeResource<T>` 类型的隐式查找范围。查找按完整名称、当前命名空间到外层链、
显式 `@using` 导入的顺序进行；命中更内层命名空间后，该层独占同名选择。该指令不改变生成
`StyleBootstrap` 使用的项目 `RootNamespace`。内联 `.ui` 的 `@style` 块不接受 `@namespace`，
使用宿主 `.ui` 的命名空间。

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
            │  ① Class="…" 逐个查目录 → 未知类名 EUI031（✅ E2 已落）
            │  ② E4：静态子树匹配 → 子元素注入派生类（✅ 已落）
            └ ③ 注册位（✅ E5）：@states 声明目录（StyleStatesCatalog）校验 :state 名
```

诊断码分配（**EUI050 起**——EUI048/EUI049 已被 @using 别名（K31）与表达式语义（G4）占用；
初版本表误分配 EUI048–052，2026-11-09 落地 E1 时按实际占用顺延修正）：

| 码 | 语义 | 级别 |
|----|------|------|
| `EUI031` | **已转正（E2）**：模板 `Class="…"` 引用目录外类名 / `Class={expr}` 形态不符 / `Style=` 表达式 / `@bind-Class`（原「保留未实现」语义终止；`<Class>` 标签形态保留不变） | Error |
| `EUI050` | 样式表语法错误 / 不支持的选择器形态 | Error |
| `EUI051` | 未知属性名（不在 StyleBuilder setter 目录） | Error |
| `EUI052` | 值类型不匹配（color/length/theme 形态不符、未定义变量、单位后缀） | Error |
| `EUI053` | **已落地（E4）**：嵌套塌缩的派生名与显式类名撞车（同名嵌套段合并不算冲突） | Error |
| `EUI054` | **已落地（E5）**：样式表类与手写 `Define` 重复注册（双通道冲突——源生成器扫宿主 C# 语法树的 `Define("name"` 字符串字面量调用与样式表类求交集；Warning 挂样式表类名 span） | Warning（运行时后者覆盖前者的既有语义不变） |

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
| **E1** | EustyleCompiler：词法/语法解析、属性值绑定、伪类变体、`$变量`、生成器 + EUI050–052；**`@style` 组件块（§4.8①，共享解析核心）** | 画廊样式表化（GalleryStyles → app.eustyle）；`@style` 块编译+作用域前缀用例；测试 +15 左右 | 无 |
| **E2** | 类名目录接入 UiCompiler + 模板 `Class` 语法转正（EUI031）+ 元素级 `Style=` 属性（§4.8②） | Showcase.ui 用 `Class`，未知类名编译期报错用例；`Style=` 属性生成 local 直调用例 | E1 |
| **E3** | `:disabled`：`IsEnabled` 属性 + 第 4 枚举位 + painter 置灰兜底 + `.Disabled(...)` / `:disabled` | 全控件可禁用；TextBox 顺路受益；InputRouter 对 disabled 元素跳过命中 | 无（可与 E1 并行） |
| **E4** | 嵌套塌缩 + `.ui` 注入 + EUI053 | 画廊分区卡派生样式走塌缩链路；注入用例（含 @foreach 分支） | E1+E2 |
| **E5** | 注册制伪类（`PseudoStates.Register`）+ `@states` 声明 + EUI052 + 可选颜色函数 | 第三方状态端到端用例 | E1 |

**执行顺序（已批准，2026-11-12 定案）：E3 → E1 → E2 → E4 → E5**。
依据：E3 补齐 0.1 控件集缺口最急（`:disabled` 是 TextBox 的前置）；E1/E2 是作者体验主线；
E4 依赖 E1+E2；E5 收尾开放性。0.1 alpha 出口只需 E3（+ 可选 E1）。
每片独立提交并逐片登记台账（E3 = D104 起，E1 起沿 E 编号在 design/03 §12 双侧登记）。

| 片 | 状态 |
|----|------|
| E3 | ✅ 完成（2026-11-09，D103）：Disabled 位 + IsEnabled 继承 + 路由/焦点门控 + 置灰令牌 + `.Disabled()` 变体 + FocusRingWidth；699 例全绿 |
| E1 | ✅ 完成（2026-11-09，D104）：EustyleParser/Generator + EUI050–052 + `.eustyle` → StyleBootstrap.g.cs + `@style` 组件块（前缀注册）；画廊迁移 app.eustyle；722 例全绿（352/264/106） |
| E2 | ✅ 完成（2026-11-09，D105）：类名目录管道（StyleClassCatalog 值相等跨管道）+ `Class=` 转正（本地 @style 优先解析、未知类名 EUI031、表达式 Split/ToArray 探针形态）+ `Style=` local 直赋（ParseDeclarations + Visual.SetXxx/SetValue）；739 例全绿（352/281/106） |
| E4 | ✅ 完成（2026-11-09，D106）：嵌套塌缩（EustyleNested + EustyleCollapse 合并/命名/EUI053）+ 注入规则跨管道（StyleClassCatalog 扩展）+ 模板静态子树匹配（直接子穿透块结构、插槽不穿透）+ Emitter 合并发射（派生类排后）；画廊 card 塌缩链路 + @foreach 注入；752 例全绿（352/294/106） |
| E5 | ✅ 完成（2026-11-10，D107）：PseudoStatesRegistry.Register（幂等/平台名拒绝/位 0x10 起）+ Element.SetRegisteredState（掩码即凭证）+ ClassStyleVariants.State 变体 + @states 声明（预扫两遍/跨文件全局目录/撞平台名 EUI050）+ EUI054 双通道 Warning（手写 Define 扫描）；painter 补齐 TextBlock class 背景绘制；画廊 spinner 双芯片演示；769 例全绿（359/304/106）。**样式系统五片全部落地** |

---

## 8. 风险与开放问题

| 风险 | 应对 |
|------|------|
| 塌缩注入对动态树失效 | 明示边界（§4.5-3）+ 手工 BEM 兜底；不做运行时补匹配 |
| 双通道治理（手写 Define × 样式表） | EUI052 Warning + 约定样式表唯一来源；生成器覆盖手写场景逐步收敛 |
| SCSS 兼容蔓延 | §4.7 明确不做清单；新特性先过「编译期直配」一致性门槛 |
| 注册位上限 | 32 位掩码充足；不够升 long（段键 record 同步扩字段，一次性迁移） |
| 焦点环宽度等主题参数不可类覆盖 | ✅ E3：目录里的 `FocusRingWidth`（`StyleBuilder.FocusRingWidth` + `Visual.FocusRingWidthProperty`），painter 读 class 值回落令牌 |
| `Padding: 8 16` 多值 | **已定案（2026-11-12）**：E1 单值（StyleBuilder `Padding` 为 `double`）；四边独立/多值待属性系统引入 Thickness 支持后扩，此前样式表多值一律报 `EUI050`，不做运行时兜底 |

---

## 9. 已落地 ↔ 本设计对照

| 已落 | 设计条目 |
|------|----------|
| D98（S-A 主题令牌化） | §6.1-4 换主题丢段链路 |
| D99（S-B Class 样式 + 四层） | §2 全节 |
| D100（S-C ThemeColor） | §4.4 `theme()`、§2.2 末行、§6.2 |
| D102（伪类三位 + 链式变体 + 段键） | §3.1–3.3、§6.1-3 |
| D101（AppBuilder/DI） | §4.1 接入点 `UseStyles` |
| D107（注册制伪类 E5） | §3.2 第三方注册位行、§5 ③/EUI054、§7 E5 |
| 提案 E1–E5 | §4、§5、§7（全部落地） |
