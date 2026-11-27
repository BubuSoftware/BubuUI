using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class TemplateProjectionTests
{
    [Fact]
    public void RejectedManualTemplateDoesNotStealContentFromTheCurrentPresenter()
    {
        var content = new TextBox { Text = "keep" };
        using var owner = new Button { Content = content };
        owner.Template = new FuncControlTemplate<Button>(button => new TemplateInstance(new ContentPresenter { Content = button.Content }));
        using var host = new HeadlessRoot(owner);
        host.UpdateLayout();
        var original = Assert.IsType<ContentPresenter>(owner.TemplateRoot);
        host.Focus.Focus(content);
        Assert.Throws<InvalidOperationException>(() => owner.Template = new FuncControlTemplate<Button>(button =>
        {
            var root = new Grid();
            root.Children.Add(new ContentPresenter { Part = "duplicate", Content = button.Content });
            root.Children.Add(new Border { Part = "duplicate" });
            return new TemplateInstance(root);
        }));
        Assert.Same(original, owner.TemplateRoot);
        Assert.Same(content, Assert.Single(original.Children));
        Assert.Same(original, content.Parent);
        Assert.Same(content, host.Focus.Focused);
        Assert.False(content.IsDisposed);
        owner.ClearLocalValue(Element.TemplateProperty);
    }

    [Fact]
    public void RemovingHookFailureDoesNotTryToAttachTheCurrentRootTwice()
    {
        using var owner = new RemovingProbe { Template = new FuncControlTemplate<RemovingProbe>(_ => new TemplateInstance(new Grid())) };
        using var host = new HeadlessRoot(owner);
        host.UpdateLayout();
        var original = owner.TemplateRoot;
        owner.FailRemoving = true;
        var error = Assert.Throws<InvalidOperationException>(() => owner.Template = new FuncControlTemplate<RemovingProbe>(_ => new TemplateInstance(new Grid())));
        Assert.Equal("removal failed", error.Message);
        Assert.Same(original, owner.TemplateRoot);
        Assert.Same(owner, original!.Parent);
        Assert.False(original.IsDisposed);
        owner.FailRemoving = false;
        owner.ClearLocalValue(Element.TemplateProperty);
    }

    private sealed class RemovingProbe : TemplatedControl
    {
        public bool FailRemoving { get; set; }
        protected override void OnTemplateRemoving()
        {
            if (FailRemoving) throw new InvalidOperationException("removal failed");
            base.OnTemplateRemoving();
        }
    }

    private static ControlTemplate Project() => new FuncControlTemplate<StackPanel>(owner =>
    {
        var root = new Grid();
        root.Children.Add(new ItemsPresenter { Owner = owner });
        return new TemplateInstance(root);
    });

    [Fact]
    public void ProjectedItemsRemainMutableAndKeepFocusWhenTemplateChanges()
    {
        using var owner = new StackPanel { Template = Project() };
        var first = new TextBox { Text = "keep", Width = 100, Height = 30 };
        owner.Children.Add(first);
        using var host = new HeadlessRoot(owner);
        host.UpdateLayout();
        var presenter = Assert.Single(owner.Descendants().OfType<ItemsPresenter>());
        Assert.Same(presenter, first.Parent);
        Assert.Same(owner, first.ContentParent);
        host.Focus.Focus(first);
        var second = new Button { Width = 100, Height = 20 };
        owner.Children.Add(second);
        host.UpdateLayout();
        Assert.Equal(2, presenter.VisualChildren.Count);
        Assert.Same(presenter, second.Parent);
        var old = owner.TemplateRoot!;
        owner.Template = Project();
        host.UpdateLayout();
        Assert.True(old.IsDisposed);
        Assert.Same(first, host.Focus.Focused);
        Assert.Equal("keep", first.Text);
        Assert.False(first.IsDisposed);
        Assert.Same(first, owner.Children[0]);
        Assert.True(owner.Children.Remove(second));
        Assert.Null(second.Parent);
        Assert.False(second.IsDisposed);
        second.Dispose();
        owner.Template = null;
        host.UpdateLayout();
        Assert.Null(owner.TemplateRoot);
        Assert.Same(owner, first.Parent);
        Assert.Same(first, host.Focus.Focused);
    }

    [Fact]
    public void DisposingPresenterReturnsBorrowedItemsToTheirOwner()
    {
        using var owner = new StackPanel { Template = Project() };
        var content = new TextBox { Text = "borrowed" };
        owner.Children.Add(content);
        using var host = new HeadlessRoot(owner);
        host.UpdateLayout();
        Assert.Single(owner.Descendants().OfType<ItemsPresenter>()).Dispose();
        Assert.False(content.IsDisposed);
        Assert.Same(owner, content.Parent);
        Assert.Same(content, Assert.Single(owner.Children));
        owner.Template = null;
        host.UpdateLayout();
        owner.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public void CompetingPresentersRollbackWithoutLosingItems()
    {
        using var owner = new StackPanel { Template = Project() };
        var content = new TextBox { Text = "value" };
        owner.Children.Add(content);
        using var host = new HeadlessRoot(owner);
        host.UpdateLayout();
        var old = owner.TemplateRoot;
        var error = Assert.Throws<AggregateException>(() => owner.Template = new FuncControlTemplate<StackPanel>(panel =>
        {
            var root = new Grid();
            root.Children.Add(new ItemsPresenter { Owner = panel });
            root.Children.Add(new ItemsPresenter { Owner = panel });
            return new TemplateInstance(root);
        }));
        Assert.IsType<InvalidOperationException>(Assert.Single(error.Flatten().InnerExceptions));
        Assert.Same(old, owner.TemplateRoot);
        Assert.False(content.IsDisposed);
        Assert.Same(content, Assert.Single(owner.Children));
        Assert.Same(Assert.Single(owner.Descendants().OfType<ItemsPresenter>()), content.Parent);
        owner.ClearLocalValue(Element.TemplateProperty);
    }
}
