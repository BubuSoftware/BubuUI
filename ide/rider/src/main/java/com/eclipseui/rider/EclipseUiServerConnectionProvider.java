package com.eclipseui.rider;

import com.intellij.openapi.project.Project;
import com.redhat.devtools.lsp4ij.server.ProcessStreamConnectionProvider;

import java.nio.file.Path;

/** Starts "dotnet EclipseUI.LanguageServer.dll --stdio ..." over stdio. */
public final class EclipseUiServerConnectionProvider extends ProcessStreamConnectionProvider {

    public EclipseUiServerConnectionProvider(Project project) {
        Path server = ServerCommand.resolveServer(project);
        if (server == null) {
            // Failing loudly here puts a readable reason in LSP4IJ's LSP console.
            // The factory also raises a balloon notification with the fix.
            throw new IllegalStateException(
                    "EclipseUI.LanguageServer.dll not found. Run ide/build.ps1 to publish the shared "
                            + "server, or set the DLL path in Settings | Languages & Frameworks | EclipseUI.");
        }
        super.setCommands(ServerCommand.build(project, server));
    }
}
