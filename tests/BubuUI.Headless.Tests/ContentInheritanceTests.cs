using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class ContentInheritanceTests
{
    [Fact]
    public void UiDerivedControl_PreservesTheCallersDefaultContent()
    {
        using var host = new ContentInheritanceHost();
        var root = new HeadlessRoot(host);
        root.UpdateLayout();

        var text = Assert.IsType<TextBlock>(Assert.Single(host.ExplicitCard.Children));
        Assert.Equal("external content", text.FullText());
        Assert.Same(text, Assert.Single(host.ExplicitCard.Body));
        Assert.Equal(1, host.ExplicitCard.BodyWrites);
    }

    [Fact]
    public void UiDerivedControl_PreservesObjectInitializerContent()
    {
        var text = new TextBlock();
        text.AddRun("from code");
        using var card = new ContentInherited { Body = new Element[] { text } };
        var root = new HeadlessRoot(card);
        root.UpdateLayout();

        Assert.Same(text, Assert.Single(card.Children));
        Assert.Same(text, Assert.Single(card.Body));
        Assert.Equal("from code", text.FullText());
        Assert.Equal(1, card.BodyWrites);
    }

    [Fact]
    public void OmittedContent_DoesNotOverwriteTheCodeRootsDefault()
    {
        using var card = new ContentInherited();
        card.EnsureInitialized();

        var text = Assert.IsType<TextBlock>(Assert.Single(card.Children));
        Assert.Equal("default content", text.FullText());
        Assert.Equal(0, card.BodyWrites);
    }

    [Fact]
    public void OmittedUseSiteContent_DoesNotCallTheInheritedContentSetter()
    {
        using var host = new ContentInheritanceHost();
        host.EnsureInitialized();

        var text = Assert.IsType<TextBlock>(Assert.Single(host.DefaultCard.Children));
        Assert.Equal("default content", text.FullText());
        Assert.Equal(0, host.DefaultCard.BodyWrites);
    }
}
