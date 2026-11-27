using System.Linq;
using BubuUI.Elements;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// .bui 派生控件上的 Class / Style / 附加属性直接作用于控件实例。
///
/// 这组用例走真实编译管线（<c>Fixtures/ComponentTagHost.bui</c> 里的
/// <c>&lt;ComponentTagTarget Class={…} Grid.Row={…} Style="…" /&gt;</c>），检查样式和布局的实际行为。
/// </summary>
public class ComponentTagElementAttributeTests
{
    private const string OwnClass = "ComponentTagTarget__own";

    private static (ComponentTagHost Host, WrapPanel Target) Mount()
    {
        var host = new ComponentTagHost();
        var root = new HeadlessRoot(host);
        root.UpdateLayout();
        var target = host.Descendants<WrapPanel>().Single();
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
