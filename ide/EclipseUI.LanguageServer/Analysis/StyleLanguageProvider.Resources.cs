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
        foreach (var ns in _document.EnclosingNamespaces.Concat(_document.Imports).Prepend(""))
        {
            INamespaceSymbol? space = _compilation.GlobalNamespace;
            foreach (var segment in ns.Split('.', StringSplitOptions.RemoveEmptyEntries))
                space = space?.GetNamespaceMembers().FirstOrDefault(candidate => candidate.Name == segment);
            foreach (var type in space?.GetTypeMembers() ?? []) types.AddRange(ResourceTypes(type));
        }
        var lastDot = prefix.LastIndexOf('.');
        if (lastDot >= 0)
            foreach (var visible in VisibleResourceTypes(prefix[..lastDot])) types.Add(visible);
        foreach (var type in types.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            var fullName = type.ToDisplayString();
            var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            var shortName = ns.Length == 0 ? fullName : fullName[(ns.Length + 1)..];
            var qualified = ns.Length > 0 && prefix.StartsWith(ns + ".", StringComparison.Ordinal);
            var nearest = _document.EnclosingNamespaces
                .Select(scope => ResourceType(scope + "." + shortName))
                .FirstOrDefault(candidate => candidate is not null);
            var shadowed = nearest is not null && !SymbolEqualityComparer.Default.Equals(nearest, type);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field => ResourceMatches(field, property)))
                if (qualified || !shadowed)
                    yield return ((qualified ? fullName : shortName) + "." + field.Name, field);
        }
    }
    private IFieldSymbol? FindResource(string name, UiPropDef? property)
    {
        var split = name.LastIndexOf('.');
        if (_compilation is null || split <= 0) return null;
        var typeName = name[..split]; var memberName = name[(split + 1)..];
        var types = VisibleResourceTypes(typeName);
        if (types.Length != 1) return null;
        var field = types[0].GetMembers(memberName).OfType<IFieldSymbol>().SingleOrDefault();
        return field is not null && ResourceMatches(field, property) ? field : null;
    }

    private INamedTypeSymbol[] VisibleResourceTypes(string typeName)
    {
        if (_compilation is null) return [];
        if (typeName.IndexOf('.') >= 0 && ResourceType(typeName) is { } exact) return [exact];
        foreach (var ns in _document.EnclosingNamespaces)
            if (ResourceType(ns + "." + typeName) is { } inScope) return [inScope];
        if (ResourceType(typeName) is { } global) return [global];
        return _document.Imports.Select(ns => ResourceType(ns + "." + typeName))
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
    }
    private UiDefinition? ResourceDefinition(IFieldSymbol field)
    {
        var analysis = DocumentAnalysis.Create("@namespace __StyleProbe\n@using EclipseUI.Elements\n<Border />", "StyleProbe.ui", _catalog, _compilation,
            project: _project, workspace: _workspace, documentPath: _document.Path);
        return DefinitionProvider.SymbolDefinition(analysis, field);
    }
}
