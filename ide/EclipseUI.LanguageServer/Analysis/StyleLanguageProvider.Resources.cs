using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;

namespace EclipseUI.LanguageServer.Analysis;

internal sealed partial class StyleLanguageProvider
{
    private INamedTypeSymbol? ResourceType(string name)
    {
        if (_compilation is null) return null;
        if (_compilation.GetTypeByMetadataName(name) is { } type) return type;
        for (var split = name.LastIndexOf('.'); split > 0; split = name.LastIndexOf('.', split - 1))
            if (_compilation.GetTypeByMetadataName(name[..split] + "+" + name[(split + 1)..].Replace('.', '+')) is { } nested)
                return nested;
        return null;
    }
    private static IEnumerable<INamedTypeSymbol> ResourceTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var child in type.GetTypeMembers())
            foreach (var nested in ResourceTypes(child)) yield return nested;
    }
    private bool ResourceMatches(IFieldSymbol field, UiPropDef? property)
    {
        if (_compilation is null || property is null || !field.IsStatic || field.DeclaredAccessibility != Accessibility.Public ||
            !_compilation.IsSymbolAccessibleWithin(field, _compilation.Assembly) || field.Type is not INamedTypeSymbol resource ||
            resource.OriginalDefinition.ToDisplayString() != "EclipseUI.ThemeResource<T>") return false;
        var type = ResourceType(property.DeclaringTypeName.Replace("global::", ""));
        var target = type?.GetMembers(property.PropertyFieldName).OfType<IFieldSymbol>().FirstOrDefault()?.Type as INamedTypeSymbol;
        return target is not null && SymbolEqualityComparer.Default.Equals(target.TypeArguments.FirstOrDefault(), resource.TypeArguments[0]);
    }
    private IEnumerable<(string Name, IFieldSymbol Field)> Resources(UiPropDef? property, string prefix)
    {
        if (_compilation is null || property is null) yield break;
        var types = new List<INamedTypeSymbol>();
        foreach (var import in _document.Imports.Prepend(""))
        {
            INamespaceSymbol? space = _compilation.GlobalNamespace;
            foreach (var segment in import.Split('.', StringSplitOptions.RemoveEmptyEntries))
                space = space?.GetNamespaceMembers().FirstOrDefault(candidate => candidate.Name == segment);
            foreach (var type in space?.GetTypeMembers() ?? []) types.AddRange(ResourceTypes(type));
        }
        var lastDot = prefix.LastIndexOf('.');
        if (lastDot >= 0 && ResourceType(prefix[..lastDot]) is { } explicitType) types.Add(explicitType);
        foreach (var type in types.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            var fullName = type.ToDisplayString();
            var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            var shortName = ns.Length == 0 ? fullName : fullName[(ns.Length + 1)..];
            var qualified = ns.Length > 0 && prefix.StartsWith(ns + ".", StringComparison.Ordinal);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field => ResourceMatches(field, property)))
                yield return ((qualified ? fullName : shortName) + "." + field.Name, field);
        }
    }
    private IFieldSymbol? FindResource(string name, UiPropDef? property)
    {
        var split = name.LastIndexOf('.');
        if (_compilation is null || split <= 0) return null;
        var typeName = name[..split]; var memberName = name[(split + 1)..];
        var types = new[] { typeName }.Concat(_document.Imports.Select(ns => ns + "." + typeName))
            .Select(ResourceType).OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        if (types.Length != 1) return null;
        var field = types[0].GetMembers(memberName).OfType<IFieldSymbol>().SingleOrDefault();
        return field is not null && ResourceMatches(field, property) ? field : null;
    }
    private UiDefinition? ResourceDefinition(IFieldSymbol field)
    {
        var analysis = DocumentAnalysis.Create("@namespace __StyleProbe\n@using EclipseUI.Elements\n<Border />", "StyleProbe.ui", _catalog, _compilation,
            project: _project, workspace: _workspace, documentPath: _document.Path);
        return DefinitionProvider.SymbolDefinition(analysis, field);
    }
}
