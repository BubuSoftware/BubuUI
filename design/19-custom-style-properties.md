# 19 · 样式属性自定义化设计（User-Defined Style Properties）

> 状态：**提案（未实施）**——2026-11-12 起草；基于 `[UiLiteralParser]` 落地先例（见 §11 前提）
> 关联：design/17（样式系统 §4.3 目录单一来源 / §4.4 值语法）、design/14 §14.7.3（字面量转换与
> `[UiLiteralParser]` 声明式扩展先例）、spec/01 §5（模板字面量）、spec/02 §1（R1/R2 约束）
> 代码基线：`src/EclipseUI.Compiler.Metadata/StylePropertyCatalogFactory.cs`、
> `src/EclipseUI.Compiler/Style/`（EustyleParser/EustyleGenerator/EustyleAst）、
> `src/EclipseUI.Rendering/StyleRegistry.cs`

---

## 0. 一句话总纲

把「什么属性可写」与「值怎么构造」从框架类型上解耦：**属性目录的进料口从「`StyleBuilder` 的方法」
改为「带 `[UiStyleProperty]` 标注的静态 `StyledProperty<T>` 字段」（全局符号扫描），值语法从
3 种内建字面量扩展为「按目标类型声明的解析器分派」（枚举/字符串内建、复杂类型复用
`[UiLiteralParser]`）**。校验、塌缩、`Define` 直配、AOT 零反射全部保留——只换进料口，不换引擎。

---

## 1. 背景与问题（现状事实）

`StylePropertyCatalogFactory.Build`（design/17 §4.3「单一来源」的实现）有三道锁，把样式属性
定义能力完全锁在框架程序集内：

| # | 锁 | 代码位置 | 后果 |
|---|----|---------|------|
| L1 | setter 必须是 `EclipseUI.Rendering.StyleBuilder` 的 public 实例单参方法（按元数据名精确取类型） | `StylePropertyCatalogFactory.cs:26` `StyleBuilderMetadataName` | 用户程序集的同签名类、子类、扩展方法（非该类型成员）一律不可见 |
| L2 | 值形态仅 `double` / `Color` / `ThemeColor`，其余参数类型进 EUI026 | `KindOf`，工厂 ：100 附近 | `Thickness` / 枚举 / 字符串 / 画刷不可能成为样式值 |
| L3 | 每条声明只取一个值 token，随即要求 `;` | `EustyleParser.ParseDeclaration`（:637 起） | CSS 式多值 `Padding: 8 16` 语法上不存在 |

用户当前的等效替代只有两条，且都不进 `.eustyle` 语法：手写 C# `registry.Define("x", s =>
s.Set(任意字段, 值))`（`Set<T>` 泛型逃生口，工厂明确跳过泛型方法不进目录），或模板 local 层
`P={expr}`。第三方 NuGet 消费者**没有任何**给样式表加属性的途径。

## 2. 目标与非目标

### 2.1 目标

| # | 目标 | 承载机制 |
|---|------|----------|
| G1 | 应用/第三方库从**自己的程序集**声明样式属性，不改框架源码 | `[UiStyleProperty]` 字段标注 + 全局符号扫描 |
| G2 | 枚举、字符串成为一等样式值；任意复杂类型经声明式解析器可写 | 值分派表（§5）复用 `UiTypeShape.EnumMembers` 与 `[UiLiteralParser]` |
| G3 | 编译期校验、诊断质量、增量缓存、AOT 零反射全部保留 | 目录仍是纯数据 + 值相等；扫描只读符号；`Parse` 只生成调用不执行 |
| G4 | 内建 6 属性零回归；`StyleBuilder` 通道与标注通道共存 | 目录并集 + 冲突规则（§7） |

### 2.2 非目标（明确不做）

- **不扫全部 `StyledProperty` 字段**（隐式 opt-in 缺失会把 `Visual.Bounds` 等内部属性污染目录，
  且标注缺失时无法区分「想暴露」与「纯内部」——显式标注同时是文档）
- 不做运行时反射 / 运行时样式解析 / CSS `var()` / 选择器引擎（design/17 §1.2 既有边界不动）
- 多值语法 `Padding: 8 16` 放 P3（§10），P1 不动 `ParseDeclaration` 的单值消费约定
- 不给 `Set<T>` 手写通道加目录校验（它本来就是逃生口，EUI054 双通道警告照旧）

## 3. 设计总览：两个解耦

```
属性来源：StyleBuilder 方法（硬编码元数据名）   →  [UiStyleProperty] 标注字段（全局扫描，opt-in）
值语法：  #hex / theme() / 裸数字               →  按目标类型 T 分派：内建 → 枚举/字符串 → [UiLiteralParser]
```

与 design/14 §14.7.3 `[UiLiteralParser]`（2026-11-12 已落地）同一模式：**编译期只读符号、
生成对静态 `Parse` 的调用、`Pattern` 编译期拒错**——不违反 R1（不执行用户程序集），R2 已按
「格式编译期校验、值由声明式解析器生成一次」口径放宽（spec/02 §1，2026-11-12）。

## 4. 发现机制：`[UiStyleProperty]`

### 4.1 用户写法

```csharp
namespace Probe;

public static class BadgeStyles
{
    [UiStyleProperty]                          // 属性名 = 字段名去 "Property" 后缀 → "Glow"
    public static readonly StyledProperty<double> GlowProperty =
        StyledProperty<double>.Register<Badge>("Glow", 0d);

    [UiStyleProperty(Name = "Inset", Owner = typeof(Badge))]
    public static readonly StyledProperty<Thickness> InsetProperty = ...;
}
```

### 4.2 特性定义（`Runtime/UiAttributes.cs` 追加）

```csharp
[AttributeUsage(AttributeTargets.Field)]
public sealed class UiStylePropertyAttribute : Attribute
{
    public string? Name { get; set; }      // 缺省 = 字段名去 "Property" 后缀；去后后为空必须显式
    public Type? Owner { get; set; }       // 适用控件类型；缺省 = 不校验适用性（现状口径）
}
```

### 4.3 扫描规则（`StylePropertyCatalogFactory.Build` 扩展）

1. 保留现有 `StyleBuilder` setter 扫描（内建通道，零改动）；
2. 新增：遍历 `UiCatalogFactory.CandidateAssemblies` 的全部类型字段（`CollectPropertyFields`
   **已在全局收集**，只需加「有标注才入选」过滤），对每个标注字段：
   - 字段必须 `public static readonly`、类型为构造闭包 `StyledProperty<T>`（`T` 非泛型形参）；
   - `Name` 解析：显式 `Name` → 用之；否则去 `Property` 后缀，结果为空 → 报 **EUI058**；
   - kind 推导复用 `KindOf(T)` + 枚举/字符串扩展（§5）；
   - 产出 `StylePropertyDef`，来源标记 `IsBuiltIn = false`（见 §8 冲突规则）；
   - 声明不合规（类型不符、`T` 含泛型形参、字段非只读）→ **EUI058**，不进目录、不抛异常
     （沿用「构建不抛异常，问题进 Problems」口径）。

## 5. 值语法与转换分派

### 5.1 值语法扩展（`EustyleAst` + `EustyleParser.ParseValue`）

```scss
.badge {
    Glow: 4;                    // 裸数字（现状）
    Background: theme(#333, #eee);  // 双色（现状，准入规则不变）
    Align: Center;              // ★ 新增：裸标识符 = 枚举成员（CSS keyword 风格）
    Label: "hello world";       // ★ 新增：带引号字符串
    Tone: Success;              // ★ 枚举成员同样适用于自定义枚举属性
}
```

`EustyleValue` 新增两个 record：

```csharp
public sealed record EustyleKeyword(string Name, TextSpan Span) : EustyleValue(Span);
public sealed record EustyleString(string Value, TextSpan Span) : EustyleValue(Span);
```

解析规则：`ParseValue` 中 `char.IsLetter` 分支识别出的标识符，若不是 `theme` → `EustyleKeyword`
（现状是报错）；`"` 开头 → `EustyleString`。`$var` 对新值形态**不做**插值（变量仍限于数字/颜色，
P1 保持简单；P2 视需求放开）。

### 5.2 转换分派表（`EustyleGenerator.ValueLiteral` 泛化）

| 目标类型 T | 值形态 | 转换 | 生成代码 | 现成设施 |
|---|---|---|---|---|
| `double` | number | 内建 | `4d` | 现状 |
| `Color` | color | 内建 hex | `new global::…Color(r,g,b[,a])` | 现状 |
| `ThemeColor` | theme() | 内建 | `new global::…ThemeColor(dark, light)` | 现状 |
| 枚举 | keyword | 编译期成员校验 | `global::Ns.T.Member` | `UiTypeShape.EnumMembers`（模板侧同款；未命中成员报 **EUI052** 并列候选） |
| `string` | string / keyword | 直通 | `"hello"` | — |
| 其它（`Thickness` 等） | 任意 | **复用 `[UiLiteralParser]`**：`Pattern` 编译期校验 → 生成 `T.Parse("…")` | `global::Ns.T.Parse("8 16")` | design/14 §14.7.3 整套（`UiCatalog.FindLiteralParser`） |

分派实现在 `StylePropertyDef` 上预计算（扫描期把 T 的分派结果、`ParseCall`、候选成员表算好存进
def，发射期零符号依赖）。`theme()` 准入：仍按是否存在主题资源重载/字段；自定义属性 P1 **不支持**
`theme()`（需要 `XxxResourceProperty` 约定，见 §9 开放问题）。

### 5.3 执行时机与成本（R2 口径核对）

- **class 通道**：生成代码进 `registry.Define("x", s => s.Set(field, T.Parse("…")))`——
  build lambda **注册期求值一次**（`StyleRegistry.cs:126`「Define 时求值一次」，setter 记录为
  预计算命令对象 `StyleSetter<T>`），`Parse` 启动期跑一次，逐帧零成本；
- **local 通道**：`EmitLocalSetters` 产物在元素构造期执行一次（Build 阶段代码）；
- 两通道都不产生每 Render 重算——R2 放宽口径（spec/02 §1，2026-11-12）无需再动。

## 6. 发射通道

### 6.1 class 规则（`EustyleGenerator.EmitRule` / `EmitSetterGroup`）

内建属性照旧发 `s.Background(...)` 命名方法；**自定义属性改发泛型逃生口**（`StyleBuilder` 上
没有对应方法）：

```csharp
registry.Define("glowing", s =>
{
    s.Set(global::Probe.BadgeStyles.GlowProperty, 4d);
})
.Hover(s =>
{
    s.Set(global::Probe.BadgeStyles.GlowProperty, 8d);
});
```

判据：`StylePropertyDef.IsBuiltIn`（§4.3）。变体链、分组键 `(Variant, RegisteredState)`、
声明序后者胜——全部不动（规则层与属性来源正交）。

### 6.2 local 直赋（`EmitLocalSetters`）

本来就是字段驱动：`target.SetValue(<目录 field>, <值>)`。只需 `ValueLiteral` 按 §5.2 分派，
field 对自定义属性解析为标注字段的 `global::` 全限定名（`ResolveField` 已按字段名全局找，
标注字段天然命中）。

## 7. 目录合并与冲突规则

| 情形 | 规则 |
|---|---|
| 用户属性与 `StyleBuilder` 内建同名 | 用户声明败，报 **EUI059**（Warning 级不短路？——**否**，取 Error：同名意味着作者意图已被静默吞掉）；目录取内建 |
| 两个用户属性同名（跨程序集） | 报 **EUI059**，两个都不进目录（`ResolveField` 既有「多程序集歧义」口径对齐） |
| 用户属性名与另一个内建属性的资源字段撞（如 `BackgroundResource`） | 不冲突（名字空间不同），照常进目录 |
| 属性名大小写 | 沿用 §4.3：Ordinal 精确匹配，无归一化（EUI051 消息自动列出并集目录） |

## 8. 诊断码分配

| 码 | 名称 | 触发 |
|---|---|---|
| **EUI058** | `StylePropertyDeclarationInvalid` | `[UiStyleProperty]` 字段不合规：类型非 `StyledProperty<T>` 闭包、`T` 含泛型形参、字段非 `public static readonly`、`Name` 解析为空 |
| **EUI059** | `StylePropertyNameConflict` | 样式属性名冲突（用户 vs 内建 / 用户 vs 用户跨程序集） |
| EUI051/052/054 | 沿用 | 未知名（并集目录）、值形态错（按声明分派）、双通道冲突（照旧） |

EUI055–057 已被 IDE 支持与模板契约占用（spec/01 §5 / design/06 §6），058 起顺延。

## 9. 增量缓存与 IDE

- `StylePropertyDef` 追加 `IsBuiltIn`、`OwnerClrName`、`ParseCall`、`Candidates` 字段——
  手写值相等（`CatalogValueEq`）同步更新，**漏写会导致缓存不失效**（design/14 §14.10.3 教训，
  `[UiLiteralParser]` 落地时已踩过：数组按引用比较破坏相等性）；
- `StylePropertyCatalog` 的 `Equals/GetHashCode` 无需结构变化（字段在 def 内）；
- **LSP 零改动项**：属性名候选（`CompletionProvider` 属性名列表读目录）、EUI051 诊断、hover——
  并集目录自动生效；
- **LSP 增量项**：值候选对枚举属性列 `EnumMembers`、对带 `Candidates` 的解析器属性列候选值
  （复用 `[UiLiteralParser]` 在 `CompletionProvider.Values()` 的既有分派，加 keyword/string 分支）。

## 10. 落地切分

| 阶段 | 内容 | 改动面 | 出口标准 |
|---|---|---|---|
| **P1** | `[UiStyleProperty]` + 标注扫描 + enum/string/number 值 + `s.Set` 发射 + EUI058/059 | `UiAttributes.cs`、`StylePropertyCatalogFactory.cs`、`EustyleAst/Parser`（两种新值）、`EustyleGenerator`（ValueLiteral 分派 + EmitRule 判据）、`Diagnostics.cs`、LSP 值候选 | Gallery 加一个自定义属性端到端演示；新测试 ≥10 例；全量测试 0 回归 |
| **P2** | 复用 `[UiLiteralParser]` 支持任意复杂类型（`Thickness.Parse` 等） | 目录 def 预计算分派接 `UiCatalog.FindLiteralParser`；发射期 `Parse` 调用 | `Inset: "8 16 8 16"`（`Thickness.Parse(string)`）端到端 |
| **P3** | 多值语法 `Padding: 8 16 8 16` + `Owner` 适用性诊断（清 design/17 §4.3 待办「`Padding` 写在 `Button` 上静默无效」） | `ParseDeclaration` 多 token 消费 + 声明 arity 约定 + Analyzer 适用性检查 | 多值端到端 + 误用报错 |

P1 预估 ~5 文件 / 200–300 行；P2 主要在目录预计算与测试；P3 动解析器约定，单独评审。

## 11. 前提与风险

- **前提**：`[UiLiteralParser]` 已落地（2026-11-12，见 design/14 §14.7.3 落地记录）——P2 的
  解析器分派、`Pattern` 校验、LSP 候选都是复用；若该先例回退，本提案 P2 需重评。
- **风险 R1**：目录并集扩大后 EUI051 提示列表变长——消息按来源分组（内建在前）缓解。
- **风险 R2**：用户在库 A 与库 B 声明同名属性 → EUI059 双双拒绝可能让无辜方不可用——
  接受（显式 `Name` 改名成本极低，语义可预测优先）。
- **风险 R3**：`Set<T>` 生成代码对自定义属性的类型安全依赖目录推导正确——扫描期对 `T` 做
  非泛型闭包校验（EUI058），发射期零符号依赖，生成产物过 `CSharpSyntaxTree` 语法级校验
  （D104 既有守卫）。

## 12. 规范影响（实施时同步）

| 文档 | 位置 | 改动 |
|---|---|---|
| design/17 | §4.3「单一来源」 | 进料口定义改为「内建 setter + 标注字段并集」，冲突规则表 |
| design/17 | §4.4 值语法 | keyword/string 两种新值 + 分派表 |
| spec/02 | §1 R2 行 | 补「自定义样式属性解析调用在注册期求值一次」一句 |
| spec/02 | §12 台账 | 实施后按片登记（建议 E6：属性自定义化） |
| spec/01 | §5 | 不动（模板字面量与样式值是两条通道，本提案不交叉） |

## 13. 开放问题（实施前拍板）

1. `theme()` 对自定义属性：要求用户同时声明 `XxxResourceProperty` 字段（复用内建约定），还是
   P1/P2 直接不支持？——倾向后者，等有真实需求再立约定。
2. EUI059 取 Error 还是 Warning？——本提案取 Error（§7），若落地时发现迁移痛点再降级。
3. `$var` 是否放开到 string/keyword——P1 不做，P2 视使用反馈。
