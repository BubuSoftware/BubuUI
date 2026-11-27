package com.bubuui.rider;

import com.intellij.lang.Language;

/** IntelliJ Language for .bui templates. Referenced from plugin.xml by ID. */
public final class BubuUiLanguage extends Language {
    public static final BubuUiLanguage INSTANCE = new BubuUiLanguage();

    private BubuUiLanguage() {
        super("BubuUI");
    }
}
