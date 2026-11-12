package com.eclipseui.rider;

import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import javax.swing.Icon;

/** Binds *.eustyle to {@link EustyleLanguage}; the TextMate highlighter is wired in plugin.xml. */
public final class EustyleFileType extends LanguageFileType {
    public static final EustyleFileType INSTANCE = new EustyleFileType();

    private EustyleFileType() {
        super(EustyleLanguage.INSTANCE);
    }

    @Override
    public @NotNull String getName() {
        return "Eustyle";
    }

    @Override
    public @NotNull String getDescription() {
        return "EclipseUI stylesheet (.eustyle)";
    }

    @Override
    public @NotNull String getDefaultExtension() {
        return "eustyle";
    }

    @Override
    public @Nullable Icon getIcon() {
        return null;
    }
}
