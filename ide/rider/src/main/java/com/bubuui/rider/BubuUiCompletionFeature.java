package com.bubuui.rider;

import com.intellij.application.options.CodeStyle;
import com.intellij.codeInsight.completion.CompletionInitializationContext;
import com.intellij.codeInsight.completion.InsertionContext;
import com.intellij.codeInsight.lookup.LookupElement;
import com.intellij.codeInsight.template.Template;
import com.intellij.codeInsight.template.TemplateManager;
import com.intellij.openapi.project.Project;
import com.redhat.devtools.lsp4ij.client.features.LSPCompletionFeature;
import com.redhat.devtools.lsp4ij.client.features.LSPCompletionProposal;
import com.redhat.devtools.lsp4ij.features.completion.SnippetTemplateFactory;
import org.eclipse.lsp4j.CompletionItem;
import org.eclipse.lsp4j.InsertTextFormat;
import org.eclipse.lsp4j.InsertTextMode;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

/** LSP4IJ 0.21 advertises AdjustIndentation but inserts text edits without a line prefix. */
public final class BubuUiCompletionFeature extends LSPCompletionFeature {
    @Override
    public @Nullable LookupElement createLookupElement(@NotNull CompletionItem item, @NotNull LSPCompletionContext context) {
        if (item.getInsertTextFormat() != InsertTextFormat.Snippet || !item.getLabel().startsWith("@"))
            return super.createLookupElement(item, context);
        return new LSPCompletionProposal(item, context, this) {
            @Override
            public void handleInsert(@NotNull InsertionContext insertion) {
                var document = insertion.getDocument();
                int line = document.getLineNumber(insertion.getStartOffset());
                int lineStart = document.getLineStartOffset(line);
                var chars = document.getCharsSequence();
                int first = lineStart;
                while (first < chars.length() && (chars.charAt(first) == ' ' || chars.charAt(first) == '\t')) first++;
                String prefix = chars.subSequence(lineStart, first).toString();
                var options = CodeStyle.getIndentOptions(insertion.getFile());
                var edit = item.getTextEdit();
                String snippet = edit == null ? item.getInsertText()
                        : edit.isLeft() ? edit.getLeft().getNewText() : edit.getRight().getNewText();
                if (snippet == null) snippet = item.getLabel();
                String text = item.getInsertTextMode() == InsertTextMode.AsIs ? snippet.replace("\r\n", "\n")
                        : indentSnippet(snippet, prefix, options.INDENT_SIZE, options.TAB_SIZE, options.USE_TAB_CHARACTER);
                Template template = createTemplate(text, insertion.getProject());
                // Use LSP4IJ's normal range/additional-edit handling, then activate the inline
                // template ourselves so its global defaults cannot change per-file whitespace.
                if (edit == null) item.setInsertText(template.getTemplateText());
                else if (edit.isLeft()) edit.getLeft().setNewText(template.getTemplateText());
                else edit.getRight().setNewText(template.getTemplateText());
                apply(document, insertion.getOffset(CompletionInitializationContext.SELECTION_END_OFFSET));
                insertion.setAddCompletionChar(false);
                var caret = insertion.getEditor().getCaretModel();
                caret.moveToOffset(Math.max(0, caret.getOffset() - template.getTemplateText().length()));
                TemplateManager.getInstance(insertion.getProject()).startTemplate(insertion.getEditor(), template);
            }
        };
    }

    static Template createTemplate(String text, Project project) {
        Template template = SnippetTemplateFactory.createTemplate(text, project, name -> null, null, true);
        template.setToIndent(false);
        template.setToReformat(false);
        return template;
    }

    static String indentSnippet(String snippet, String prefix, int indentSize, int tabSize, boolean tabs) {
        String[] lines = snippet.replace("\r\n", "\n").split("\n", -1);
        int width = Math.max(1, tabSize);
        for (int i = 1; i < lines.length; i++) {
            int level = 0;
            while (level < lines[i].length() && lines[i].charAt(level) == '\t') level++;
            int columns = level * Math.max(1, indentSize);
            String relative = tabs ? "\t".repeat(columns / width) + " ".repeat(columns % width) : " ".repeat(columns);
            lines[i] = prefix + relative + lines[i].substring(level);
        }
        return String.join("\n", lines);
    }
}
