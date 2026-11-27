import org.jetbrains.intellij.platform.gradle.TestFrameworkType
import org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask

plugins {
    id("java")
    id("org.jetbrains.intellij.platform") version "2.20.1"
}

group = "com.bubuui"
version = providers.gradleProperty("pluginVersion").get()

java {
    sourceCompatibility = JavaVersion.VERSION_21
    targetCompatibility = JavaVersion.VERSION_21
}

tasks.withType<JavaCompile>().configureEach {
    // Rider 2024.2 requires Java 21. Use the current build JDK without requiring
    // another toolchain installation, while checking against Java 21 APIs.
    options.release.set(21)
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // Rider (RD) is the target IDE. LSP4IJ and JetBrains TextMate are the only
        // platform dependencies: the plugin is a thin shell, all semantics live in
        // BubuUI.LanguageServer (design/06 "one shared asset, two thin shells").
        val localPlatformPath = providers.gradleProperty("localPlatformPath")
        if (localPlatformPath.isPresent) {
            local(localPlatformPath.get())
        } else {
            create(providers.gradleProperty("platformType"), providers.gradleProperty("platformVersion"))
        }
        bundledPlugin("org.jetbrains.plugins.textmate")
        plugin("com.redhat.devtools.lsp4ij", providers.gradleProperty("lsp4ijVersion").get())
        testFramework(TestFrameworkType.Platform)
    }
    testImplementation("junit:junit:4.13.2")
    // The 2024.2 platform test framework does not declare this runtime dependency.
    testRuntimeOnly("org.opentest4j:opentest4j:1.3.0")
}

intellijPlatform {
    pluginConfiguration {
        id = "com.bubuui"
        name = "BubuUI"
        version = providers.gradleProperty("pluginVersion").get()
        description = """
            BubuUI .bui templates and .bss stylesheets: diagnostics, completion,
            hover, navigation, rename and more, powered by the same stdio language
            server used by the VS Code extension.
        """.trimIndent()

        ideaVersion {
            // LSP4IJ requires 2024.2+. No untilBuild: a plugin that hard-caps its
            // upper bound breaks on every new Rider release for no reason.
            sinceBuild = "242"
        }
    }
}

// ---------------------------------------------------------------------------
// One shared TextMate asset: the grammars live in ide/vscode/syntaxes and are
// NEVER copied into this source tree. At build time we assemble a VS Code-shaped
// bundle (package.json + grammars + language configurations) under resources,
// which BubuUiTextMateBundleProvider extracts to disk at runtime.
// ---------------------------------------------------------------------------
val vscodeDir = layout.projectDirectory.dir("../vscode")
val bundleTemplate = layout.projectDirectory.dir("src/main/textmate")
val generatedBundle = layout.buildDirectory.dir("generated/textmate")

val syncTextMateBundle by tasks.registering(Sync::class) {
    group = "bubuui"
    description = "Assemble the TextMate bundle from the VS Code extension assets"

    from(vscodeDir.dir("syntaxes")) { into("syntaxes") }
    from(vscodeDir.dir("highlighting")) { into("highlighting") }
    from(vscodeDir.file("language-configuration.json"))
    from(vscodeDir.file("bss-language-configuration.json"))
    from(bundleTemplate.file("package.json"))
    into(generatedBundle)
}

tasks.named<ProcessResources>("processResources") {
    from(syncTextMateBundle) { into("textmate") }
    dependsOn(syncTextMateBundle)
}

// Use the same published server as VS Code, placed where ServerCommand resolves it.
tasks.withType<PrepareSandboxTask>().configureEach {
    from(vscodeDir.dir("server")) { into(pluginName.map { "$it/server" }) }
    doFirst {
        check(vscodeDir.file("server/BubuUI.LanguageServer.dll").asFile.isFile) {
            "Publish the shared server first: pwsh -File ../build.ps1"
        }
    }
}

// `gradle verifyShell` runs the same checks as `node test/plugin.test.js`.
// Plain node needs no JVM at all; the Gradle task is just a convenient entry
// point on machines that already have the toolchain.
tasks.register<Exec>("verifyShell") {
    group = "bubuui"
    description = "Static contract checks for the Rider shell (no IDE download needed)"
    dependsOn(syncTextMateBundle)
    workingDir(projectDir)
    commandLine(
        if (System.getProperty("os.name").startsWith("Windows")) "node.exe" else "node",
        "test/plugin.test.js")
}
