namespace BubuUI;

/// <summary>A typed, reusable theme value evaluated against the element receiving a setter.
/// Compiled styles refer to the resource field directly; no runtime name lookup is involved.</summary>
public sealed class ThemeResource<T>
{
    private readonly Func<Element, T> _resolve;
    public ThemeResource(Func<Element, T> resolve) => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    public T Resolve(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _resolve(element);
    }
}
