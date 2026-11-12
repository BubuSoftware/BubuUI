package com.eclipseui.rider;

import com.intellij.lang.Language;

/** IntelliJ Language for .ui templates. Referenced from plugin.xml by ID. */
public final class EclipseUiLanguage extends Language {
    public static final EclipseUiLanguage INSTANCE = new EclipseUiLanguage();

    private EclipseUiLanguage() {
        super("EclipseUI");
    }
}
