package com.bubuui.rider;

import com.intellij.extapi.psi.ASTWrapperPsiElement;
import com.intellij.extapi.psi.PsiFileBase;
import com.intellij.lang.ASTNode;
import com.intellij.lang.Language;
import com.intellij.lang.ParserDefinition;
import com.intellij.lang.PsiParser;
import com.intellij.lexer.Lexer;
import com.intellij.lexer.LexerBase;
import com.intellij.openapi.fileTypes.FileType;
import com.intellij.openapi.project.Project;
import com.intellij.psi.FileViewProvider;
import com.intellij.psi.PsiElement;
import com.intellij.psi.PsiFile;
import com.intellij.psi.TokenType;
import com.intellij.psi.tree.IElementType;
import com.intellij.psi.tree.IFileElementType;
import com.intellij.psi.tree.TokenSet;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

/**
 * Supplies lexical PSI boundaries for navigation. TextMate still owns colouring,
 * and the shared language server owns all template and C# semantics.
 * Without a parser, a custom language becomes one plain-text PSI leaf, which
 * Rider underlines in its entirety when modifier+mouseover finds a definition.
 */
public class BubuUiParserDefinition implements ParserDefinition {
    private static final IElementType WORD = new IElementType("WORD", Language.ANY);
    private static final IElementType PUNCTUATION = new IElementType("PUNCTUATION", Language.ANY);
    private final IFileElementType fileNodeType;

    public BubuUiParserDefinition() {
        this(BubuUiLanguage.INSTANCE);
    }

    protected BubuUiParserDefinition(Language language) {
        fileNodeType = new IFileElementType(language);
    }

    @Override
    public @NotNull Lexer createLexer(Project project) {
        return new LexerBase() {
            private CharSequence buffer = "";
            private int end;
            private int start;
            private int tokenEnd;
            private IElementType tokenType;

            @Override
            public void start(@NotNull CharSequence buffer, int startOffset, int endOffset, int initialState) {
                this.buffer = buffer;
                end = endOffset;
                tokenEnd = startOffset;
                advance();
            }

            @Override
            public int getState() { return 0; }

            @Override
            public @Nullable IElementType getTokenType() { return tokenType; }

            @Override
            public int getTokenStart() { return start; }

            @Override
            public int getTokenEnd() { return tokenEnd; }

            @Override
            public void advance() {
                start = tokenEnd;
                if (start >= end) {
                    tokenType = null;
                    return;
                }
                char first = buffer.charAt(tokenEnd++);
                if (Character.isWhitespace(first)) {
                    tokenType = TokenType.WHITE_SPACE;
                    while (tokenEnd < end && Character.isWhitespace(buffer.charAt(tokenEnd))) tokenEnd++;
                } else if (Character.isJavaIdentifierPart(first)) {
                    tokenType = WORD;
                    while (tokenEnd < end && Character.isJavaIdentifierPart(buffer.charAt(tokenEnd))) tokenEnd++;
                } else {
                    tokenType = PUNCTUATION;
                }
            }

            @Override
            public @NotNull CharSequence getBufferSequence() { return buffer; }

            @Override
            public int getBufferEnd() { return end; }
        };
    }

    @Override
    public @NotNull PsiParser createParser(Project project) {
        return (root, builder) -> {
            var file = builder.mark();
            while (!builder.eof()) builder.advanceLexer();
            file.done(root);
            return builder.getTreeBuilt();
        };
    }

    @Override
    public @NotNull IFileElementType getFileNodeType() { return fileNodeType; }

    @Override
    public @NotNull TokenSet getCommentTokens() { return TokenSet.EMPTY; }

    @Override
    public @NotNull TokenSet getStringLiteralElements() { return TokenSet.EMPTY; }

    @Override
    public @NotNull PsiElement createElement(ASTNode node) { return new ASTWrapperPsiElement(node); }

    @Override
    public @NotNull PsiFile createFile(@NotNull FileViewProvider viewProvider) {
        return new NavigationFile(viewProvider);
    }

    private static final class NavigationFile extends PsiFileBase {
        private NavigationFile(FileViewProvider viewProvider) {
            super(viewProvider, viewProvider.getBaseLanguage());
        }

        @Override
        public @NotNull FileType getFileType() { return getViewProvider().getFileType(); }
    }
}
