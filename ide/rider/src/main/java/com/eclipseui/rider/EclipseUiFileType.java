package com.eclipseui.rider;

import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import javax.swing.Icon;

/** Binds *.ui to {@link EclipseUiLanguage}; the TextMate highlighter is wired in plugin.xml. */
public final class EclipseUiFileType extends LanguageFileType {
    public static final EclipseUiFileType INSTANCE = new EclipseUiFileType();

    private EclipseUiFileType() {
        super(EclipseUiLanguage.INSTANCE);
    }

    @Override
    public @NotNull String getName() {
        return "EclipseUI";
    }

    @Override
    public @NotNull String getDescription() {
        return "EclipseUI template (.ui)";
    }

    @Override
    public @NotNull String getDefaultExtension() {
        return "ui";
    }

    @Override
    public @Nullable Icon getIcon() {
        return null;
    }
}
