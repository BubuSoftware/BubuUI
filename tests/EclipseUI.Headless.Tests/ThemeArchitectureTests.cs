using System.Xml.Linq;
using Path = System.IO.Path;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ThemeArchitectureTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The architecture tests require the source checkout.");
    }

    private static IEnumerable<string> Sources(string directory) => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));

    [Fact]
    public void CoreAndPlatformsDoNotDependOnConcreteThemePackages()
    {
        Assert.DoesNotContain(typeof(Element).Assembly.GetReferencedAssemblies(), assembly => assembly.Name?.StartsWith("EclipseUI.Themes.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(typeof(ElementPainter).Assembly.GetReferencedAssemblies(), assembly => assembly.Name?.StartsWith("EclipseUI.Themes.", StringComparison.Ordinal) == true);
        var source = Path.Combine(RepositoryRoot(), "src");
        foreach (var name in new[] { "EclipseUI.Runtime", "EclipseUI.Rendering", "EclipseUI.Platform.Windows", "EclipseUI.Platform.Linux", "EclipseUI.Platform.MacOS" })
        {
            var directory = Path.Combine(source, name);
            var project = XDocument.Load(Path.Combine(directory, name + ".csproj"));
            var references = project.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference");
            Assert.DoesNotContain(references, reference => reference.Attribute("Include")?.Value.Contains("EclipseUI.Themes.", StringComparison.Ordinal) == true);
            foreach (var file in Sources(directory))
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
                Assert.DoesNotContain(syntax.DescendantNodes().OfType<UsingDirectiveSyntax>(), directive => directive.Name?.ToString().StartsWith("EclipseUI.Themes.", StringComparison.Ordinal) == true);
            }
        }
    }

    [Fact]
    public void InteractiveControlsHaveNoBuiltInSkinDrawing()
    {
        var runtime = Path.Combine(RepositoryRoot(), "src", "EclipseUI.Runtime");
        foreach (var name in new[] { "Controls.cs", "ValueControls.cs", "TextBox.cs", "NavigationControls.cs", "DatePicker.cs", "TreeView.cs", "DataGrid.cs", "ScrollViewer.cs", "ItemsControls.cs", "Popup.cs" })
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(runtime, name))).GetRoot();
            var drawingMethods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText is "Render" or "RenderFocusVisual")
                .Where(method => method.ParameterList.Parameters.Any(parameter => parameter.Type?.ToString() == nameof(IElementDrawingContext)))
                .Select(method => $"{name}: {method.Identifier.ValueText}");
            Assert.Empty(drawingMethods);
        }
    }

    [Fact]
    public void GenericRendererHasNoInteractiveControlSkinDispatch()
    {
        var controls = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Button), nameof(CheckBox), nameof(RadioButton), nameof(ToggleButton), nameof(ToggleSwitch),
            nameof(Slider), nameof(ProgressBar), nameof(TextBox), nameof(PasswordBox), nameof(NumericUpDown),
            nameof(ComboBox), nameof(DatePicker), nameof(EclipseUI.Elements.Calendar), nameof(MenuItem),
            nameof(TreeViewItem), nameof(TabControl), nameof(DataGrid), nameof(ListBoxItem), nameof(ScrollViewer),
        };
        var scene = Path.Combine(RepositoryRoot(), "src", "EclipseUI.Rendering", "SceneGraph");
        foreach (var name in new[] { "ElementPainter.cs", "ElementDrawingContext.cs" })
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(scene, name))).GetRoot();
            Assert.DoesNotContain(syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), identifier => controls.Contains(identifier.Identifier.ValueText));
        }
        var skinShortcuts = new HashSet<string>(StringComparer.Ordinal)
        {
            "ControlSurface", "HasLayeredChrome", "Accent", "OnAccent", "SelectionBackground", "SelectionForeground",
            "MutedForeground", "StrongBorder", "AltBackground", "ThumbBackground",
        };
        Assert.DoesNotContain(typeof(IElementDrawingContext).GetMembers(), member => skinShortcuts.Contains(member.Name));
    }
}
