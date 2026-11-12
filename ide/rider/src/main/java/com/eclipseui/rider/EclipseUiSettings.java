package com.eclipseui.rider;

import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.components.PersistentStateComponent;
import com.intellij.openapi.components.State;
import com.intellij.openapi.components.Storage;
import org.jetbrains.annotations.NotNull;

/**
 * Mirror of the four VS Code settings (eclipseui.server.dotnet / server.path /
 * runtime.path / catalog.assemblies). Keeping the same knobs means the same
 * server binary can be pointed at from either IDE.
 */
@State(name = "EclipseUISettings", storages = @Storage("eclipseui.xml"))
public final class EclipseUiSettings implements PersistentStateComponent<EclipseUiSettings.State> {

    private State state = new State();

    public static EclipseUiSettings getInstance() {
        return ApplicationManager.getApplication().getService(EclipseUiSettings.class);
    }

    public @NotNull State getState() {
        return state;
    }

    @Override
    public void loadState(@NotNull State state) {
        this.state = state;
    }

    public static final class State {
        /** dotnet executable; empty = "dotnet" from PATH. */
        public String dotnet = "";
        /** Path to EclipseUI.LanguageServer.dll; empty = auto-discover in the repository. */
        public String serverDll = "";
        /** Optional EclipseUI.Runtime.dll (legacy catalog fallback). */
        public String runtimeDll = "";
        /** Extra catalog assemblies, separated by ';' or newlines. */
        public String extraAssemblies = "";
    }
}
