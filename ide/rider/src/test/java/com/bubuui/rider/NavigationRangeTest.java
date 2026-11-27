package com.bubuui.rider;

import com.intellij.openapi.editor.impl.DocumentImpl;
import com.intellij.openapi.util.TextRange;
import com.intellij.testFramework.ParsingTestCase;
import com.redhat.devtools.lsp4ij.LSPIJUtils;

/** Exercises the same PSI word lookup LSP4IJ uses for modifier+mouseover. */
public final class NavigationRangeTest extends ParsingTestCase {
    public NavigationRangeTest() {
        super("", "bui", new BubuUiParserDefinition());
    }

    @Override
    protected String getTestDataPath() { return ""; }

    public void testUiNavigationHighlightsOnlyTheHoveredName() {
        String text = "@namespace Gallery\n<StackPanel Gap=\"8\">\n"
                + "  <TextBlock>{task.Name}</TextBlock>\n</StackPanel>\n"
                + "@code { void Refresh() { Invalidate(); } }";
        myFile = createFile("Navigation.bui", text);

        assertWord(text.indexOf("StackPanel"), "StackPanel");
        assertWord(text.lastIndexOf("StackPanel"), "StackPanel");
        assertWord(text.indexOf("Gap"), "Gap");
        assertWord(text.indexOf("TextBlock"), "TextBlock");
        assertWord(text.indexOf("task"), "task");
        assertWord(text.indexOf("Name"), "Name");
        assertWord(text.indexOf("Invalidate"), "Invalidate");
    }

    public void testWordRangesFollowAnUnsavedEdit() {
        String edited = "<MyButton>text</Button>";
        doReparseTest("<Button>text</Button>", edited);
        myFile = createFile("Navigation.bui", edited);

        assertWord(1, "MyButton");
        assertWord(edited.lastIndexOf("Button"), "Button");
    }

    public void testUnicodeIdentifiersKeepTheirOwnRange() {
        myFile = createFile("Navigation.bui", "<TextBlock>{\u72b6\u6001\u6587\u672c}</TextBlock>");
        assertWord("<TextBlock>{".length(), "\u72b6\u6001\u6587\u672c");
    }

    private void assertWord(int start, String word) {
        int offset = start + 1;
        TextRange expected = new TextRange(start, start + word.length());
        assertEquals(expected, myFile.findElementAt(offset).getTextRange());
        assertEquals(expected, LSPIJUtils.getWordRangeAt(
                new DocumentImpl(myFile.getText()), myFile, offset));
    }
}
