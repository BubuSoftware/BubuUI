using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;

namespace EclipseUI.Headless.Tests;

internal static class TestHelpers
{
    public static IEnumerable<T> Descendants<T>(this Element root) where T : Element
        => root.Descendants().OfType<T>();

    public static string FullText(this TextBlock block)
        => string.Concat(block.Runs.Select(r => r.Text));
}
