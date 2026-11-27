package com.bubuui.rider;

import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.components.PersistentStateComponent;
import com.intellij.openapi.components.State;
import com.intellij.openapi.components.Storage;
import org.jetbrains.annotations.NotNull;

/**
 * Mirror of the four VS Code settings (bubuui.server.dotnet / server.path /
 * runtime.path / catalog.assemblies). Keeping the same knobs means the same
 * server binary can be pointed at from either IDE.
 */
@State(name = "BubuUISettings", storages = @Storage("bubuui.xml"))
public final class BubuUiSettings implements PersistentStateComponent<BubuUiSettings.State> {

    private State state = new State();

    public static BubuUiSettings getInstance() {
        return ApplicationManager.getApplication().getService(BubuUiSettings.class);
    }

    public @NotNull State getState() {
        return state;
    }

    @Override
    public void loadState(@NotNull State state) {
        this.state = state;
    }

    public static final class State {
        /** dotnet executable; empty = PATH, then standard .NET locations on macOS. */
        public String dotnet = "";
        /** Path to BubuUI.LanguageServer.dll; empty = auto-discover in the repository. */
        public String serverDll = "";
        /** Optional BubuUI.Runtime.dll (legacy catalog fallback). */
        public String runtimeDll = "";
        /** Extra catalog assemblies, separated by ';' or newlines. */
        public String extraAssemblies = "";
    }
}
