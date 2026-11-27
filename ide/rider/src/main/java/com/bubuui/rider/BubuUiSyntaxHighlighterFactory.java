package com.bubuui.rider;

import com.intellij.lexer.Lexer;
import com.intellij.openapi.editor.colors.TextAttributesKey;
import com.intellij.openapi.fileTypes.SyntaxHighlighter;
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.psi.tree.IElementType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;
import org.jetbrains.plugins.textmate.language.syntax.highlighting.TextMateSyntaxHighlighterFactory;
import org.jetbrains.plugins.textmate.language.syntax.lexer.TextMateElementType;

public final class BubuUiSyntaxHighlighterFactory extends SyntaxHighlighterFactory {
    @Override
    public @NotNull SyntaxHighlighter getSyntaxHighlighter(@Nullable Project project, @Nullable VirtualFile file) {
        var delegate = new TextMateSyntaxHighlighterFactory().getSyntaxHighlighter(project, file);
        return new SyntaxHighlighter() {
            @Override
            public @NotNull Lexer getHighlightingLexer() { return delegate.getHighlightingLexer(); }

            @Override
            public TextAttributesKey @NotNull [] getTokenHighlights(IElementType tokenType) {
                if (tokenType instanceof TextMateElementType token) {
                    var key = BubuUiTemplateColors.forScope(token.getScope());
                    if (key != null) return new TextAttributesKey[]{key};
                }
                return delegate.getTokenHighlights(tokenType);
            }
        };
    }
}
