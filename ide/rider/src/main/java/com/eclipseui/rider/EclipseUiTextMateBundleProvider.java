package com.eclipseui.rider;

import com.intellij.ide.plugins.IdeaPluginDescriptor;
import com.intellij.ide.plugins.PluginManagerCore;
import com.intellij.openapi.application.PathManager;
import com.intellij.openapi.extensions.PluginId;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.plugins.textmate.api.TextMateBundleProvider;

import java.io.IOException;
import java.io.InputStream;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.List;

/**
 * Contributes the VS Code-shaped TextMate bundle to the JetBrains TextMate plugin.
 *
 * <p>The platform reads bundles from disk, not from the classpath, so the resources
 * assembled by {@code syncTextMateBundle} (see build.gradle.kts) are extracted into
 * the plugin directory. The versioned folder makes plugin upgrades refresh the
 * grammars instead of leaving stale copies behind.
 *
 * <p>Files are never checked into this module: the grammars and language
 * configurations come from {@code ide/vscode}, which is the single source of truth
 * shared by both shells.
 */
public final class EclipseUiTextMateBundleProvider implements TextMateBundleProvider {

    private static final String PLUGIN_ID = "com.eclipseui.rider";
    private static final String RESOURCE_PREFIX = "/textmate/";
    private static final String BUNDLE_NAME = "eclipseui";

    private static final List<String> FILES = List.of(
            "package.json",
            "syntaxes/eclipseui.tmLanguage.json",
            "syntaxes/eustyle.tmLanguage.json",
            "language-configuration.json",
            "eustyle-language-configuration.json");

    @Override
    public @NotNull List<PluginBundle> getBundles() {
        return List.of(new PluginBundle(BUNDLE_NAME, extractBundle()));
    }

    private static Path extractBundle() {
        IdeaPluginDescriptor descriptor = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID));
        String version = descriptor == null ? "dev" : descriptor.getVersion();
        Path root = descriptor == null
                ? Paths.get(PathManager.getSystemPath(), "eclipseui", "textmate", version)
                : descriptor.getPluginPath().resolve("bundles").resolve(version);

        try {
            for (String name : FILES) {
                Path target = root.resolve(name);
                Files.createDirectories(target.getParent());
                try (InputStream stream = EclipseUiTextMateBundleProvider.class
                        .getResourceAsStream(RESOURCE_PREFIX + name)) {
                    if (stream == null) {
                        throw new IllegalStateException(
                                "Missing bundled TextMate resource " + name
                                        + " -- run the Gradle syncTextMateBundle task.");
                    }
                    Files.copy(stream, target, StandardCopyOption.REPLACE_EXISTING);
                }
            }
        } catch (IOException ex) {
            throw new UncheckedIOException("Cannot extract the EclipseUI TextMate bundle", ex);
        }
        return root;
    }
}
