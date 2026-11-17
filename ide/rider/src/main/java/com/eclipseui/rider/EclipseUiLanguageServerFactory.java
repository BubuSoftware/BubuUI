package com.eclipseui.rider;

import com.intellij.notification.NotificationGroupManager;
import com.intellij.notification.NotificationType;
import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.project.Project;
import com.redhat.devtools.lsp4ij.LanguageServerFactory;
import com.redhat.devtools.lsp4ij.server.StreamConnectionProvider;
import org.jetbrains.annotations.NotNull;

/** LSP4IJ factory referenced from plugin.xml (com.redhat.devtools.lsp4ij.server). */
public final class EclipseUiLanguageServerFactory implements LanguageServerFactory {

    @Override
    public @NotNull StreamConnectionProvider createConnectionProvider(@NotNull Project project) {
        if (ServerCommand.resolveServer(project) == null) {
            ApplicationManager.getApplication().invokeLater(() -> NotificationGroupManager.getInstance()
                    .getNotificationGroup("EclipseUI")
                    .createNotification(
                            "EclipseUI language server not found. Run ide/rider/build.sh on macOS/Linux "
                                    + "or pwsh -File ide/build.ps1 on Windows (publishes to ide/vscode/server), "
                                    + "or set the DLL path in "
                                    + "Settings | Languages & Frameworks | EclipseUI.",
                            NotificationType.WARNING)
                    .notify(project));
        }
        return new EclipseUiServerConnectionProvider(project);
    }
}
