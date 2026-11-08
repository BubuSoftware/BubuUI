using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI;

/// <summary>
/// 焦点系统：Tab 遍历按树序（深度优先）环形推进；
/// 键盘事件从焦点元素路由（无焦点时从根）。
/// </summary>
public sealed class FocusManager
{
    private readonly Element _root;

    public FocusManager(Element root) => _root = root;

    public Element? Focused { get; private set; }

    public IReadOnlyList<Element> Focusables
        => _root.Descendants().Where(e => e.IsFocusable).ToList();

    public bool Focus(Element? element)
    {
        if (element is null || !element.IsFocusable) return false;
        if (ReferenceEquals(Focused, element)) return true;

        var old = Focused;
        if (old is not null)
        {
            old.InvalidateRender();
            old.RaiseEvent(new RoutedEventArgs(Element.LostFocusEvent) { Source = old });
        }
        Focused = element;
        element.InvalidateRender();
        element.RaiseEvent(new RoutedEventArgs(Element.GotFocusEvent) { Source = element });
        return true;
    }

    public void ClearFocus()
    {
        var old = Focused;
        if (old is null) return;
        Focused = null;
        old.InvalidateRender();
        old.RaiseEvent(new RoutedEventArgs(Element.LostFocusEvent) { Source = old });
    }

    /// <summary>
    /// 焦点元素已不在本管理器的根子树内（被 Unmount/摘除）时清除焦点并广播 LostFocus。
    /// 宿主在每帧布局前调用；返回是否发生了清除。
    /// </summary>
    public bool ClearFocusIfDetached()
    {
        var focused = Focused;
        if (focused is null) return false;
        for (Element? node = focused; node is not null; node = node._parent)
            if (ReferenceEquals(node, _root))
                return false;
        ClearFocus();
        return true;
    }

    /// <summary>移到下一个可聚焦元素（环形）。Tab 键的默认实现。</summary>
    public Element? MoveNext()
    {
        var focusables = Focusables;
        if (focusables.Count == 0) return null;
        int index = -1;
        for (int i = 0; i < focusables.Count; i++)
        {
            if (ReferenceEquals(focusables[i], Focused))
            {
                index = i;
                break;
            }
        }
        var next = focusables[(index + 1) % focusables.Count];
        Focus(next);
        return next;
    }
}
