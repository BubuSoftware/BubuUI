using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// S6 语法对齐（K21–K29）的前端回归：<c>@using</c> / <c>@code</c> / <c>else</c> / <c>@switch</c> /
/// <c>@* *@</c> 与对应诊断。
///
/// 存在理由：这批语法是 S6 一次性落地的，此前**零**前端用例；
/// <c>@switch</c> 多分支因词法把 <c>break;</c> 与下一个 <c>case</c> 头累进同一个 Text token
/// 而整段落成文本节点（EUI006），正是没有用例才漏到"已实现"结论里。
/// </summary>
public class S6SyntaxTests
{
    private static (UiDocument Doc, List<UiDiagnostic> Diags) Parse(string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        return (doc, diags);
    }

    private static UiElement Root(UiDocument doc) =>
        Assert.IsType<UiElement>(doc.Nodes.Single(node => node is UiElement));

    // ------------------------------------------------------------------
    // @switch（K23 ③ / K29，D87）
    // ------------------------------------------------------------------

    [Fact]
    public void Switch_MultiSection_ParsesEverySectionWithOwnBody()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                        <TextBlock>A</TextBlock>
                        break;
                    case 2:
                        <TextBlock>B</TextBlock>
                        break;
                    default:
                        <TextBlock>D</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var node = Assert.IsType<UiSwitch>(Root(doc).Children.Single());
        Assert.Equal("mode", node.Value);
        Assert.Equal(3, node.Sections.Count);
        Assert.Equal(new[] { "1", "2" }, node.Sections.Where(s => !s.IsDefault).Select(s => s.Label));
        Assert.True(node.Sections[2].IsDefault);
        Assert.All(node.Sections, section =>
        {
            Assert.True(section.HasBreak, "每个 section 都以自己的 break; 结束");
            Assert.Single(section.Children);
        });
    }

    /// <summary>
    /// 回归钉：<c>break;</c> 与下一个 <c>case</c> 头在同一个 Text token 里，
    /// 旧实现要求整段 trim 后等于 <c>break;</c>，于是第一个 break 被当成文本 → EUI006。
    /// </summary>
    [Fact]
    public void Switch_BreakFollowedByNextCaseHeader_IsNotTreatedAsText()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                        <TextBlock>A</TextBlock>
                        break;
                    case 2:
                        <TextBlock>B</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        Assert.DoesNotContain(diags, d => d.Message.Contains("text content"));
        Assert.Empty(diags);
    }

    [Fact]
    public void Switch_BreakOnSameLineAsElement_IsStillAccepted()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                        <TextBlock>A</TextBlock> break;
                    default:
                        <TextBlock>D</TextBlock> break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        Assert.Empty(diags);
    }

    [Fact]
    public void Switch_DefaultOnly_IsLegal()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    default:
                        <TextBlock>D</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var node = Assert.IsType<UiSwitch>(Root(doc).Children.Single());
        Assert.Single(node.Sections);
        Assert.True(node.Sections[0].IsDefault);
    }

    [Fact]
    public void Switch_CaseWithoutBreak_ReportsEUI100()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                        <TextBlock>A</TextBlock>
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        var diag = Assert.Single(diags, d => d.Code == UiDiagnostics.Syntax);
        Assert.Contains("must end with a 'break;'", diag.Message);
    }

    [Fact]
    public void Switch_EmptySectionWithoutBreak_ReportsEUI100()
    {
        // K29 ③：无贯穿语义，`case 1:` 紧跟 `case 2:` 不是标签组；静默渲染空内容必须显式拒绝。
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                    case 2:
                        <TextBlock>B</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        var diag = Assert.Single(diags, d => d.Code == UiDiagnostics.Syntax);
        Assert.Contains("multi-label sections are not supported", diag.Message);
    }

    [Fact]
    public void Switch_DuplicateDefault_ReportsEUI100()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    default:
                        <TextBlock>A</TextBlock>
                        break;
                    default:
                        <TextBlock>B</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("duplicate 'default'"));
    }

    [Fact]
    public void Switch_NonConstantLabelShape_ReportsEUI047()
    {
        // 语法层只判「常量形态」（K29 ②）：标识符/成员访问是合法形态，常量性由探针判定；
        // 调用表达式、运算式在这里就该拒。
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case Foo():
                        <TextBlock>A</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        var diag = Assert.Single(diags, d => d.Code == UiDiagnostics.CaseLabelNotConstant);
        Assert.Contains("compile-time constant", diag.Message);
    }

    [Fact]
    public void Switch_ConstantShapes_AreAccepted()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case -1:
                        <TextBlock>A</TextBlock>
                        break;
                    case Mode.One:
                        <TextBlock>B</TextBlock>
                        break;
                    case "text":
                        <TextBlock>C</TextBlock>
                        break;
                    case (2):
                        <TextBlock>D</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        Assert.DoesNotContain(diags, d => d.Code == UiDiagnostics.CaseLabelNotConstant);
    }

    [Fact]
    public void Switch_BreakInsideNestedBlock_ReportsEUI100()
    {
        const string ui = """
            <StackPanel>
                @switch (mode) {
                    case 1:
                        @if (flag) {
                            <TextBlock>A</TextBlock>
                            break;
                        }
                        break;
                }
            </StackPanel>
            """;

        var (_, diags) = Parse(ui);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("not allowed inside a nested block"));
    }

    // ------------------------------------------------------------------
    // else / else if（K23 ③）
    // ------------------------------------------------------------------

    [Fact]
    public void ElseChain_BuildsElseIfAndElseBranches()
    {
        const string ui = """
            <StackPanel>
                @if (a) {
                    <TextBlock>A</TextBlock>
                }
                else if (b) {
                    <TextBlock>B</TextBlock>
                }
                else {
                    <TextBlock>C</TextBlock>
                }
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var ifNode = Assert.IsType<UiIf>(Root(doc).Children.Single());
        Assert.Equal("a", ifNode.Condition);
        Assert.Single(ifNode.ElseIfs);
        Assert.Equal("b", ifNode.ElseIfs[0].Condition);
        Assert.NotNull(ifNode.ElseChildren);
        Assert.Single(ifNode.ElseChildren!);
        Assert.True(ifNode.HasElseChain);
    }

    /// <summary>
    /// 裸 <c>else</c> 只在上一分支的 <c>}</c> 之后由词法一次性识别为续接（<c>EnableElseContinuation</c>）；
    /// 孤立出现时按普通文本处理，落 <c>EUI006</c> 而不是静默通过。
    /// 这里钉住"有诊断"这一下限（消息不是专用文案，见 spec/01 §2.7 的说明）。
    /// </summary>
    [Fact]
    public void Else_WithoutPrecedingIf_IsNotSilentlyAccepted()
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse("<StackPanel>\nelse {\n<TextBlock>A</TextBlock>\n}\n</StackPanel>", "Test.ui", diags);
        FrontEnd.Analyze(doc, UiTestCatalogs.BuiltIn(), diags);

        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.IsError);
    }

    // ------------------------------------------------------------------
    // 注释：@* *@ 与 <!-- -->（K26，D88/D12）
    // ------------------------------------------------------------------

    [Fact]
    public void RazorComment_IsDropped_AndSurroundingTextIsMerged()
    {
        const string ui = """
            <StackPanel>
                <TextBlock>a@* gone *@b</TextBlock>
            </StackPanel>
            """;

        // 文本里 'a' 后面是 '@'，词法在 '@' 处断词，因此两个文本节点；@* *@ 本身不产生节点。
        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var textBlock = Assert.IsType<UiElement>(Root(doc).Children.Single());
        var text = Assert.IsType<UiText>(textBlock.Children.Single());
        Assert.Equal("ab", string.Concat(text.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void UnclosedRazorComment_ReportsEUI100()
    {
        var (_, diags) = Parse("<StackPanel>\n@* never closed\n</StackPanel>");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("'@*' comment"));
    }

    [Fact]
    public void UnclosedHtmlComment_ReportsEUI100()
    {
        var (_, diags) = Parse("<StackPanel>\n<!-- never closed\n</StackPanel>");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("'<!--' comment"));
    }

    [Fact]
    public void BothCommentForms_CoexistInsideElement()
    {
        const string ui = """
            <StackPanel>
                @* one *@
                <!-- two -->
                <TextBlock>x</TextBlock>
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        Assert.Single(Root(doc).Children);
    }

    // ------------------------------------------------------------------
    // @using（K22/D85）与 @code（K21/K25/D86）
    // ------------------------------------------------------------------

    [Fact]
    public void Using_Directives_AreRootLevel_OrderPreserved_AndMayRepeat()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using System.Collections.Generic
            @using EclipseUI.Elements

            <StackPanel>
                <TextBlock>{text}</TextBlock>
            </StackPanel>
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        Assert.Equal(
            new[] { "EclipseUI.Elements", "System.Collections.Generic", "EclipseUI.Elements" },
            doc.Nodes.OfType<UiUsingDirective>().Select(u => u.Namespace));
    }

    [Fact]
    public void Using_ParenthesisedForm_IsRejected()
    {
        var (_, diags) = Parse("<StackPanel />\n@using (System.Text)");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("'@using (...)' is not supported"));
    }

    [Fact]
    public void CodeBlock_BelowTemplate_IsCollectedAsClassMembers()
    {
        const string ui = """
            <StackPanel>
                <TextBlock>{message}</TextBlock>
            </StackPanel>

            @code {
                public string message = "hi";
            }
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        var block = Assert.Single(doc.Nodes.OfType<UiCodeBlock>());
        Assert.Contains("public string message", block.Code);
        // 根层只允许 @code/@using/注释 + 一个元素：@code 不产生 EUI006/EUI012
        Assert.Single(doc.Nodes.OfType<UiElement>());
    }

    [Fact]
    public void CodeBlock_MultipleBlocks_AreAllCollected()
    {
        const string ui = """
            @code {
                public int a = 1;
            }

            <StackPanel>
                <TextBlock>{a}</TextBlock>
            </StackPanel>

            @code {
                public int b = 2;
            }
            """;

        var (doc, diags) = Parse(ui);

        Assert.Empty(diags);
        Assert.Equal(2, doc.Nodes.OfType<UiCodeBlock>().Count());
    }

    [Fact]
    public void CodeBlock_WithoutBraces_ReportsEUI100()
    {
        var (_, diags) = Parse("<StackPanel />\n@code \npublic int a;");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("expected '{' after @code"));
    }

    [Fact]
    public void UnclosedCodeBlock_ReportsEUI100()
    {
        var (_, diags) = Parse("<StackPanel />\n@code {\n  public int a;");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax
            && d.Message.Contains("unterminated @code"));
    }

    // ------------------------------------------------------------------
    // 明确不采纳（K23 ②/K24/K28）：诊断必须显式
    // ------------------------------------------------------------------

    [Fact]
    public void ForLoop_ReportsEUI009_WithRewriteHint()
    {
        var (_, diags) = Parse("<StackPanel>\n@for (int i = 0; i < 3; i++) { <TextBlock>x</TextBlock> }\n</StackPanel>");

        var diag = Assert.Single(diags, d => d.Code == UiDiagnostics.UnknownDirective);
        Assert.Contains("Enumerable.Range", diag.Message);
    }

    [Fact]
    public void WhileAndDo_ReportEUI009()
    {
        var (_, diags) = Parse("<StackPanel>\n@while (flag) { <TextBlock>x</TextBlock> }\n</StackPanel>");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownDirective);
    }

    [Fact]
    public void CodeStatementBlock_IsRejectedWithGuidance()
    {
        var (_, diags) = Parse("<StackPanel>\n@{ var x = 1; }\n</StackPanel>");

        var diag = Assert.Single(diags, d => d.Code == UiDiagnostics.Syntax);
        Assert.Contains("@code members or code-behind methods", diag.Message);
    }

    [Fact]
    public void WebDirective_ReportsEUI009()
    {
        var (_, diags) = Parse("@page \"/x\"\n<StackPanel />");

        Assert.Contains(diags, d => d.Code == UiDiagnostics.UnknownDirective
            && d.Message.Contains("@page"));
    }
}
