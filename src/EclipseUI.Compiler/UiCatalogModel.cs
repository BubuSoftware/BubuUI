using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>标签的来历：控件（<c>Element</c> 派生）还是组件（<c>Component</c> 派生）。属性解析顺序按此分支（design/14 §14.5.3）。</summary>
public enum UiElementOrigin { Control, Component }

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

/// <param name="Required">来自 <c>[UiParameter(Required = true)]</c>，不是 C# <c>required</c> 修饰符。</param>
/// <param name="IsContent">来自 <c>[UiContent]</c> 标在参数属性上（design/14 §14.6.3）：默认内容参数。
/// 类型只接受 <c>IReadOnlyList&lt;Element&gt;</c>（多根）或 <c>Element?</c>（单根），其余在扫描期进
/// EUI021 问题。内容参数 build-once，不可作为 <c>@bind</c> 目标，也不可经 <c>P=</c> 赋值。</param>
/// <param name="DelegateArity">委托参数的调用参数个数（0 = 非委托）。生成包装 lambda 用。</param>
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
    string? ChangedCallbackName);

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
/// 第二组件来源（spec/02 §2.3）：一个 <c>.ui</c> 文件声明的组件。
/// <para>
/// 目录是纯符号扫描，而生成器读不到自己的产物（<c>*.g.cs</c>），所以「这个标签是组件」
/// 在输入编译里只可能有两种证据：code-behind 的基类，或者这个清单。清单由调用方喂入——
/// 生成器从 <c>AdditionalTextsProvider</c>、IDE 项目系统从 <c>AdditionalFiles</c> 取，
/// 两边都是「项目里的 .ui 文件」，因此同源。
/// </para>
/// <para>
/// <paramref name="Namespace"/> 是模板 <c>@namespace</c> 的声明值（缺失时为 null，此时不按
/// 命名空间过滤——缺声明本身由 EUI055 报，不在这里重复）。
/// </para>
/// </summary>
public sealed record UiComponentDeclaration(string Tag, string? Namespace);

/// <summary>
/// 一个模板标签的完整形状。手写 <see cref="Equals(UiElementDef?)"/>：
/// 记录的位置成员里字典/列表按引用比较，会让 §14.10.3 的管道值比较永远判定"已变化"。
/// </summary>
public sealed record UiElementDef(
    string Tag,
    string ClrTypeDisplayName,
    UiElementOrigin Origin,
    UiContentModel Content,
    string? ContentTarget,
    IReadOnlyDictionary<string, UiPropDef> Properties,
    IReadOnlyDictionary<string, UiEventDef> Events,
    IReadOnlyDictionary<string, UiParameterDef> Parameters,
    IReadOnlyDictionary<string, UiSlotDef> Slots,
    IReadOnlyList<UiMetadataProblem> Problems,
    bool IsFromReferencedAssembly = false)
{
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
            && Origin == other.Origin
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
        hash = CatalogValueEq.Add(hash, (int)Origin);
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

    public UiCatalog(
        IEnumerable<UiElementDef> elements,
        IEnumerable<UiHiddenTag> hiddenTags,
        IEnumerable<UiMetadataProblem> problems,
        IEnumerable<UiAttachedPropDef>? attachedProperties = null,
        StylePropertyCatalog? styleProperties = null)
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

    /// <summary>全部 <c>Owner.Prop</c> 键（Ordinal 排序），供未知附加属性的建议算法使用。</summary>
    public IEnumerable<string> AttachedKeys =>
        AttachedProperties.Select(p => $"{p.OwnerTag}.{p.Name}");

    /// <summary>
    /// K22（D85）：按模板 <c>@using</c> 导入的命名空间过滤标签解析。内置控件（EclipseUI.Elements）
    /// 无豁免；只有导入命名空间（或全局命名空间）中的候选可见。目录里有同名标签但全部未导入时
    /// 返回 NotFound 并携带完整类型名列表（供「缺失 @using」建议）；导入后同名多类型仍是 Ambiguous。
    /// </summary>
    public UiTagResolution Resolve(string tag, IReadOnlyList<string>? imports)
    {
        if (tag is null) return new UiTagResolution(UiTagResolutionKind.NotFound, null, null, null);
        if (_byTag.TryGetValue(tag, out var defs))
        {
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
        && StyleProperties.Equals(other.StyleProperties);

    public override bool Equals(object? obj) => Equals(obj as UiCatalog);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Start;
        hash = CatalogValueEq.AddSequence(hash, Elements);
        hash = CatalogValueEq.AddSequence(hash, HiddenTags);
        hash = CatalogValueEq.AddSequence(hash, Problems);
        hash = CatalogValueEq.AddSequence(hash, AttachedProperties);
        return CatalogValueEq.Add(hash, StyleProperties);
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
