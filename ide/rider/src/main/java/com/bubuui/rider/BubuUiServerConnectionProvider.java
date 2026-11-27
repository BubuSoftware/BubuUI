package com.bubuui.rider;

import com.intellij.openapi.project.Project;
import com.redhat.devtools.lsp4ij.server.ProcessStreamConnectionProvider;

import java.nio.file.Path;
import java.util.List;

/** Starts "dotnet BubuUI.LanguageServer.dll --stdio ..." over stdio. */
public final class BubuUiServerConnectionProvider extends ProcessStreamConnectionProvider {

    public BubuUiServerConnectionProvider(Project project) {
        Path server = ServerCommand.resolveServer(project);
        if (server == null) {
            // Failing loudly here puts a readable reason in LSP4IJ's LSP console.
            // The factory also raises a balloon notification with the fix.
            throw new IllegalStateException(
                    "BubuUI.LanguageServer.dll not found. Run ide/rider/build.sh on macOS/Linux "
                            + "or pwsh -File ide/build.ps1 on Windows to publish the shared "
                            + "server, or set the DLL path in Settings | Languages & Frameworks | BubuUI.");
        }
        List<String> commands = ServerCommand.build(project, server);
        super.setCommands(commands);
        // MSBuildLocator's SDK fallback must use the same host even if the GUI's
        // PATH has no dotnet, or the user selected a different SDK installation.
        super.setUserEnvironmentVariables(DotnetExecutable.serverEnvironment(commands.get(0)));
    }
}
