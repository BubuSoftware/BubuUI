package com.bubuui.rider;

import com.intellij.openapi.editor.impl.DocumentImpl;
import com.intellij.openapi.util.TextRange;
import com.intellij.testFramework.ParsingTestCase;
import com.redhat.devtools.lsp4ij.LSPIJUtils;

public final class BssNavigationRangeTest extends ParsingTestCase {
    public BssNavigationRangeTest() {
        super("", "bss", new BssParserDefinition());
    }

    @Override
    protected String getTestDataPath() { return ""; }

    public void testStylesheetNavigationDoesNotSelectTheWholeFile() {
        String text = ".primary {\n  Background: #FFFFFF;\n  CornerRadius: 8;\n}";
        myFile = createFile("Navigation.bss", text);
        var document = new DocumentImpl(text);

        for (String word : new String[]{"primary", "Background", "CornerRadius"}) {
            int start = text.indexOf(word);
            var expected = new TextRange(start, start + word.length());
            assertEquals(expected, myFile.findElementAt(start + 1).getTextRange());
            assertEquals(expected, LSPIJUtils.getWordRangeAt(document, myFile, start + 1));
        }
    }
}
