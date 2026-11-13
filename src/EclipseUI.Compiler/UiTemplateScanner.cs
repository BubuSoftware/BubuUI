using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>
/// 从 .ui 文件提取根类型、命名空间、imports 和 @code，供生成前符号视图使用。
///
/// 生成器（<c>AdditionalTextsProvider</c>）与 IDE 项目系统（<c>AdditionalFiles</c>）共用这一份
/// 实现——两侧喂给 <see cref="Metadata.UiCatalogFactory.Build"/> 的清单必须逐字节同源，
/// 否则同一个标签会在编辑器里绿、在 <c>dotnet build</c> 里 EUI001。这正是这个类型存在的理由。
///
/// 读 <c>@namespace</c> 一条指令 + 根层 <c>@code</code> 块（K34/D112：生成前符号视图合并模板
/// 特性用）。诊断一律丢弃——解析错误由各主管道报告，这里只负责「这条声明叫什么、在哪个
/// 命名空间、<c>@code</code> 里写了什么」。
/// </summary>
public static class UiTemplateScanner
{
    /// <summary>
    /// 单个文件 → 一条组件声明；文件名不是合法标识符时返回 null（EUI013 由发射阶段报，
    /// 目录里留半条指向不存在类型的脏声明没有意义）。
    /// </summary>
    public static UiTemplateDeclaration? FromFile(string path, string text)
    {
        var tag = Path.GetFileNameWithoutExtension(path);
        if (!IsUsableName(tag)) return null;
        // @namespace 缺失时返回 null：EUI055 已报 Error 并短路产物，目录按标签名认领即可，
        // 不在这里重复报（§2.7）。
        var doc = FrontEnd.Parse(text, path, new List<UiDiagnostic>());
        var blocks = new List<UiCodeBlockText>();
        foreach (var node in doc.Nodes)
        {
            if (node is not UiCodeBlock block || block.Code.Length == 0) continue;
            var (line, character) = PositionOf(text, block.CodeSpan.Start);
            blocks.Add(new UiCodeBlockText(block.Code, block.CodeSpan.Start, block.CodeSpan.Length,
                line, character));
        }
        return new UiTemplateDeclaration(tag, doc.Namespace)
        {
            Path = path,
            CodeBlocks = blocks,
            RootTag = doc.Nodes.OfType<UiElement>().FirstOrDefault()?.Tag,
            Usings = TemplateAnalyzer.CollectUsingLines(doc.Nodes),
        };
    }

    /// <summary>项目文件清单 → 声明清单（不含 .ui 的项直接跳过）。</summary>
    public static IReadOnlyList<UiTemplateDeclaration> FromFiles(IEnumerable<(string Path, string Text)> files)
    {
        var result = new List<UiTemplateDeclaration>();
        foreach (var (path, text) in files)
        {
            if (!string.Equals(Path.GetExtension(path), ".ui", StringComparison.OrdinalIgnoreCase)) continue;
            if (FromFile(path, text) is { } declaration) result.Add(declaration);
        }
        return result;
    }

    /// <summary>0 基行列：绝对偏移 → (行, 列)。扫描器一次线性扫描即可，目录侧按块内偏移复用。</summary>
    private static (int Line, int Character) PositionOf(string text, int offset)
    {
        var line = 0;
        var lastNewline = -1;
        for (var i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') { line++; lastNewline = i; }
        return (line, offset - lastNewline - 1);
    }

    /// <summary>
    /// 文件名能否当类名/标签名：词法口径（字母或 <c>_</c> 开头，其后字母/数字/下划线）。
    /// 本程序集不引用 Roslyn（前端是纯解析器），所以不复用 <c>SyntaxFacts</c>——差别只有
    /// C# 关键字（如 <c>class.ui</c>）：那种文件在发射阶段已被 EUI013 挡住，目录里多一条
    /// 也走不到生成。判定与 <c>Analyzer.IsValidIdentifier</c> 同口径。
    /// </summary>
    private static bool IsUsableName(string name)
    {
        if (name.Length == 0) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }
}
