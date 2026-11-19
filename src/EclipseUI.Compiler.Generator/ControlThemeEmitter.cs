using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EclipseUI.Compiler.Generator;

/// <summary>Binds theme setters to the same property identities and literal converters as .ui.</summary>
internal sealed class ControlThemeEmitter(UiCatalog catalog, IReadOnlyList<UiTemplateDeclaration> templates, Compilation compilation)
{
    private readonly Dictionary<string, (string Type, string Field)> _themes = new(StringComparer.Ordinal);
    private static string Quote(string value) => SymbolDisplay.FormatLiteral(value, true);
    private static string Field(UiPropDef property) => property.DeclaringTypeName + "." + property.PropertyFieldName;
    private readonly StringBuilder _fields = new();
    private readonly StringBuilder _apply = new();
    private readonly List<UiDiagnostic> _diagnostics = new();
    private IReadOnlyList<string> _imports = Array.Empty<string>();
    private IReadOnlyList<string> _enclosingNamespaces = Array.Empty<string>();
    public string Fields => _fields.ToString();
    public string Apply => _apply.ToString();

    public void Declare(ThemeStyleDocument doc)
    {
        SetScope(doc);
        foreach (var node in doc.Nodes.Where(n => n.Header.StartsWith("@theme ", StringComparison.Ordinal)))
        {
            var header = Regex.Match(node.Header, @"^@theme\s+([A-Za-z_]\w*)\s+for\s+([A-Za-z_]\w*)$");
            if (!header.Success) continue;
            var type = Resolve(header.Groups[2].Value);
            if (type is not null && !_themes.ContainsKey(header.Groups[1].Value))
                _themes.Add(header.Groups[1].Value, (type.ClrTypeDisplayName, header.Groups[1].Value));
        }
    }
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    public IReadOnlyList<UiDiagnostic> Emit(ThemeStyleDocument doc)
    {
        _diagnostics.Clear(); SetScope(doc);
        foreach (var node in doc.Nodes)
        {
            if (node.Header.StartsWith("@theme ", StringComparison.Ordinal)) EmitTheme(node);
            else EmitStyle(node);
        }
        return _diagnostics.ToArray();
    }
    private void SetScope(ThemeStyleDocument doc)
    {
        _imports = doc.Imports;
        _enclosingNamespaces = TemplateAnalyzer.EnclosingNamespaces(doc.Namespace);
    }

    private UiElementDef? Resolve(string name) => catalog.Resolve(name, _imports, _enclosingNamespaces).Element;
    private void Error(ThemeStyleNode node, string message) => _diagnostics.Add(UiDiagnostics.Error("EUI050", message, node.Span));

    private void EmitTheme(ThemeStyleNode node)
    {
        var header = Regex.Match(node.Header, @"^@theme\s+([A-Za-z_]\w*)\s+for\s+([A-Za-z_]\w*)$");
        if (!header.Success) { Error(node, "Expected @theme Name for ControlType { ... }"); return; }
        var name = header.Groups[1].Value;
        if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || name is "Apply" or "ApplyControlThemes" or "StyleBootstrap")
        { Error(node, "Control theme name conflicts with a generated member or is not a valid C# identifier."); return; }
        var target = Resolve(header.Groups[2].Value);
        if (target is null || !target.Properties.ContainsKey("Template")) { Error(node, "Theme target must be a visible Element type."); return; }
        if (!_emitted.Add(name)) { Error(node, $"Duplicate control theme '{name}'."); return; }
        var templateNodes = node.Children.Where(n => n.Header == "Template" && n.Value is not null).ToArray();
        if (templateNodes.Length != 1) { Error(node, "A control theme requires exactly one Template: template(Type) declaration."); return; }
        var match = Regex.Match(templateNodes[0].Value!, @"^template\(([\w.]+)\)$");
        if (!match.Success) { Error(templateNodes[0], "Expected template(TemplateType)."); return; }
        var templateName = match.Groups[1].Value;
        string? templateType = ResolveTemplate(templateName);
        if (templateType is null)
        {
            var symbol = ResolveVisibleType(templateName);
            if (symbol is not null) templateType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        if (templateType is null) { Error(templateNodes[0], $"Unknown or ambiguous template '{templateName}'; declare its namespace with @namespace or import it with @using."); return; }
        var templateSymbol = compilation.GetTypeByMetadataName(templateType.Substring(8));
        var templateBase = templateSymbol?.BaseType;
        var targetSymbol = compilation.GetTypeByMetadataName(target.ClrTypeDisplayName.Substring(8));
        if (templateBase?.OriginalDefinition.ToDisplayString() != "EclipseUI.ControlTemplate<T>" ||
            !Assignable(targetSymbol, templateBase.TypeArguments.FirstOrDefault()))
        { Error(templateNodes[0], "Template target is incompatible with this control theme."); return; }

        var rules = new List<string>();
        var defaults = node.Children.Where(n => (n.Value is not null && n.Header != "Template") || n.Header.StartsWith("@transition ", StringComparison.Ordinal)).ToArray();
        rules.Add(Rule("global::EclipseUI.PseudoStates.None", "null", Setters(defaults, target)));
        foreach (var child in node.Children.Where(n => n.Value is null && !n.Header.StartsWith("@transition ", StringComparison.Ordinal)))
        {
            var selector = Selector(child.Header, child, target);
            if (selector is null) continue;
            if (child.Children.Any(n => n.Value is null)) { Error(child, "Theme rules accept declarations; deeper selector nesting is not supported."); continue; }
            rules.Add(Rule(selector.Value.States, selector.Value.PartPredicate, Setters(child.Children, selector.Value.PartType ?? target, false)));
        }
        _fields.Append("    public static global::EclipseUI.ControlTheme ").Append(name).Append(" { get; } = new(typeof(")
            .Append(target.ClrTypeDisplayName).Append("), new ").Append(templateType).Append("(), ").Append(string.Join(", ", rules)).AppendLine(");");
    }
    private static bool Assignable(ITypeSymbol? target, ITypeSymbol? expected)
    {
        for (var current = target as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, expected)) return true;
        return false;
    }

    private string? ResolveTemplate(string name)
    {
        var qualified = templates.Where(t => t.TemplateTarget is not null && t.Namespace + "." + t.Tag == name).ToArray();
        if (qualified.Length == 1) return "global::" + qualified[0].Namespace + "." + qualified[0].Tag;
        if (qualified.Length > 1) return null;

        foreach (var ns in _enclosingNamespaces)
        {
            var inScope = templates.Where(t => t.TemplateTarget is not null && t.Tag == name && t.Namespace == ns).ToArray();
            if (inScope.Length == 1) return "global::" + inScope[0].Namespace + "." + inScope[0].Tag;
            if (inScope.Length > 1) return null;
        }

        var imported = templates.Where(t => t.TemplateTarget is not null && t.Tag == name &&
            t.Namespace is not null && _imports.Contains(t.Namespace)).ToArray();
        return imported.Length == 1 ? "global::" + imported[0].Namespace + "." + imported[0].Tag : null;
    }

    private INamedTypeSymbol? ResolveVisibleType(string name)
    {
        if (name.IndexOf('.') >= 0 && FindType(name) is { } exact) return exact;
        foreach (var ns in _enclosingNamespaces)
            if (FindType(ns + "." + name) is { } inScope) return inScope;
        if (FindType(name) is { } global) return global;
        var imported = _imports.Select(ns => FindType(ns + "." + name))
            .Where(type => type is not null).Cast<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Take(2).ToArray();
        return imported.Length == 1 ? imported[0] : null;
    }
    private static string Rule(string states, string part, IEnumerable<string> setters) =>
        $"new global::EclipseUI.ThemeRule({states}, {part}, new global::EclipseUI.ThemeSetter[] {{ {string.Join(", ", setters)} }})";

    private void EmitStyle(ThemeStyleNode node)
    {
        if (node.Value is not null) { Error(node, "Expected a typed style rule or @theme declaration."); return; }
        var selector = Selector(node.Header, node, null);
        if (selector is null) return;
        var s = selector.Value;
        var defaults = node.Children.Where(n => n.Value is not null || n.Header.StartsWith("@transition ", StringComparison.Ordinal)).ToArray();
        AddStyle(s, defaults);
        foreach (var nested in node.Children.Where(n => n.Value is null && !n.Header.StartsWith("@transition ", StringComparison.Ordinal)))
        {
            if (!nested.Header.StartsWith("&", StringComparison.Ordinal)) { Error(nested, "Nested rules must start with '&'."); continue; }
            var expanded = Selector(node.Header + nested.Header.Substring(1), nested, null);
            if (expanded is { } e) AddStyle(e, nested.Children);
        }
    }
    private void AddStyle((UiElementDef OwnerType, UiElementDef? PartType, string OwnerPredicate, string PartPredicate, string States, int Specificity) selector, IReadOnlyList<ThemeStyleNode> declarations)
    {
        var setters = Setters(declarations, selector.PartType ?? selector.OwnerType,
            selector.States == "global::EclipseUI.PseudoStates.None");
        _apply.Append("        registry.AddRule(").Append(selector.OwnerPredicate).Append(", ").Append(selector.PartPredicate)
            .Append(", ").Append(selector.Specificity).Append(", new global::EclipseUI.ThemeSetter[] { ").Append(string.Join(", ", setters)).AppendLine(" });");
    }
    private (UiElementDef OwnerType, UiElementDef? PartType, string OwnerPredicate, string PartPredicate, string States, int Specificity)? Selector(string text, ThemeStyleNode source, UiElementDef? owner)
    {
        var halves = text.Split(new[] { "/template/" }, StringSplitOptions.None);
        if (halves.Length > 2) { Error(source, "Only one explicit template boundary is supported per selector."); return null; }
        var match = Regex.Match(halves[0].Trim(), @"^(?<type>[A-Za-z_]\w*|&)?(?<classes>(?:\.[A-Za-z_][\w-]*)*)(?<states>(?::[a-z-]+)*)$");
        if (!match.Success || match.Value.Length == 0) { Error(source, "Unsupported selector; use Type.class:state /template/ Type.class."); return null; }
        var typeName = match.Groups["type"].Value;
        var specificity = (typeName.Length > 0 && typeName != "&" ? 1 : 0) + 100 *
            (match.Groups["classes"].Value.Count(c => c == '.') + match.Groups["states"].Value.Count(c => c == ':'));
        var target = owner ?? (typeName.Length == 0 ? catalog.ElementStyleTarget : Resolve(typeName));
        if (target is null) { Error(source, $"Unknown or ambiguous selector type '{typeName}'."); return null; }
        var conditions = new List<string> { "e is " + target.ClrTypeDisplayName };
        foreach (var cls in match.Groups["classes"].Value.Split('.').Where(c => c.Length > 0))
            conditions.Add($"global::System.Linq.Enumerable.Contains(e.Classes, {Quote(cls)})");
        var states = new List<string>();
        foreach (var state in match.Groups["states"].Value.Split(':').Where(c => c.Length > 0))
        {
            var name = state switch { "hover" => "Hover", "pressed" or "active" => "Pressed", "focus" => "Focused", "focus-visible" => "FocusVisible", "focus-within" => "FocusWithin", "disabled" => "Disabled", _ => null };
            if (name is null) { Error(source, $"Unsupported theme state '{state}'."); return null; }
            states.Add("global::EclipseUI.PseudoStates." + name);
        }
        var mask = states.Count == 0 ? "global::EclipseUI.PseudoStates.None" : string.Join(" | ", states);
        conditions.Add($"(e.PseudoStates & ({mask})) == ({mask})");
        UiElementDef? partType = null;
        var part = "null";
        if (halves.Length == 2)
        {
            var partMatch = Regex.Match(halves[1].Trim(), @"^(?<type>[A-Za-z_]\w*)(?<classes>(?:\.[A-Za-z_][\w-]*)*)$");
            partType = partMatch.Success ? Resolve(partMatch.Groups["type"].Value) : null;
            if (partType is null) { Error(source, "Template selector requires a known element type, optionally followed by classes."); return null; }
            var tests = new List<string> { "p is " + partType.ClrTypeDisplayName };
            tests.AddRange(partMatch.Groups["classes"].Value.Split('.').Where(c => c.Length > 0).Select(c => $"global::System.Linq.Enumerable.Contains(p.Classes, {Quote(c)})"));
            part = "p => " + string.Join(" && ", tests);
            specificity += 1 + 100 * partMatch.Groups["classes"].Value.Count(c => c == '.');
        }
        return (target, partType, "e => " + string.Join(" && ", conditions), part, mask, specificity);
    }
    private List<string> Setters(IReadOnlyList<ThemeStyleNode> nodes, UiElementDef target, bool allowTransitions = true)
    {
        var result = new List<string>();
        foreach (var node in nodes)
        {
            if (node.Header.StartsWith("@transition ", StringComparison.Ordinal) || node.Header == "transition")
            {
                if (!allowTransitions) { Error(node, "Transition configuration is only supported in a base rule, not a state variant."); continue; }
                var transition = Transition(node, target);
                if (transition is not null) result.Add(transition);
                continue;
            }
            if (node.Value is null) { Error(node, "Expected a property declaration."); continue; }
            if (!target.Properties.TryGetValue(node.Header, out var property)) { Error(node, $"'{node.Header}' is not a styled property on {target.Tag}."); continue; }
            var value = node.Value;
            var dynamic = false;
            string? expression;
            if (value.StartsWith("resource(", StringComparison.Ordinal))
            {
                var name = value.EndsWith(")", StringComparison.Ordinal) ? value.Substring(9, value.Length - 10).Trim() : "";
                if (node.Header == "Theme" && _themes.TryGetValue(name, out var theme))
                {
                    if (!Assignable(compilation.GetTypeByMetadataName(target.ClrTypeDisplayName.Substring(8)), compilation.GetTypeByMetadataName(theme.Type.Substring(8))))
                    { Error(node, "Control theme resource target is incompatible with the selector."); continue; }
                    expression = name;
                }
                else if (name.IndexOf('.') >= 0)
                {
                    expression = Resource(name, property, node);
                    dynamic = true;
                }
                else { Error(node, $"Unknown or incompatible control theme resource '{name}'."); continue; }
            }
            else if (value.StartsWith("theme(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
            {
                var pair = value.Substring(6, value.Length - 7).Split(',');
                if (pair.Length != 2) { Error(node, "theme() requires dark and light literals."); continue; }
                var dark = Literal(property, pair[0].Trim(), node); var light = Literal(property, pair[1].Trim(), node);
                if (dark is null || light is null) continue;
                expression = $"e => e.IsLightTheme ? {light} : {dark}"; dynamic = true;
            }
            else expression = Literal(property, value, node);
            if (expression is not null) result.Add($"global::EclipseUI.ThemeSetter.{(dynamic ? "Dynamic" : "Value")}({Field(property)}, {expression})");
        }
        return result;
    }

    private string? Transition(ThemeStyleNode node, UiElementDef target)
    {
        string name;
        IEnumerable<(string Name, string Value)> fields;
        if (node.Header == "transition")
        {
            var parts = (node.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) { Error(node, "Expected transition: Property 150ms CubicOut;"); return null; }
            name = parts[0];
            fields = new[] { ("Duration", parts[1]), ("Easing", parts[2]) };
        }
        else
        {
            name = node.Header.Substring("@transition ".Length).Trim();
            if (node.Value is not null || node.Children.Any(n => n.Value is null))
            { Error(node, "Transition block accepts Duration and Easing declarations."); return null; }
            fields = node.Children.Select(n => (n.Header, n.Value!));
        }
        if (!target.Properties.TryGetValue(name, out var property))
        { Error(node, $"'{name}' is not a styled property on {target.Tag}."); return null; }
        var typeName = property.DeclaringTypeName;
        var type = compilation.GetTypeByMetadataName(typeName.StartsWith("global::", StringComparison.Ordinal) ? typeName.Substring(8) : typeName);
        var field = type?.GetMembers(property.PropertyFieldName).OfType<IFieldSymbol>().SingleOrDefault();
        var valueType = (field?.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
        if (field is null || !field.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "EclipseUI.UiAnimatableAttribute") ||
            (valueType?.SpecialType != SpecialType.System_Double && valueType?.ToDisplayString() != "EclipseUI.UiColor"))
        { Error(node, $"Property '{name}' does not support animation."); return null; }
        if (!Eustyle.TryParseTransition(fields, out var ms, out var easing, out var error))
        { Error(node, error); return null; }
        return $"global::EclipseUI.ThemeSetter.Transition({Field(property)}, new global::EclipseUI.TransitionSpec(global::System.TimeSpan.FromMilliseconds({ms.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}), global::EclipseUI.Easings.{easing}))";
    }
    private string? Resource(string name, UiPropDef property, ThemeStyleNode node)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$"))
        { Error(node, "Expected resource(Type.StaticField)."); return null; }
        var split = name.LastIndexOf('.');
        var typeName = name.Substring(0, split); var memberName = name.Substring(split + 1);
        var candidates = VisibleTypes(typeName);
        if (candidates.Length != 1)
        { Error(node, $"Unknown or ambiguous typed resource '{name}'; declare its namespace with @namespace or import it with @using."); return null; }
        var field = candidates[0].GetMembers(memberName).OfType<IFieldSymbol>().SingleOrDefault();
        if (field is null || !field.IsStatic || field.DeclaredAccessibility != Accessibility.Public ||
            !compilation.IsSymbolAccessibleWithin(field, compilation.Assembly) ||
            field.Type is not INamedTypeSymbol resourceType ||
            resourceType.OriginalDefinition.ToDisplayString() != "EclipseUI.ThemeResource<T>")
        { Error(node, $"Resource '{name}' must be a public static ThemeResource<T> field."); return null; }
        var declaringType = FindType(property.DeclaringTypeName.Replace("global::", ""));
        var propertyField = declaringType?.GetMembers(property.PropertyFieldName).OfType<IFieldSymbol>().SingleOrDefault();
        var valueType = (propertyField?.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
        if (valueType is null || !SymbolEqualityComparer.Default.Equals(valueType, resourceType.TypeArguments[0]))
        {
            Error(node, $"Resource '{name}' has value type '{resourceType.TypeArguments[0].ToDisplayString()}', but property '{property.Name}' requires '{property.ClrTypeDisplayName}'.");
            return null;
        }
        var member = SyntaxFacts.GetKeywordKind(field.Name) == SyntaxKind.None ? field.Name : "@" + field.Name;
        return field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + member + ".Resolve";
    }

    private INamedTypeSymbol[] VisibleTypes(string typeName)
    {
        if (typeName.IndexOf('.') >= 0 && FindType(typeName) is { } exact) return [exact];
        foreach (var ns in _enclosingNamespaces)
            if (FindType(ns + "." + typeName) is { } inScope) return [inScope];
        if (FindType(typeName) is { } global) return [global];
        return _imports.Select(ns => FindType(ns + "." + typeName))
            .Where(type => type is not null).Cast<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
    }
    private INamedTypeSymbol? FindType(string name)
    {
        var type = compilation.GetTypeByMetadataName(name);
        if (type is not null) return type;
        // Metadata names use '+' for nested types while style references use normal C# dots.
        for (var split = name.LastIndexOf('.'); split >= 0; split = name.LastIndexOf('.', split - 1))
        {
            var nested = name.Substring(0, split) + "+" + name.Substring(split + 1).Replace('.', '+');
            if (compilation.GetTypeByMetadataName(nested) is { } match) return match;
            if (split == 0) break;
        }
        return null;
    }
    private string? Literal(UiPropDef property, string value, ThemeStyleNode node)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
        if (UiLiterals.TryConvert(property.TypeShape, value, catalog.FindLiteralParser(property.TypeShape.CanonicalKey), out var expression, out var error)) return expression;
        Error(node, error ?? $"Cannot convert value for {property.Name}."); return null;
    }
}
