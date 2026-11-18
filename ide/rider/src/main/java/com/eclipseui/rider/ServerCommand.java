package com.eclipseui.rider;

import com.intellij.ide.plugins.IdeaPluginDescriptor;
import com.intellij.ide.plugins.PluginManagerCore;
import com.intellij.openapi.application.PathManager;
import com.intellij.openapi.extensions.PluginId;
import com.intellij.openapi.project.Project;
import com.intellij.util.EnvironmentUtil;

import java.io.IOException;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.List;

/**
 * Resolves the shared language server DLL and builds its command line.
 *
 * <p>Release packages embed the same published server as VS Code in server/.
 * Explicit settings and the environment override it; repository builds remain
 * a fallback for extension development.
 */
public final class ServerCommand {

    public static final String SERVER_DLL = "EclipseUI.LanguageServer.dll";
    private static final String PLUGIN_ID = "com.eclipseui";

    private ServerCommand() {
    }

    /** @return the DLL to launch, or null when nothing plausible exists. */
    public static Path resolveServer(Project project) {
        EclipseUiSettings.State state = EclipseUiSettings.getInstance().getState();

        Path configured = usable(state.serverDll);
        if (configured != null) return configured;

        Path fromEnvironment = usable(System.getenv("ECLIPSEUI_SERVER_DLL"));
        if (fromEnvironment != null) return fromEnvironment;

        IdeaPluginDescriptor descriptor = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID));
        if (descriptor != null) {
            Path bundled = usable(descriptor.getPluginPath().resolve("server").resolve(SERVER_DLL).toString());
            if (bundled != null) return bundled;
        }

        String basePath = project.getBasePath();
        if (basePath == null || basePath.isBlank()) return null;

        Path root = repositoryRoot(Paths.get(basePath));
        if (root == null) return null;

        Path[] candidates = {
                root.resolve("ide").resolve("vscode").resolve("server").resolve(SERVER_DLL),
                root.resolve("ide").resolve("EclipseUI.LanguageServer").resolve("bin")
                        .resolve("Release").resolve("net10.0").resolve(SERVER_DLL),
                root.resolve("ide").resolve("EclipseUI.LanguageServer").resolve("bin")
                        .resolve("Debug").resolve("net10.0").resolve(SERVER_DLL),
        };
        for (Path candidate : candidates) {
            if (Files.isRegularFile(candidate)) return candidate;
        }
        return null;
    }

    /** Mirrors serverArguments() in ide/vscode/client/extension.js. */
    public static List<String> build(Project project, Path server) {
        EclipseUiSettings.State state = EclipseUiSettings.getInstance().getState();

        List<String> args = new ArrayList<>();
        args.add(DotnetExecutable.resolve(state.dotnet, EnvironmentUtil.getEnvironmentMap()));
        args.add(server.toAbsolutePath().toString());
        args.add("--stdio");
        // LSP4IJ opens file URIs; the shared server supplies a read-only C# cache.
        args.add("--materialize-decompiled");

        String basePath = project.getBasePath();
        if (basePath != null && !basePath.isBlank()) {
            args.add("--workspace");
            args.add(basePath);
        }
        if (!blank(state.runtimeDll)) {
            args.add("--runtime");
            args.add(state.runtimeDll.trim());
        }
        if (!blank(state.extraAssemblies)) {
            for (String assembly : state.extraAssemblies.split("[;\\r\\n]+")) {
                if (!assembly.isBlank()) {
                    args.add("--assembly");
                    args.add(assembly.trim());
                }
            }
        }

        // stdout carries JSON-RPC; logs go to a file and stderr (LSP4IJ shows the
        // latter in the LSP console, which is where "0 project(s)" is diagnosed).
        args.add("--log");
        args.add(Paths.get(PathManager.getLogPath(), "eclipseui-server.log").toString());
        args.add("--log-stderr");
        return args;
    }

    private static Path usable(String value) {
        if (value == null || value.isBlank()) return null;
        Path path = Paths.get(value.trim());
        return Files.isRegularFile(path) ? path : null;
    }

    private static Path repositoryRoot(Path start) {
        Path current = start.toAbsolutePath();
        while (current != null) {
            if (Files.exists(current.resolve(".git")) || hasSolution(current)) return current;
            current = current.getParent();
        }
        return null;
    }

    private static boolean hasSolution(Path directory) {
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(directory, "*.sln*")) {
            return stream.iterator().hasNext();
        } catch (IOException ex) {
            return false;
        }
    }

    private static boolean blank(String value) {
        return value == null || value.isBlank();
    }
}
