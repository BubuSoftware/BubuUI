package com.bubuui.rider;

import com.intellij.testFramework.ParsingTestCase;
import com.intellij.codeInsight.template.impl.TemplateImpl;
import com.redhat.devtools.lsp4ij.features.completion.SnippetTemplateLoader;
import com.redhat.devtools.lsp4ij.features.completion.snippet.LspSnippetParser;

public final class DirectiveCompletionTest extends ParsingTestCase {
    public DirectiveCompletionTest() { super("", "bui", new BubuUiParserDefinition()); }
    @Override protected String getTestDataPath() { return ""; }

    private TemplateImpl parse(String text) {
        var template = new TemplateImpl("", "");
        template.setInline(true);
        new LspSnippetParser(new SnippetTemplateLoader(template, name -> null, null, true)).parse(text);
        return template;
    }

    public void testDoSnippetRetainsPlaceholdersAndUsesTwoSpaceIndent() {
        String text = BubuUiCompletionFeature.indentSnippet("@do {\n\t$0\n} while (${1:condition});", "    ", 2, 4, false);
        assertEquals("@do {\n      $0\n    } while (${1:condition});", text);
        var template = parse(text);
        assertFalse(template.getTemplateText().contains("$0"));
        assertFalse(template.getTemplateText().contains("${"));
        assertTrue(template.getTemplateText().replace("\t", "<TAB>").replace("\n", "<NL>"), template.getTemplateText().contains("\n      \n    } while ("));
        assertFalse(template.getVariables().isEmpty());
    }

    public void testTabsAndNestedSwitchIndentAreNotExpandedWithGlobalDefaults() {
        String text = BubuUiCompletionFeature.indentSnippet("@switch (${1:value}) {\n\tcase 1:\n\t\t$0\n}", "\t", 4, 4, true);
        assertEquals("@switch (${1:value}) {\n\t\tcase 1:\n\t\t\t$0\n\t}", text);
        var template = parse(text);
        assertTrue(template.getTemplateText().replace("\t", "<TAB>").replace("\n", "<NL>"), template.getTemplateText().contains("\n\t\tcase 1:\n\t\t\t"));
    }
}
