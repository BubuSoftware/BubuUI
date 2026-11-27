package com.bubuui.rider;

import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import javax.swing.Icon;

/** Binds *.bui to {@link BubuUiLanguage}; the TextMate highlighter is wired in plugin.xml. */
public final class BubuUiFileType extends LanguageFileType {
    public static final BubuUiFileType INSTANCE = new BubuUiFileType();

    private BubuUiFileType() {
        super(BubuUiLanguage.INSTANCE);
    }

    @Override
    public @NotNull String getName() {
        return "BubuUI";
    }

    @Override
    public @NotNull String getDescription() {
        return "BubuUI template (.bui)";
    }

    @Override
    public @NotNull String getDefaultExtension() {
        return "bui";
    }

    @Override
    public @Nullable Icon getIcon() {
        return null;
    }
}
