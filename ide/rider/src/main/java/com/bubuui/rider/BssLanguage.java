package com.bubuui.rider;

import com.intellij.lang.Language;

/** IntelliJ Language for .bss stylesheets. Referenced from plugin.xml by ID. */
public final class BssLanguage extends Language {
    public static final BssLanguage INSTANCE = new BssLanguage();

    private BssLanguage() {
        super("Bss");
    }
}
