package com.bubuui.rider;

import com.intellij.ui.JBColor;
import org.jetbrains.plugins.textmate.language.syntax.lexer.TextMateScope;
import org.junit.Test;

import java.util.List;

import static org.junit.Assert.*;

public final class BubuUiTemplateColorsTest {
    @Test
    public void templatePaletteWorksInBothThemes() {
        boolean wasDark = !JBColor.isBright();
        try {
            String[] scopes = {"entity.name.type.class.bubuui", "entity.other.attribute-name.bubuui",
                    "keyword.control.import.bubuui", "string.quoted.double.bubuui",
                    "comment.block.bubuui", "punctuation.separator.key-value.bubuui"};
            int[][] colors = {{0x267F99, 0x0451A5, 0xAF00DB, 0xA31515, 0x008000, 0x666666},
                    {0x4EC9B0, 0x9CDCFE, 0xC586C0, 0xCE9178, 0x6A9955, 0xB4B4B4}};
            for (int theme = 0; theme < 2; theme++) {
                JBColor.setDark(theme == 1);
                for (int i = 0; i < scopes.length; i++) {
                    var key = BubuUiTemplateColors.forScope(TextMateScope.EMPTY.add("source.bubuui").add(scopes[i]));
                    assertNotNull(key);
                    assertEquals(colors[theme][i], key.getDefaultAttributes().getForegroundColor().getRGB() & 0xFFFFFF);
                }
            }
        } finally { JBColor.setDark(wasDark); }
    }

    @Test
    public void embeddedCSharpAlwaysDelegatesAndEmptyScopeIsSafe() {
        assertNull(BubuUiTemplateColors.forScope(TextMateScope.EMPTY));
        assertNull(BubuUiTemplateColors.forScopes(List.of("entity.name.function.cs", "source.cs", "source.bubuui")));
        assertNull(BubuUiTemplateColors.forScopes(List.of("string.quoted.double.cs", "source.cs", "string.quoted.double.bubuui")));
        assertEquals("BUBUUI_ATTRIBUTE", BubuUiTemplateColors.forScopes(List.of(
                "support.type.property-name.bss", "string.quoted.double.bubuui")).getExternalName());
        assertNull(BubuUiTemplateColors.forScopes(List.of("source.bubuui")));
    }
}
