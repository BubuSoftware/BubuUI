using System.Linq;
using EclipseUI.Elements;
using Gallery;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class UnifiedControlTests
{
    private sealed class CodeCounter : Counter
    {
        public void IncrementFromCode()
        {
            count++;
            Invalidate();
        }
    }

    [Fact]
    public void GeneratedRoot_IsTheControlInstanceUsedByTheHost()
    {
        var counter = new Counter();
        var root = new HeadlessRoot(counter);

        Assert.Equal(typeof(StackPanel), typeof(Counter).BaseType);
        Assert.Same(counter, root.Content);
        Assert.NotEmpty(counter.Children);
    }

    [Fact]
    public void UiAndCodeControls_ComposeAndUpdateThroughTheSameTree()
    {
        var host = new UnifiedHost();
        var root = new HeadlessRoot(host);
        root.UpdateLayout();

        Assert.Same(host, root.Content);
        Assert.Same(host, host.Card.Parent);
        Assert.Same(host, host.CounterView.Parent);
        Assert.Equal(11, host.Card.Gap);

        host.Card.Trigger.Click();
        var toggle = host.CounterView.Descendants<CheckBox>().Single();
        toggle.Toggle();
        root.UpdateLayout();
        host.CounterView.Descendants<Button>().Single().Click();
        root.UpdateLayout();

        Assert.Equal("code: 1", host.Card.Output.FullText());
        Assert.Equal("You pressed 1 times", host.CounterView.Descendants<TextBlock>().Single().FullText());
    }

    [Fact]
    public void UiCanDeriveFromAPureCodeControl_WithoutReplacingItsInstance()
    {
        var control = new CodeRoot();
        var root = new HeadlessRoot(control);
        root.UpdateLayout();

        Assert.Equal(typeof(CodeCard), typeof(CodeRoot).BaseType);
        Assert.Same(control, root.Content);
        Assert.Equal(13, control.Gap);
        Assert.Same(control, control.Trigger.Parent);
        control.Trigger.Click();
        root.UpdateLayout();
        Assert.Equal("code: 1", control.Output.FullText());
    }

    [Fact]
    public void CallerProperties_TakePrecedenceOverTemplateDefaults()
    {
        var counter = new Counter { Gap = 21 };

        counter.EnsureInitialized();

        Assert.Equal(21, counter.Gap);
    }

    [Fact]
    public void CallerClasses_MergeAfterTheTemplatesOwnClasses()
    {
        var control = new ComponentTagTarget { Classes = new[] { "outside" } };

        control.EnsureInitialized();

        Assert.Equal(new[] { "ComponentTagTarget__own", "outside" }, control.Classes);
    }

    [Fact]
    public void PureCodeCanDeriveFromAUiControl_AndUseItsGeneratedUpdates()
    {
        var control = new CodeCounter();
        var root = new HeadlessRoot(control);

        control.IncrementFromCode();
        root.UpdateLayout();

        Assert.Equal("You pressed 1 times", control.Descendants<TextBlock>().Single().FullText());
    }

    [Fact]
    public void RootInputs_AreReadyBeforeTheCodeBaseConsumesThem()
    {
        var control = new RootInputs();
        _ = new HeadlessRoot(control);

        Assert.Equal("from ui", control.CapturedTitle);
        var content = Assert.Single(control.Body!);
        Assert.Same(content, Assert.Single(control.Children));
        Assert.Equal("body from ui", Assert.IsType<TextBlock>(content).FullText());
    }

    [Fact]
    public void RootSlots_AreReadyBeforeTheCodeBaseConsumesThem()
    {
        var control = new RootSlots();
        _ = new HeadlessRoot(control);

        Assert.Same(control.Header, Assert.Single(control.Children));
        Assert.Equal("header from ui", Assert.IsType<TextBlock>(control.Header).FullText());
    }
}
