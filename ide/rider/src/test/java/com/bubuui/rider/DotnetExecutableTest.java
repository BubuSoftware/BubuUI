package com.bubuui.rider;

import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;
import static org.junit.Assume.assumeTrue;

/** macOS GUI launches may inherit only the system PATH, without shell startup files. */
public final class DotnetExecutableTest {
    private static final String HOME = "/Users/developer";
    private static final Map<String, String> GUI_ENV = Map.of("PATH", "/usr/bin:/bin:/usr/sbin:/sbin");

    @Rule
    public final TemporaryFolder temporaryFolder = new TemporaryFolder();

    @Test
    public void productionDiscoveryRequiresAnExecutableRegularFile() throws IOException {
        assumeTrue(System.getProperty("os.name").toLowerCase(Locale.ROOT).startsWith("mac"));
        Path directoryEntry = temporaryFolder.newFolder("directory-entry").toPath();
        Files.createDirectory(directoryEntry.resolve("dotnet"));
        Path firstSdk = temporaryFolder.newFolder("first-sdk").toPath();
        Path firstHost = Files.createFile(firstSdk.resolve("dotnet"));
        Files.setPosixFilePermissions(firstHost, PosixFilePermissions.fromString("rw-------"));
        Path fallbackSdk = temporaryFolder.newFolder("SDK 空格 fallback").toPath();
        Path fallbackHost = Files.createFile(fallbackSdk.resolve("dotnet"));
        Files.setPosixFilePermissions(fallbackHost, PosixFilePermissions.fromString("rwx------"));
        Map<String, String> environment = Map.of(
                "PATH", directoryEntry + ":" + firstSdk + ":" + GUI_ENV.get("PATH"),
                "DOTNET_ROOT", fallbackSdk.toString());

        assertTrue(Files.isExecutable(directoryEntry.resolve("dotnet")));
        assertEquals(fallbackHost.toString(), DotnetExecutable.resolve("", environment));

        Files.setPosixFilePermissions(firstHost, PosixFilePermissions.fromString("rwx------"));
        assertEquals(firstHost.toString(), DotnetExecutable.resolve("", environment));
    }

    @Test
    public void serverEnvironmentReplacesAnInheritedRootWithTheSelectedSdk() throws IOException {
        Path oldSdk = temporaryFolder.newFolder("old-sdk").toPath();
        Files.createDirectory(oldSdk.resolve("sdk"));
        Path oldHost = Files.createFile(oldSdk.resolve("dotnet"));
        Path selectedSdk = temporaryFolder.newFolder("selected SDK 中文").toPath();
        Files.createDirectory(selectedSdk.resolve("sdk"));
        Path selectedHost = Files.createFile(selectedSdk.resolve("dotnet"));
        Map<String, String> environment = new HashMap<>(Map.of(
                "DOTNET_ROOT", oldSdk.toString(), "DOTNET_HOST_PATH", oldHost.toString()));

        String executable = DotnetExecutable.resolve(selectedHost.toString(), environment);
        environment.putAll(DotnetExecutable.serverEnvironment(executable));

        assertEquals(Map.of("DOTNET_ROOT", selectedSdk.toRealPath().toString(),
                "DOTNET_HOST_PATH", selectedHost.toRealPath().toString()), environment);
    }

    @Test
    public void serverEnvironmentResolvesSymlinksToTheActualSdkInstallation() throws IOException {
        assumeTrue(System.getProperty("os.name").toLowerCase(Locale.ROOT).startsWith("mac"));
        Path sdk = temporaryFolder.newFolder("real-sdk").toPath();
        Files.createDirectory(sdk.resolve("sdk"));
        Path host = Files.createFile(sdk.resolve("dotnet"));
        Path bin = temporaryFolder.newFolder("bin").toPath();
        Path link = Files.createSymbolicLink(bin.resolve("dotnet"), host);

        assertEquals(Map.of("DOTNET_ROOT", sdk.toRealPath().toString(),
                        "DOTNET_HOST_PATH", host.toRealPath().toString()),
                DotnetExecutable.serverEnvironment(link.toString()));
    }

    @Test
    public void serverEnvironmentDoesNotTreatAWrapperDirectoryAsAnSdkRoot() throws IOException {
        Path wrapper = Files.createFile(temporaryFolder.newFolder("wrappers").toPath().resolve("dotnet"));
        assertEquals(Map.of("DOTNET_HOST_PATH", wrapper.toRealPath().toString()),
                DotnetExecutable.serverEnvironment(wrapper.toString()));
    }

    @Test
    public void serverEnvironmentPreservesAnUnresolvedAbsoluteHostForTheProcessError() throws IOException {
        Path missingHost = temporaryFolder.newFolder("missing SDK").toPath().resolve("dotnet");
        assertEquals(Map.of("DOTNET_HOST_PATH", missingHost.toString()),
                DotnetExecutable.serverEnvironment(missingHost.toString()));
    }

    @Test
    public void relativeCommandsDoNotOverrideTheInheritedDotnetEnvironment() {
        for (String executable : new String[]{"dotnet", "./dotnet", "tools/dotnet", "../sdk/dotnet"}) {
            assertEquals(executable, Map.of(), DotnetExecutable.serverEnvironment(executable));
        }
    }

    @Test
    public void explicitSettingWinsOverPathAndInstallationDiscovery() {
        assertEquals("/Volumes/Developer Tools/自定义 .NET/dotnet", DotnetExecutable.resolve(
                "  /Volumes/Developer Tools/自定义 .NET/dotnet  ", "Mac OS X", HOME,
                Map.of("PATH", "/opt/homebrew/bin", "DOTNET_ROOT", "/usr/local/share/dotnet"),
                path -> { throw new AssertionError("An explicit setting must not trigger discovery"); }));
    }

    @Test
    public void pathTakesPriorityOverDotnetRootAndDefaultInstallations() {
        assertEquals(nativePath("/custom/sdk/dotnet"), resolve(
                Map.of("PATH", "/usr/bin:/custom/sdk:/opt/homebrew/bin",
                        "DOTNET_ROOT", "/usr/local/share/dotnet"),
                "/custom/sdk/dotnet", "/opt/homebrew/bin/dotnet", "/usr/local/share/dotnet/dotnet"));
    }

    @Test
    public void pathSearchSkipsMissingOrNonExecutableCandidates() {
        assertEquals(nativePath("/second/sdk/dotnet"), resolve(
                Map.of("PATH", "/missing/sdk:/not-executable/sdk:/second/sdk"),
                "/second/sdk/dotnet"));
    }

    @Test
    public void dotnetRootWorksWithoutTheShellPath() {
        assertEquals(nativePath("/Volumes/Developer Tools/SDK 中文/dotnet"), resolve(
                Map.of("PATH", "/usr/bin:/bin", "DOTNET_ROOT", "/Volumes/Developer Tools/SDK 中文"),
                "/Volumes/Developer Tools/SDK 中文/dotnet", HOME + "/.dotnet/dotnet"));
    }

    @Test
    public void userInstallationWorksWhenRiderIsOpenedFromTheDock() {
        assertEquals(nativePath(HOME + "/.dotnet/dotnet"), resolve(GUI_ENV,
                HOME + "/.dotnet/dotnet", "/usr/local/share/dotnet/dotnet"));
    }

    @Test
    public void homeDirectoryMayContainSpacesAndUnicode() {
        String userHome = "/Users/开发 User";
        Path executable = Path.of(userHome, ".dotnet", "dotnet").toAbsolutePath();
        assertEquals(executable.toString(), DotnetExecutable.resolve(
                "", "Mac OS X", userHome, GUI_ENV, executable::equals));
    }

    @Test
    public void officialAndHomebrewInstallationsWorkWithoutPathEntries() {
        for (String installation : new String[]{
                "/usr/local/share/dotnet/dotnet",
                "/opt/homebrew/opt/dotnet/libexec/dotnet",
                "/usr/local/opt/dotnet/libexec/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/local/bin/dotnet"}) {
            assertEquals(installation, nativePath(installation), resolve(GUI_ENV, installation));
        }
    }

    @Test
    public void defaultInstallationOrderIsStableWhenSeveralHostsExist() {
        assertEquals(nativePath("/usr/local/share/dotnet/dotnet"), resolve(GUI_ENV,
                "/usr/local/share/dotnet/dotnet", "/opt/homebrew/opt/dotnet/libexec/dotnet",
                "/usr/local/opt/dotnet/libexec/dotnet", "/opt/homebrew/bin/dotnet"));
    }

    @Test
    public void missingOrNonExecutableHostsFallBackToDotnet() {
        assertEquals("dotnet", resolve(GUI_ENV));
        assertEquals("dotnet", resolve(Map.of()));
    }

    @Test
    public void malformedEnvironmentEntriesDoNotBlockDiscovery() {
        assertEquals(nativePath(HOME + "/.dotnet/dotnet"), resolve(
                Map.of("PATH", "/invalid\0/path:/usr/bin", "DOTNET_ROOT", "/invalid\0/root"),
                HOME + "/.dotnet/dotnet"));
    }

    @Test
    public void unknownHomeDirectoryDoesNotBlockSystemInstallation() {
        Path executable = Path.of("/usr/local/share/dotnet/dotnet").toAbsolutePath();
        assertEquals(executable.toString(), DotnetExecutable.resolve(
                "", "Mac OS X", null, GUI_ENV, executable::equals));
    }

    @Test
    public void blankSettingUsesAutomaticDiscovery() {
        Path executable = Path.of(HOME, ".dotnet", "dotnet").toAbsolutePath();
        assertEquals(executable.toString(), DotnetExecutable.resolve(
                " \t ", "Mac OS X", HOME, GUI_ENV, executable::equals));
        assertEquals(executable.toString(), DotnetExecutable.resolve(
                null, "Mac OS X", HOME, GUI_ENV, executable::equals));
    }

    @Test
    public void otherPlatformsKeepTheirExistingPathBehavior() {
        for (String os : new String[]{"Windows 11", "Linux"}) {
            assertEquals("dotnet", DotnetExecutable.resolve("", os, HOME, GUI_ENV,
                    path -> { throw new AssertionError("macOS discovery must not run on " + os); }));
        }
    }

    private static String resolve(Map<String, String> environment, String... executables) {
        Set<Path> paths = Stream.of(executables).map(Path::of)
                .map(Path::toAbsolutePath).collect(Collectors.toSet());
        return DotnetExecutable.resolve("", "Mac OS X", HOME, environment, paths::contains);
    }

    private static String nativePath(String path) {
        return Path.of(path).toAbsolutePath().toString();
    }
}
