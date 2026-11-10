using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EclipseUI.Compiler;
using EclipseUI.Rendering;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// design/17 §4.3「单一来源」的一致性闸门：属性目录必须等于 StyleBuilder 的公开 setter 集，
/// 且每条目标字段都真实存在。防止「改了运行时、忘了编译器」的漂移重新变成手写表。
/// </summary>
public class StylePropertyCatalogTests
{
    private static StylePropertyCatalog Catalog => UiTestCatalogs.StyleProperties();

    [Fact]
    public void Catalog_MatchesStyleBuilderPublicSetters()
    {
        var catalog = Catalog;
        Assert.Empty(catalog.Problems);

        var expected = typeof(StyleBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsGenericMethod)          // Set<T> 是通用逃生口，不是样式属性
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, catalog.Properties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Catalog_TargetFields_ResolveToRealStyledProperties()
    {
        foreach (var property in Catalog.Properties)
        {
            Assert.StartsWith("global::", property.PropertyField);
            var field = ResolveField(property.PropertyField);
            Assert.True(typeof(StyledProperty).IsAssignableFrom(field.FieldType),
                $"{property.Name} → {property.PropertyField} is not a StyledProperty");
            Assert.Equal(property.Kind, KindOf(field.FieldType));

            if (property.ResourceField is { } resource)
            {
                var resourceField = ResolveField(resource);
                Assert.True(typeof(StyledProperty).IsAssignableFrom(resourceField.FieldType));
                Assert.Equal(StyleValueKind.Color, KindOf(resourceField.FieldType));
            }
        }
    }

    [Fact]
    public void Catalog_ThemeCapability_ComesFromThemeColorOverloads()
    {
        var catalog = Catalog;
        var themeSetters = typeof(StyleBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(ThemeColor))
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(themeSetters, catalog.Properties.Where(p => p.AllowsTheme)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Find_IsExactAndCaseSensitive_NoAliases()
    {
        var catalog = Catalog;
        foreach (var name in catalog.SortedNames())
            Assert.Equal(name, catalog.Find(name)?.Name);

        // 旧的小写链接符写法、去分隔符写法、大小写变体一律不兼容。
        foreach (var name in new[] { "CornerRadius", "FocusRingWidth", "Background" })
        {
            Assert.Null(catalog.Find(EustyleCollapse.Kebab(name)));
            Assert.Null(catalog.Find(name.ToLowerInvariant()));
            Assert.Null(catalog.Find(name.ToUpperInvariant()));
        }
        Assert.Null(catalog.Find("Corner_Radius"));
        Assert.Null(catalog.Find("cornerradius"));
    }

    private static FieldInfo ResolveField(string display)
    {
        var full = display.Substring("global::".Length);
        var dot = full.LastIndexOf('.');
        var typeName = full.Substring(0, dot);
        var fieldName = full.Substring(dot + 1);
        // 显式触碰运行时/渲染程序集：按需加载可能尚未把目标类型带进 AppDomain
        var type = new[] { typeof(StyleBuilder).Assembly, typeof(EclipseUI.Component).Assembly }
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Distinct()
            .Select(a => a.GetType(typeName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException($"type '{typeName}' is not loaded");
        return type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"field '{display}' not found");
    }

    private static StyleValueKind KindOf(Type fieldType)
    {
        var valueType = fieldType.IsGenericType
            ? fieldType.GenericTypeArguments[0]
            : throw new InvalidOperationException($"expected StyledProperty<T>, got {fieldType}");
        var underlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (underlying == typeof(double)) return StyleValueKind.Number;
        if (underlying == typeof(Color) || underlying == typeof(ThemeColor)) return StyleValueKind.Color;
        throw new InvalidOperationException($"unexpected value type {valueType}");
    }
}
