package com.bubuui.rider;

import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import javax.swing.Icon;

/** Binds *.bss to {@link BssLanguage}; the TextMate highlighter is wired in plugin.xml. */
public final class BssFileType extends LanguageFileType {
    public static final BssFileType INSTANCE = new BssFileType();

    private BssFileType() {
        super(BssLanguage.INSTANCE);
    }

    @Override
    public @NotNull String getName() {
        return "Bss";
    }

    @Override
    public @NotNull String getDescription() {
        return "BubuUI stylesheet (.bss)";
    }

    @Override
    public @NotNull String getDefaultExtension() {
        return "bss";
    }

    @Override
    public @Nullable Icon getIcon() {
        return null;
    }
}
