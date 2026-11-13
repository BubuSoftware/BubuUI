using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>插槽基数（G4 实现，形状先冻结）。</summary>
public enum UiSlotCardinality { Single, Multiple }

/// <summary>
/// 内容模型。数值与运行时 <c>EclipseUI.UiContentKind</c> 一一对应（design/14 §14.4.4/§14.5.2），
/// 测试断言两侧一致；顺序变化会静默错映射，所以显式赋值。
/// </summary>
public enum UiContentModel
{
    None = 0,
    Children = 1,
    Text = 2,
    TextRuns = 3,
}

/// <summary>类型形状：字面量与表达式规则的判定依据。</summary>
public enum UiTypeShapeKind { Primitive, Enum, Delegate, Signal, Other }

/// <summary>标签解析结果（design/14 §14.5.1）。不可用标签带原因，避免把拼错的名字指向抽象类型。</summary>
public enum UiTagResolutionKind
{
    Found,
    NotFound,
    Ambiguous,
    Unavailable,

    /// <summary>K31：标签命中本文件的 <c>@using</c> 别名，但别名目标不在目录里（已报 EUI048）。</summary>
    AliasUnresolved,
}

/// <param name="CanonicalKey">转换器与泛型匹配的唯一查表键：内置类型用 C# 关键字名（<c>double</c>/<c>string</c>/…），
/// 其余用不含 <c>global::</c> 前缀的命名空间限定全名。</param>
/// <param name="EnumMembers">枚举成员名（升序）；非枚举为空。列表成员按引用比较，所以本记录手写结构相等
/// ——目录值必须可比较，否则 §14.10.3 的增量缓存永远判定"已变化"。</param>
public sealed record UiTypeShape(UiTypeShapeKind Kind, string CanonicalKey, IReadOnlyList<string> EnumMembers)
{
    public bool Equals(UiTypeShape? other) =>
        other is not null
        && Kind == other.Kind
        && CanonicalKey == other.CanonicalKey
        && CatalogValueEq.Sequence(EnumMembers, other.EnumMembers);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Add(CatalogValueEq.Start, (int)Kind);
        hash = CatalogValueEq.Add(hash, CanonicalKey);
        return CatalogValueEq.AddSequence(hash, EnumMembers);
    }
}

/// <param name="ClrTypeDisplayName">Roslyn <see cref="SymbolDisplayFormat"/> FullyQualifiedFormat 结果，已含 <c>global::</c>，生成代码直接用。</param>
/// <param name="DeclaringTypeName">属性的声明类型（继承属性为基类），生成代码按该类型引用 <c>{Name}Property</c>。</param>
/// <param name="CanWrite">public setter 且非 <c>init</c>；false 只可能来自引用程序集的异常元数据 → <c>EUI019</c>。</param>
public sealed record UiPropDef(
    string Name,
    string ClrTypeDisplayName,
    string DeclaringTypeName,
    string PropertyFieldName,
    bool CanWrite,
    UiTypeShape TypeShape);

/// <param name="AttributeName">模板属性名（<c>OnClick</c>）。</param>
/// <param name="EventName">去掉 <c>Event</c> 后缀的事件名（<c>Click</c>）。</param>
/// <param name="DeclaringTypeName">声明该字段的元素/组件类型（继承事件为基类）。</param>
/// <param name="ArgsTypeName">当前运行时只有 <c>RoutedEventArgs</c> 一种实参形状（typed args 属 G4）。</param>
public sealed record UiEventDef(
    string AttributeName,
    string EventName,
    string DeclaringTypeName,
    string FieldName,
    string ArgsTypeName);

/// <summary>
/// 附加属性（design/14 §14.5.5，G4 落地）：宿主类声明 <c>public static readonly AttachedProperty&lt;T&gt; XxxProperty</c>
/// 与 <c>Get/Set</c> 方法对，模板写 <c>Owner.Prop="v"</c>。生成代码直调静态 setter，值存储由
/// <c>AttachedProperty&lt;T&gt;</c> 的弱表承担，目录与生成器不引入新存储形态。
/// </summary>
/// <param name="OwnerTag">模板前缀（宿主类型短名，如 <c>Grid</c>）。</param>
/// <param name="Name">属性名（去掉 <c>Property</c> 后缀，如 <c>Row</c>）。</param>
/// <param name="OwnerClrName">宿主类型的全限定名（<c>global::Ns.Grid</c>），生成代码按它调静态方法。</param>
/// <param name="SetterMethod">静态 setter 名（<c>SetRow</c>）；getter 由扫描规则保证存在，生成侧不引用。</param>
/// <param name="ClrTypeDisplayName">值类型 T 的全限定显示名（<c>global::System.Int32</c>）。</param>
/// <param name="TypeShape">值类型形状：字面量转换（<c>UiLiterals</c>）按它分派。</param>
public sealed record UiAttachedPropDef(
    string OwnerTag,
    string Name,
    string OwnerClrName,
    string SetterMethod,
    string ClrTypeDisplayName,
    UiTypeShape TypeShape);

/// <summary>
/// 声明式字面量解析器（design/14 §14.7.3）：带 <c>[UiLiteralParser]</c> 的类型经只读符号
/// 扫描进入本表，<see cref="UiLiterals"/> 的 parser 感知重载按 <see cref="CanonicalKey"/>
/// 查它。手写结构相等：record 默认对 <see cref="Candidates"/> 按引用比较，会让 §14.10.3
/// 的增量缓存永远判定"已变化"（与 <see cref="UiTypeShape"/> 同因）。
/// </summary>
/// <param name="CanonicalKey">目标类型键，与 <see cref="UiTypeShape.CanonicalKey"/> 同一来源。</param>
/// <param name="ParseCall">生成代码用的调用前缀（<c>global::Ns.Type.Parse</c>，已含方法名）。</param>
/// <param name="Pattern">编译期校验字面量的 BCL Regex；null 表示运行时才校验。</param>
/// <param name="Hint">EUI003 的期望格式说明；null 时诊断回显 Pattern。</param>
/// <param name="Candidates">LSP 引号内值候选（Ordinal）；可为空表。</param>
public sealed record UiLiteralParserDef(
    string CanonicalKey,
    string ParseCall,
    string? Pattern,
    string? Hint,
    IReadOnlyList<string> Candidates)
{
    public bool Equals(UiLiteralParserDef? other) =>
        other is not null
        && CanonicalKey == other.CanonicalKey
        && ParseCall == other.ParseCall
        && Pattern == other.Pattern
        && Hint == other.Hint
        && CatalogValueEq.Sequence(Candidates, other.Candidates);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Start;
        hash = CatalogValueEq.Add(hash, CanonicalKey);
        hash = CatalogValueEq.Add(hash, ParseCall);
        hash = CatalogValueEq.Add(hash, Pattern);
        hash = CatalogValueEq.Add(hash, Hint);
        return CatalogValueEq.AddSequence(hash, Candidates);
    }
}

/// <param name="Required">来自 <c>[UiParameter(Required = true)]</c>，不是 C# <c>required</c> 修饰符。</param>
/// <param name="IsContent">来自 <c>[UiContent]</c> 标在参数属性上（design/14 §14.6.3）：默认内容参数。
/// 类型只接受 <c>IReadOnlyList&lt;Element&gt;</c>（多根）或 <c>Element?</c>（单根），其余在扫描期进
/// EUI021 问题。内容参数 build-once，不可作为 <c>@bind</c> 目标，也不可经 <c>P=</c> 赋值。</param>
/// <param name="DelegateArity">委托参数的调用参数个数（0 = 非委托）。生成包装 lambda 用。</param>
/// <param name="ChangedCallbackName">双向通道：同名 <c>{P}Changed</c> 参数且形状正确时回填，否则 null。</param>
/// <param name="ChangedShapeProblem">声明了 <c>{P}Changed</c> 但**形状不对**时的原因（design/14 §14.6.1）。
/// 形状指 <c>Action&lt;T&gt;</c>：恰好一个参数、且类型与 <c>P</c> 相同。生成代码的
/// <c>PChanged = v =&gt; …</c> 里 lambda 的形参类型由目标委托推断，再拿去做
/// <c>EqualityComparer{T}.Default.Equals(source, v)</c> 与赋值——所以两边类型必须同一个，
/// 不是「能隐式转换」就够。null 且 <see cref="ChangedCallbackName"/> 为 null 表示压根没有回调。</param>
public sealed record UiParameterDef(
    string Name,
    string ClrTypeDisplayName,
    UiTypeShape TypeShape,
    bool CanWrite,
    bool Required,
    bool IsDelegate,
    int DelegateArity,
    bool IsContent,
    bool ContentIsMultiple,
    string? ChangedCallbackName,
    string? ChangedShapeProblem = null);

public sealed record UiSlotDef(
    string Name,
    string PropertyName,
    string ClrTypeDisplayName,
    UiSlotCardinality Cardinality);

/// <summary>
/// 目录构建不抛异常：声明不合规的候选进这里（design/14 §14.5.4）。
/// D74 ②：Source 是源项目符号的声明位置（纯数据，可跨增量缓存边界）；
/// 引用程序集符号没有源位置、保持 null，它们只在使用点暴露（design/14 §14.9.2）。
/// </summary>
public sealed record UiMetadataProblem(
    string Code,
    string Message,
    string DeclaringTypeName,
    string? MemberName,
    UiSourceLocation? Source = null);

/// <summary>已进入扫描但不可用的标签，用于使用点诊断与补全过滤。</summary>
public sealed record UiHiddenTag(
    string Tag,
    string ClrTypeName,
    string ReasonCode,
    string Reason);

/// <summary>
/// 一个 .ui 文件声明的生成类型；根标签、命名空间、imports 和 @code 共同构成其符号视图。
/// </summary>
public sealed record UiTemplateDeclaration(string Tag, string? Namespace)
{
    /// <summary>The declared root type is the generated class's base type.</summary>
    public string? RootTag { get; init; }

    /// <summary>Namespace imports and aliases used to resolve the root and code members.</summary>
    public IReadOnlyList<string> Usings { get; init; } = Array.Empty<string>();
    /// <summary>.ui 文件路径（目录侧声明问题的 <see cref="UiSourceLocation"/> 载体）。</summary>
    public string Path { get; init; } = "";

    /// <summary>根层 <c>@code</c> 块（文档顺序）；无 <c>@code</c> 时为空。</summary>
    public IReadOnlyList<UiCodeBlockText> CodeBlocks { get; init; } = Array.Empty<UiCodeBlockText>();

    public bool Equals(UiTemplateDeclaration? other) =>
        other is not null
        && Tag == other.Tag
        && Namespace == other.Namespace
        && Path == other.Path
        && RootTag == other.RootTag
        && CatalogValueEq.Sequence(Usings, other.Usings)
        && CatalogValueEq.Sequence(CodeBlocks, other.CodeBlocks);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Add(CatalogValueEq.Start, Tag);
        hash = CatalogValueEq.Add(hash, Namespace ?? "");
        hash = CatalogValueEq.Add(hash, Path);
        hash = CatalogValueEq.Add(hash, RootTag);
        hash = CatalogValueEq.AddSequence(hash, Usings);
        return CatalogValueEq.AddSequence(hash, CodeBlocks);
    }
}

/// <summary>
/// 一个 <c>@code</code> 块的清单快照（K34/D112）：块体原文 + 块体在 .ui 中的绝对 span +
/// 块体起点的 0 基行列（目录侧把声明问题定位回 .ui 用——块内偏移 + 换行计数即可推出任意
/// 绝对 span 的行列，不需要携带全文）。
/// </summary>
public sealed record UiCodeBlockText(
    string Code, int SpanStart, int SpanLength, int StartLine, int StartCharacter)
{
    public bool Equals(UiCodeBlockText? other) =>
        other is not null && Code == other.Code && SpanStart == other.SpanStart && SpanLength == other.SpanLength
        && StartLine == other.StartLine && StartCharacter == other.StartCharacter;

    public override int GetHashCode() =>
        CatalogValueEq.Add(CatalogValueEq.Add(CatalogValueEq.Add(
            CatalogValueEq.Add(CatalogValueEq.Add(CatalogValueEq.Start, Code), SpanStart), SpanLength),
            StartLine), StartCharacter);
}

/// <summary>
/// 一个模板标签的完整形状。手写 <see cref="Equals(UiElementDef?)"/>：
/// 记录的位置成员里字典/列表按引用比较，会让 §14.10.3 的管道值比较永远判定"已变化"。
/// </summary>
public sealed record UiElementDef(
    string Tag,
    string ClrTypeDisplayName,
    UiContentModel Content,
    string? ContentTarget,
    IReadOnlyDictionary<string, UiPropDef> Properties,
    IReadOnlyDictionary<string, UiEventDef> Events,
    IReadOnlyDictionary<string, UiParameterDef> Parameters,
    IReadOnlyDictionary<string, UiSlotDef> Slots,
    IReadOnlyList<UiMetadataProblem> Problems,
    bool IsFromReferencedAssembly = false)
{
    /// <summary>Only Element-derived objects can occupy element content positions.</summary>
    public bool IsEmbeddable { get; init; } = true;
    public bool CanInherit { get; init; } = true;
    public bool IsTemplate { get; init; }
    public bool ContentIsSingle { get; init; }
    public string? BaseTypeDisplayName { get; init; }
    /// <summary>
    /// 类型上**存在但不能作为 <c>@bind-</c> 目标**的公开成员名 → 原因短语（spec/01 §4.5）。
    /// 与 <see cref="Properties"/> 互补：可样式属性在这里不会出现，出现的一定是「名字认得、绑定不行」，
    /// 使用点因此能把 EUI040（不是绑定目标）与 EUI002（根本没有这个属性）分开报。
    /// 原因短语接在 <c>'{名字}' of '{标签}'</c> 之后成为完整句子（首词小写、无句号）。
    /// </summary>
    public IReadOnlyDictionary<string, string> NonBindableProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="name"/> 不是绑定目标时的原因；名字压根不存在（或就是可绑属性）返回 null。
    /// </summary>
    public string? BindRejection(string name) =>
        NonBindableProperties.TryGetValue(name, out var reason) ? reason : null;

    public bool Equals(UiElementDef? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Tag == other.Tag
            && ClrTypeDisplayName == other.ClrTypeDisplayName
            && IsEmbeddable == other.IsEmbeddable
            && CanInherit == other.CanInherit
            && IsTemplate == other.IsTemplate
            && ContentIsSingle == other.ContentIsSingle
            && BaseTypeDisplayName == other.BaseTypeDisplayName
            && Content == other.Content
            && ContentTarget == other.ContentTarget
            && IsFromReferencedAssembly == other.IsFromReferencedAssembly
            && CatalogValueEq.Map(Properties, other.Properties)
            && CatalogValueEq.Map(Events, other.Events)
            && CatalogValueEq.Map(Parameters, other.Parameters)
            && CatalogValueEq.Map(Slots, other.Slots)
            && CatalogValueEq.Map(NonBindableProperties, other.NonBindableProperties)
            && CatalogValueEq.Sequence(Problems, other.Problems);
    }

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Start;
        hash = CatalogValueEq.Add(hash, Tag);
        hash = CatalogValueEq.Add(hash, ClrTypeDisplayName);
        hash = CatalogValueEq.Add(hash, IsEmbeddable);
        hash = CatalogValueEq.Add(hash, CanInherit);
        hash = CatalogValueEq.Add(hash, IsTemplate);
        hash = CatalogValueEq.Add(hash, ContentIsSingle);
        hash = CatalogValueEq.Add(hash, BaseTypeDisplayName);
        hash = CatalogValueEq.Add(hash, (int)Content);
        hash = CatalogValueEq.Add(hash, ContentTarget);
        hash = CatalogValueEq.Add(hash, IsFromReferencedAssembly);
        hash = CatalogValueEq.AddMap(hash, Properties);
        hash = CatalogValueEq.AddMap(hash, Events);
        hash = CatalogValueEq.AddMap(hash, Parameters);
        hash = CatalogValueEq.AddMap(hash, Slots);
        hash = CatalogValueEq.AddMap(hash, NonBindableProperties);
        return CatalogValueEq.AddSequence(hash, Problems);
    }
}

public readonly record struct UiTagResolution(
    UiTagResolutionKind Kind,
    UiElementDef? Element,
    IReadOnlyList<string>? Candidates,
    UiHiddenTag? Hidden);

/// <summary>
/// 不可变模板目录 + 派生索引。全部成员按 Ordinal 稳定排序，且逐字段实现结构相等，
/// 供增量管道做值比较（design/14 §14.10.3）。
/// </summary>
public sealed class UiCatalog : IEquatable<UiCatalog>
{
    private readonly Dictionary<string, UiElementDef[]> _byTag;
    private readonly Dictionary<string, UiElementDef> _byType;
    private readonly Dictionary<string, UiHiddenTag> _hidden;
    private readonly Dictionary<(string Owner, string Name), UiAttachedPropDef> _attached;
    private readonly Dictionary<string, UiLiteralParserDef> _literalParsers;

    public UiCatalog(
        IEnumerable<UiElementDef> elements,
        IEnumerable<UiHiddenTag> hiddenTags,
        IEnumerable<UiMetadataProblem> problems,
        IEnumerable<UiAttachedPropDef>? attachedProperties = null,
        StylePropertyCatalog? styleProperties = null,
        IEnumerable<UiLiteralParserDef>? literalParsers = null)
    {
        _byTag = elements
            .GroupBy(e => e.Tag, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.ClrTypeDisplayName, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        Elements = _byTag.Values.SelectMany(a => a).OrderBy(e => e.Tag, StringComparer.Ordinal).ToList();
        // K31：@using 别名按 CLR 全名精确查找（别名是显式选择，不受命名空间可见性约束）
        _byType = Elements
            .GroupBy(e => e.ClrTypeDisplayName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _hidden = hiddenTags
            .GroupBy(h => h.Tag, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(h => h.ClrTypeName, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        HiddenTags = _hidden.Values.OrderBy(h => h.Tag, StringComparer.Ordinal).ToList();
        Problems = problems
            .OrderBy(p => p.DeclaringTypeName, StringComparer.Ordinal)
            .ThenBy(p => p.MemberName, StringComparer.Ordinal)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .ToList();
        // 附加属性（design/14 §14.5.5）：Ordinal 排序；同 (Owner, Name) 冲突由扫描器报问题，
        // 此处第一个（排序后）胜出，保证目录结构确定。
        _attached = new Dictionary<(string, string), UiAttachedPropDef>();
        foreach (var prop in (attachedProperties ?? Array.Empty<UiAttachedPropDef>())
            .OrderBy(p => p.OwnerTag, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!_attached.ContainsKey((prop.OwnerTag, prop.Name)))
                _attached[(prop.OwnerTag, prop.Name)] = prop;
        }
        AttachedProperties = _attached.Values
            .OrderBy(p => p.OwnerTag, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        // 声明式字面量解析器（design/14 §14.7.3）：Ordinal 按 CanonicalKey 排序；
        // 同 CanonicalKey 冲突由扫描器报问题，此处第一个（排序后）胜出，保证目录结构确定
        //（与 _attached 同一口径）。
        _literalParsers = new Dictionary<string, UiLiteralParserDef>(StringComparer.Ordinal);
        foreach (var parser in (literalParsers ?? Array.Empty<UiLiteralParserDef>())
            .OrderBy(p => p.CanonicalKey, StringComparer.Ordinal))
        {
            if (!_literalParsers.ContainsKey(parser.CanonicalKey))
                _literalParsers[parser.CanonicalKey] = parser;
        }
        LiteralParsers = _literalParsers.Values
            .OrderBy(p => p.CanonicalKey, StringComparer.Ordinal)
            .ToList();
        // 编译期派生的可样式属性表面（design/17 §4.3）：与标签表面同一次符号扫描产出，
        // 随 UiCatalog 一起走增量缓存；缺省空目录仅供无符号语境的静态前端。
        StyleProperties = styleProperties ?? StylePropertyCatalog.Empty;
    }

    /// <summary>可样式属性目录（StyleBuilder setter + StyledProperty 字段派生，单一来源）。</summary>
    public StylePropertyCatalog StyleProperties { get; }

    public IReadOnlyList<UiElementDef> Elements { get; }

    public IReadOnlyList<UiHiddenTag> HiddenTags { get; }

    public IReadOnlyList<UiMetadataProblem> Problems { get; }

    /// <summary>附加属性表面（design/14 §14.5.5）：Ordinal 按 OwnerTag/Name 排序。</summary>
    public IReadOnlyList<UiAttachedPropDef> AttachedProperties { get; }

    /// <summary>声明式字面量解析器表面（design/14 §14.7.3）：Ordinal 按 CanonicalKey 排序。</summary>
    public IReadOnlyList<UiLiteralParserDef> LiteralParsers { get; }

    public IEnumerable<string> ElementTags => _byTag.Keys.OrderBy(t => t, StringComparer.Ordinal);

    public UiTagResolution Resolve(string tag)
    {
        if (tag is null) return new UiTagResolution(UiTagResolutionKind.NotFound, null, null, null);
        if (_byTag.TryGetValue(tag, out var defs))
        {
            if (defs.Length == 1)
                return new UiTagResolution(UiTagResolutionKind.Found, defs[0], null, null);
            return new UiTagResolution(
                UiTagResolutionKind.Ambiguous,
                null,
                defs.Select(d => d.ClrTypeDisplayName).ToList(),
                null);
        }
        if (_hidden.TryGetValue(tag, out var hidden))
            return new UiTagResolution(UiTagResolutionKind.Unavailable, null, null, hidden);
        return new UiTagResolution(UiTagResolutionKind.NotFound, null, null, null);
    }

    /// <summary>
    /// K31：按 CLR 显示名（<c>global::Ns.Type</c>）精确解析，供 <c>@using</c> 别名使用。
    /// 别名是模板里的显式选择，因此**不做** @using 命名空间可见性过滤——这正是它解决重名的方式。
    /// </summary>
    public UiElementDef? FindByTypeDisplayName(string clrTypeDisplayName) =>
        _byType.TryGetValue(clrTypeDisplayName, out var def) ? def : null;

    /// <summary>按 <c>Owner.Prop</c> 两段查附加属性（design/14 §14.5.5）；名中含更多点号时调用方先行拒绝。</summary>
    public UiAttachedPropDef? FindAttached(string owner, string name) =>
        _attached.TryGetValue((owner, name), out var def) ? def : null;

    /// <summary>按目标类型键查声明式字面量解析器（design/14 §14.7.3）；未注册返回 null。</summary>
    public UiLiteralParserDef? FindLiteralParser(string canonicalKey) =>
        _literalParsers.TryGetValue(canonicalKey, out var parser) ? parser : null;

    /// <summary>全部 <c>Owner.Prop</c> 键（Ordinal 排序），供未知附加属性的建议算法使用。</summary>
    public IEnumerable<string> AttachedKeys =>
        AttachedProperties.Select(p => $"{p.OwnerTag}.{p.Name}");

    /// <summary>
    /// K22（D85）+ K33（D111）：按「<c>@namespace</c> 链（隐式可见、内层独占）→ <c>@using</c>
    /// 导入命名空间（或全局命名空间）」过滤标签解析，内置控件（EclipseUI.Elements）无豁免。
    /// 链上与导入命名空间都不可见时返回 NotFound 并携带完整类型名列表（供「缺失 @using」建议）；
    /// 导入后同名多类型仍是 Ambiguous。
    /// </summary>
    /// <param name="enclosingNamespaces">
    /// K33（D111）：<c>@namespace</c> 的 C# 名称查找链，最内层在前（见
    /// <see cref="TemplateAnalyzer.EnclosingNamespaces"/>）。这一链是**隐式可见**的，且按 C# 规则
    /// **优先于 @using**——内层先命中就独占，不再与外层或导入命名空间的同名候选合并成 EUI016。
    /// 空/null 表示没有命名空间声明，退回纯 <c>@using</c> 口径。
    /// </param>
    public UiTagResolution Resolve(string tag, IReadOnlyList<string>? imports,
        IReadOnlyList<string>? enclosingNamespaces)
    {
        if (tag is null) return new UiTagResolution(UiTagResolutionKind.NotFound, null, null, null);
        if (_byTag.TryGetValue(tag, out var defs))
        {
            // ① C# 简单名查找：当前命名空间 → 逐级外层。命中的那一层独占，因为 C# 里外层/using
            //    的同名类型在内层已声明同名类型时根本不可见。
            if (enclosingNamespaces is { Count: > 0 })
            {
                foreach (var ns in enclosingNamespaces)
                {
                    var inScope = defs
                        .Where(d => string.Equals(TemplateAnalyzer.NamespaceOfClrType(d.ClrTypeDisplayName), ns,
                            StringComparison.Ordinal))
                        .ToArray();
                    // 同一命名空间里出现同名两类型只能是跨程序集重复声明（EUI016 的既有语义）
                    if (inScope.Length == 1) return new UiTagResolution(UiTagResolutionKind.Found, inScope[0], null, null);
                    if (inScope.Length > 1)
                        return new UiTagResolution(UiTagResolutionKind.Ambiguous, null,
                            inScope.Select(d => d.ClrTypeDisplayName).ToList(), null);
                }
            }
            // ② 退回 @using 口径
            // 可见性统一由 NamespaceVisible 决定：global 命名空间类型不写 @using 也可见，
            // 具名命名空间类型必须已导入（K22）。imports 为空时具名类型自然全部不可见。
            var visible = defs
                .Where(d => NamespaceVisible(d.ClrTypeDisplayName, imports ?? Array.Empty<string>()))
                .ToArray();
            if (visible.Length == 1)
                return new UiTagResolution(UiTagResolutionKind.Found, visible[0], null, null);
            if (visible.Length > 1)
                return new UiTagResolution(
                    UiTagResolutionKind.Ambiguous,
                    null,
                    visible.Select(d => d.ClrTypeDisplayName).ToList(),
                    null);
            // 全部未导入：NotFound + 候选类型名（缺 @using 建议），不静默降级成别的状态
            return new UiTagResolution(
                UiTagResolutionKind.NotFound,
                null,
                defs.Select(d => d.ClrTypeDisplayName).ToList(),
                null);
        }
        if (_hidden.TryGetValue(tag, out var hiddenTag))
            return new UiTagResolution(UiTagResolutionKind.Unavailable, null, null, hiddenTag);
        return new UiTagResolution(UiTagResolutionKind.NotFound, null, null, null);
    }

    private static bool NamespaceVisible(string clrTypeDisplayName, IReadOnlyList<string> imports)
    {
        var ns = TemplateAnalyzer.NamespaceOfClrType(clrTypeDisplayName);
        // 全局命名空间类型没有 @using 可写：始终可见
        return ns is null || imports.Contains(ns, StringComparer.Ordinal);
    }

    public bool Equals(UiCatalog? other) =>
        other is not null
        && CatalogValueEq.Sequence(Elements, other.Elements)
        && CatalogValueEq.Sequence(HiddenTags, other.HiddenTags)
        && CatalogValueEq.Sequence(Problems, other.Problems)
        && CatalogValueEq.Sequence(AttachedProperties, other.AttachedProperties)
        && CatalogValueEq.Sequence(LiteralParsers, other.LiteralParsers)
        && StyleProperties.Equals(other.StyleProperties);

    public override bool Equals(object? obj) => Equals(obj as UiCatalog);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Start;
        hash = CatalogValueEq.AddSequence(hash, Elements);
        hash = CatalogValueEq.AddSequence(hash, HiddenTags);
        hash = CatalogValueEq.AddSequence(hash, Problems);
        hash = CatalogValueEq.AddSequence(hash, AttachedProperties);
        hash = CatalogValueEq.AddSequence(hash, LiteralParsers);
        hash = CatalogValueEq.Add(hash, StyleProperties);
        return hash;
    }
}

/// <summary>
/// 结构相等助手：集合成员按内容比较，缺失集合按空处理。
/// 哈希用 unchecked 累加（netstandard2.0 没有 <c>System.HashCode</c>）。
/// </summary>
internal static class CatalogValueEq
{
    public const int Start = 17;

    public static bool Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? a, IReadOnlyDictionary<TKey, TValue>? b)
        where TKey : notnull
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        var valueComparer = EqualityComparer<TValue>.Default;
        foreach (var pair in a)
            if (!b.TryGetValue(pair.Key, out var other) || !valueComparer.Equals(pair.Value, other))
                return false;
        return true;
    }

    public static bool Sequence<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Count; i++)
            if (!comparer.Equals(a[i], b[i]))
                return false;
        return true;
    }

    public static int Add(int hash, object? value) => unchecked(hash * 397 + (value?.GetHashCode() ?? 0));

    public static int AddMap<TValue>(int hash, IReadOnlyDictionary<string, TValue>? map)
    {
        if (map is null) return Add(hash, 0);
        hash = Add(hash, map.Count);
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            hash = Add(hash, key);
            hash = Add(hash, map[key]);
        }
        return hash;
    }

    public static int AddSequence<T>(int hash, IReadOnlyList<T>? sequence)
    {
        if (sequence is null) return Add(hash, 0);
        hash = Add(hash, sequence.Count);
        foreach (var item in sequence)
            hash = Add(hash, item);
        return hash;
    }
}
