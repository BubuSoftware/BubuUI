package com.bubuui.rider;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.function.Predicate;

/** Finds .NET even when Rider is opened from Finder with a minimal PATH. */
final class DotnetExecutable {
    private DotnetExecutable() {
    }

    static String resolve(String configured, Map<String, String> environment) {
        return resolve(configured, System.getProperty("os.name"), System.getProperty("user.home"),
                environment, path -> Files.isRegularFile(path) && Files.isExecutable(path));
    }

    /** Keep MSBuildLocator on the selected SDK, including symlinked installations. */
    static Map<String, String> serverEnvironment(String executable) {
        Path host = Path.of(executable);
        if (!host.isAbsolute()) return Map.of();
        try {
            host = host.toRealPath();
        } catch (IOException ignored) {
            // Preserve the normal launch error for a missing/inaccessible executable.
        }
        Path root = host.getParent();
        if (root != null && Files.isDirectory(root.resolve("sdk"))) {
            return Map.of("DOTNET_HOST_PATH", host.toString(), "DOTNET_ROOT", root.toString());
        }
        // A custom launcher script may manage its own SDK environment.
        return Map.of("DOTNET_HOST_PATH", host.toString());
    }

    static String resolve(String configured, String osName, String userHome,
                          Map<String, String> environment, Predicate<Path> isExecutable) {
        if (configured != null && !configured.isBlank()) return configured.trim();
        if (!osName.toLowerCase(Locale.ROOT).startsWith("mac")) return "dotnet";

        List<String> directories = new ArrayList<>();
        // Honour the IDE's inherited PATH before checking standard installations.
        for (String directory : environment.getOrDefault("PATH", "").split(":")) {
            if (!directory.isBlank()) directories.add(directory);
        }
        String dotnetRoot = environment.get("DOTNET_ROOT");
        if (dotnetRoot != null && !dotnetRoot.isBlank()) directories.add(dotnetRoot);
        if (userHome != null && !userHome.isBlank()) directories.add(userHome + "/.dotnet");
        directories.addAll(List.of(
                "/usr/local/share/dotnet",
                "/opt/homebrew/opt/dotnet/libexec",
                "/usr/local/opt/dotnet/libexec",
                "/opt/homebrew/bin",
                "/usr/local/bin"));

        for (String directory : directories) {
            try {
                Path candidate = Path.of(directory).resolve("dotnet").toAbsolutePath();
                if (isExecutable.test(candidate)) return candidate.toString();
            } catch (InvalidPathException ignored) {
                // A malformed environment entry should not block other installations.
            }
        }
        // Keep the normal process error when no SDK installation can be found.
        return "dotnet";
    }
}
