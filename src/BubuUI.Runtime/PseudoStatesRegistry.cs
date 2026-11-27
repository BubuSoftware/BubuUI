namespace BubuUI;

/// <summary>
/// 注册制伪类（E5，design/17 §3.2）：第三方/控件自定义状态位（<c>:loading</c>、<c>:busy</c>…）的
/// 启动期注册表。字符串只存在于注册时刻，运行期全是掩码测试——注册位直接 OR 进
/// <see cref="Element.PseudoStates"/> 掩码与绘制段键，段缓存机制零改动。
/// <para>
/// <see cref="Register"/> 幂等：同名重复注册返回同一掩码——样式表生成代码（变体链实参内联
/// <c>Register("name")</c>）与宿主 C# 各自调用互不干扰。写入权受控：合法位只能来自
/// <see cref="Register"/> 分配（掩码即凭证），<see cref="Element.SetRegisteredState"/> 拒绝
/// 一切未注册/平台位掩码。
/// </para>
/// </summary>
public static class PseudoStatesRegistry
{
    /// <summary>平台保留名（撞名注册即启动期错误）：六个枚举位 + <c>active</c>（pressed 的 CSS 别名）。</summary>
    private static readonly string[] PlatformNames =
        ["hover", "pressed", "active", "focus", "focus-visible", "focus-within", "disabled"];

    private static readonly Dictionary<string, PseudoStates> Registered = new(StringComparer.Ordinal);

    /// <summary>下一个可分配位（平台六位的后一位起）。位 31 是符号位，不参与分配。</summary>
    private static int _nextBit = (int)PseudoStates.FocusWithin << 1;

    /// <summary>全部已注册位的并集（<see cref="Element.SetRegisteredState"/> 的合法性校验源）。</summary>
    private static PseudoStates _registeredBits;

    private static readonly object Gate = new();

    /// <summary>
    /// 注册一个自定义伪类状态名并返回其掩码位（启动期调用，幂等）。名称规范化（去前导
    /// <c>:</c>）、小写敏感（Ordinal）；与平台位撞名（hover/pressed/active/focus/focus-visible/focus-within/disabled）
    /// 或名称非法立即抛出——启动期 fail fast，不留运行期静默。位从 <see cref="PseudoStates.FocusWithin"/>
    /// 后一位自增分配；int 掩码可容纳 25 个注册位（design/17 §8：不够升 long，段键同步扩字段）。
    /// </summary>
    public static PseudoStates Register(string name)
    {
        var normalized = Normalize(name);
        lock (Gate)
        {
            if (Registered.TryGetValue(normalized, out var existing))
                return existing;
            if (_nextBit <= 0)
                throw new InvalidOperationException(
                    $"no free pseudo-state bits left for '{normalized}'; the int mask holds 25 " +
                    "registered states — promote PseudoStates to long (design/17 §8) when needed");
            var mask = (PseudoStates)_nextBit;
            _nextBit <<= 1;
            _registeredBits |= mask;
            Registered[normalized] = mask;
            return mask;
        }
    }

    /// <summary>名字是否已注册。</summary>
    public static bool IsRegistered(string name) =>
        Registered.ContainsKey(Normalize(name));

    /// <summary>已注册名字的掩码；未注册返回 <see cref="PseudoStates.None"/>。</summary>
    public static PseudoStates GetMask(string name) =>
        Registered.TryGetValue(Normalize(name), out var mask) ? mask : PseudoStates.None;

    /// <summary>掩码是否是「已注册位的非空子集」——平台位与任意伪造位都被拒绝。</summary>
    internal static bool IsRegisteredMask(PseudoStates mask) =>
        mask != PseudoStates.None && (mask & _registeredBits) == mask;

    /// <summary>规范化：去前导 ':'、去首尾空白、拒绝空名与非法字符（Ident 字符集）。</summary>
    private static string Normalize(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        var normalized = name.Trim().TrimStart(':');
        if (normalized.Length == 0)
            throw new ArgumentException("pseudo-state name must not be empty.", nameof(name));
        foreach (var c in normalized)
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '_')
                throw new ArgumentException(
                    $"pseudo-state name '{name}' contains invalid character '{c}'; " +
                    "use letters, digits, '-' or '_'.", nameof(name));
        if (Array.IndexOf(PlatformNames, normalized) >= 0)
            throw new ArgumentException(
                $"'{normalized}' is a platform pseudo-class (hover/pressed/active/focus/focus-visible/focus-within/disabled); " +
                "registered names must not shadow platform states.", nameof(name));
        return normalized;
    }
}
