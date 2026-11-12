package com.eclipseui.rider;

import com.intellij.lang.Language;

/** IntelliJ Language for .eustyle stylesheets. Referenced from plugin.xml by ID. */
public final class EustyleLanguage extends Language {
    public static final EustyleLanguage INSTANCE = new EustyleLanguage();

    private EustyleLanguage() {
        super("Eustyle");
    }
}
