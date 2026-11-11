using System.Linq;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// spec/02 §4.12.6：组件标签上的元素级属性（Class / Style / 附加属性）落到组件根元素上。
///
/// 这组用例走真实编译管线（<c>Fixtures/ComponentTagHost.ui</c> 里的
/// <c>&lt;ComponentTagTarget Class={…} Grid.Row={…} Style="…" /&gt;</c>），钉的是行为而不只是发射文本：
/// 组件不是 <see cref="Element"/>（spec/03 §5.3），标签上的这些属性只能在 Mount 之后写到 Root。
/// </summary>
public class ComponentTagElementAttributeTests
{
    private const string OwnClass = "ComponentTagTarget__own";

    private static (ComponentTagHost Host, WrapPanel Target) Mount()
    {
        var host = new ComponentTagHost();
        var root = new HeadlessRoot(host);
        root.UpdateLayout();
        var target = host.Root.Descendants<WrapPanel>().Single();
        return (host, target);
    }

    [Fact]
    public void AttachedProperty_LandsOnRoot_AndGridPlacesIt()
    {
        var (_, target) = Mount();

        Assert.Equal(1, Grid.GetRow(target));
        // 布局侧真读到同一个值：第 0 行是 Auto（标题行，高度 > 0），组件根落在它下面的 60 行。
        Assert.True(target.FinalRect.Y > 0, $"expected the root below row 0, got y={target.FinalRect.Y}");
        Assert.Equal(60, target.FinalRect.Height);
    }

    [Fact]
    public void Class_MergesIntoTheRootsOwnClasses_OuterLast()
    {
        var (_, target) = Mount();

        // 组件自身在根上声明的类保留，外部类排在末尾（同属性冲突时外部覆盖内部）。
        Assert.Equal(new[] { OwnClass, "wide" }, target.Classes.ToArray());
    }

    [Fact]
    public void Style_WritesTheLocalLayerOfTheRootElement()
    {
        var (_, target) = Mount();

        // local 层压过类层：根自己的 @style 类给 6，标签上的 Style= 给 20。
        Assert.Equal(20, Visual.GetCornerRadius(target));
    }

    [Fact]
    public void DynamicClassAndAttachedProperty_UpdateWithoutAccumulatingClasses()
    {
        var (host, target) = Mount();

        host.outerClass = "narrow";
        host.row = 2;
        host.Invalidate();
        Assert.True(host.ProcessInvalidations());

        Assert.Equal(2, Grid.GetRow(target));
        // 关键：自身类仍然只出现一次，外部类被整体替换（不是每轮追加）。
        Assert.Equal(new[] { OwnClass, "narrow" }, target.Classes.ToArray());
    }
}
