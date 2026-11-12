'use strict';
// Static contract checks for the Rider shell.
//
// The IDE cannot compile this module without a JDK + Gradle + a Rider SDK, but
// almost every integration mistake here is a wiring mistake (extension point
// name, class name, language id, grammar path, watcher glob). Those are plain
// text, so they are checked as text, with zero dependencies:
//
//   node ide/rider/test/plugin.test.js
//
// The assertions deliberately cross-reference the VS Code shell and the LSP
// server, so "one shared asset, two thin shells" stays true mechanically.

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const ide = path.resolve(root, '..');
const vscode = path.join(ide, 'vscode');
const server = path.join(ide, 'EclipseUI.LanguageServer');

let passed = 0;
function check(name, fn) {
    try {
        fn();
        passed++;
        console.log('  ok   ' + name);
    } catch (error) {
        console.error('  FAIL ' + name + '\n       ' + error.message);
        process.exitCode = 1;
    }
}
function read(abs) {
    return fs.readFileSync(abs, 'utf8');
}
function rider(rel) {
    return read(path.join(root, rel));
}
function assert(condition, message) {
    if (!condition) throw new Error(message);
}
function has(text, needle, message) {
    assert(text.includes(needle), (message || 'missing') + ': ' + needle);
}

const pluginXml = rider('src/main/resources/META-INF/plugin.xml');
const gradle = rider('build.gradle.kts');
const properties = rider('gradle.properties');
const bundleJson = JSON.parse(rider('src/main/textmate/package.json'));
const vscodePackage = JSON.parse(read(path.join(vscode, 'package.json')));
const clientJs = read(path.join(vscode, 'client', 'extension.js'));
const hostCs = read(path.join(server, 'Server', 'LanguageServerHost.cs'));
const connectionCs = read(path.join(server, 'Protocol', 'JsonRpcConnection.cs'));
const projectTests = read(path.join(ide, '..', 'tests', 'EclipseUI.Ide.Tests', 'ProjectSystemTests.cs'));
const protocolTests = read(path.join(ide, '..', 'tests', 'EclipseUI.Ide.Tests', 'ProtocolTests.cs'));

console.log('Rider shell contract');

check('plugin identity and platform dependencies', () => {
    has(pluginXml, '<id>com.eclipseui.rider</id>');
    has(pluginXml, '<idea-version since-build="242.*"/>');
    has(pluginXml, '<depends>com.intellij.modules.platform</depends>');
    has(pluginXml, '<depends>com.redhat.devtools.lsp4ij</depends>');
    has(pluginXml, '<depends>org.jetbrains.plugins.textmate</depends>');
    has(gradle, 'create(providers.gradleProperty("platformType"), providers.gradleProperty("platformVersion"))');
    has(gradle, 'plugin("com.redhat.devtools.lsp4ij"');
    has(gradle, 'bundledPlugin("org.jetbrains.plugins.textmate")');
    assert(/platformType=RD\b/.test(properties), 'gradle.properties must target Rider (RD)');
    assert(/platformVersion=2024\.2\b/.test(properties), 'Rider 2024.2 == build 242');
    has(gradle, 'sinceBuild = "242"');
    has(properties, 'lsp4ijVersion=0.21.0');
});

check('LSP4IJ server + language mappings use the official extension points', () => {
    has(pluginXml, '<extensions defaultExtensionNs="com.redhat.devtools.lsp4ij">');
    assert(/<server\s+id="eclipseui"[\s\S]*?factoryClass="com\.eclipseui\.rider\.EclipseUiLanguageServerFactory"/.test(pluginXml),
        'server EP needs id=eclipseui and the factory class');
    const mappings = [...pluginXml.matchAll(/<languageMapping\s+([^>]+)\/>/g)].map(m => m[1]);
    assert(mappings.length === 2, 'exactly two languageMapping entries expected, got ' + mappings.length);
    has(mappings.join('\n'), 'language="EclipseUI"');
    has(mappings.join('\n'), 'language="Eustyle"');
    has(mappings.join('\n'), 'languageId="eclipseui"');
    has(mappings.join('\n'), 'languageId="eustyle"');
    for (const mapping of mappings) has(mapping, 'serverId="eclipseui"');
    // The pattern mapping is a documented fallback, not an active duplicate.
    assert(!/^\s*<fileNamePatternMapping/m.test(pluginXml),
        'do not enable fileNamePatternMapping alongside languageMapping (double connection)');
});

check('Language + FileType pairs are declared for both extensions', () => {
    assert(/<fileType\s+name="EclipseUI"[\s\S]*?extensions="ui"[\s\S]*?implementationClass="com\.eclipseui\.rider\.EclipseUiFileType"/.test(pluginXml),
        '*.ui must bind to EclipseUiFileType');
    assert(/<fileType\s+name="Eustyle"[\s\S]*?extensions="eustyle"[\s\S]*?implementationClass="com\.eclipseui\.rider\.EustyleFileType"/.test(pluginXml),
        '*.eustyle must bind to EustyleFileType');
    has(rider('src/main/java/com/eclipseui/rider/EclipseUiLanguage.java'), 'super("EclipseUI")');
    has(rider('src/main/java/com/eclipseui/rider/EustyleLanguage.java'), 'super("Eustyle")');
    has(rider('src/main/java/com/eclipseui/rider/EclipseUiFileType.java'), 'EclipseUiLanguage.INSTANCE');
    has(rider('src/main/java/com/eclipseui/rider/EustyleFileType.java'), 'EustyleLanguage.INSTANCE');
});

check('TextMate highlighter is explicitly bound (custom FileType workaround)', () => {
    has(pluginXml, '<textmate.bundleProvider');
    has(pluginXml, 'implementation="com.eclipseui.rider.EclipseUiTextMateBundleProvider"');
    const highlighters = [...pluginXml.matchAll(/<editorHighlighterProvider\s+([^>]+)\/>/g)].map(m => m[1]);
    assert(highlighters.length === 2, 'two editorHighlighterProvider entries expected');
    has(highlighters.join('\n'), 'filetype="EclipseUI"');
    has(highlighters.join('\n'), 'filetype="Eustyle"');
    for (const entry of highlighters) {
        has(entry, 'org.jetbrains.plugins.textmate.language.syntax.highlighting.TextMateEditorHighlighterProvider');
    }
    const factories = [...pluginXml.matchAll(/<lang\.syntaxHighlighterFactory\s+([^>]+)\/>/g)].map(m => m[1]);
    assert(factories.length === 2, 'two lang.syntaxHighlighterFactory entries expected');
    has(factories.join('\n'), 'language="EclipseUI"');
    has(factories.join('\n'), 'language="Eustyle"');
    for (const entry of factories) {
        has(entry, 'org.jetbrains.plugins.textmate.language.syntax.highlighting.TextMateSyntaxHighlighterFactory');
    }
});

check('every plugin.xml class reference resolves to a real Java source file', () => {
    // Collect every FQN regardless of the attribute name (implementationClass /
    // factoryClass / instance / implementation / serviceImplementation).
    // Third-party classes (org.jetbrains.plugins.textmate.*) are checked above.
    const references = [...pluginXml.matchAll(/"(com\.eclipseui\.rider\.[A-Za-z0-9_]+)"/g)]
        .map(m => m[1]);
    assert(references.length >= 6, 'expected several platform class references, got ' + references.length);
    has(pluginXml, 'serviceImplementation="com.eclipseui.rider.EclipseUiSettings"');
    for (const fqn of new Set(references)) {
        const simple = fqn.slice(fqn.lastIndexOf('.') + 1);
        const file = path.join(root, 'src/main/java/com/eclipseui/rider', simple + '.java');
        assert(fs.existsSync(file), 'missing Java source for ' + fqn);
        has(read(file), 'class ' + simple, 'class name mismatch in ' + simple + '.java');
    }
});

check('grammars come from ide/vscode only (single shared asset)', () => {
    has(gradle, 'val vscodeDir = layout.projectDirectory.dir("../vscode")');
    has(gradle, 'from(vscodeDir.dir("syntaxes"))');
    has(gradle, 'from(vscodeDir.file("language-configuration.json"))');
    has(gradle, 'from(vscodeDir.file("eustyle-language-configuration.json"))');
    has(gradle, 'into("textmate")');
    has(gradle, 'val generatedBundle = layout.buildDirectory.dir("generated/textmate")');
    assert(!fs.existsSync(path.join(root, 'src/main/resources/textmate')),
        'do not check in a second copy of the grammars under src/main/resources');
    assert(!fs.existsSync(path.join(root, 'syntaxes')),
        'do not check in a syntaxes/ folder in the Rider module');
    has(rider('src/main/java/com/eclipseui/rider/EclipseUiTextMateBundleProvider.java'),
        'getResourceAsStream(RESOURCE_PREFIX + name)');
});

check('bundle package.json declares the same grammars/scopeNames as VS Code', () => {
    const grammars = bundleJson.contributes.grammars;
    assert(grammars.length === 2, 'bundle must contribute both grammars');
    for (const grammar of grammars) {
        const declared = grammar.path.replace(/^\.\//, '');
        assert(fs.existsSync(path.join(vscode, declared)), 'grammar not found: ' + declared);
        const source = JSON.parse(read(path.join(vscode, declared)));
        assert(source.scopeName === grammar.scopeName,
            'scopeName drift for ' + declared + ': ' + source.scopeName + ' vs ' + grammar.scopeName);
    }
    const vsGrammars = vscodePackage.contributes.grammars;
    assert(JSON.stringify(grammars.map(g => g.scopeName)) === JSON.stringify(vsGrammars.map(g => g.scopeName)),
        'bundle scopeName list must match the VS Code extension');
    assert(JSON.stringify(grammars[0].embeddedLanguages) === JSON.stringify(vsGrammars[0].embeddedLanguages),
        'embeddedLanguages must stay in sync with the VS Code extension');
    const extensions = bundleJson.contributes.languages.map(l => l.extensions[0]).sort();
    assert(JSON.stringify(extensions) === JSON.stringify(['.eustyle', '.ui']),
        'bundle language extensions must match the FileTypes');
});

check('server launch command mirrors the VS Code client', () => {
    const command = rider('src/main/java/com/eclipseui/rider/ServerCommand.java');
    const provider = rider('src/main/java/com/eclipseui/rider/EclipseUiServerConnectionProvider.java');
    const factory = rider('src/main/java/com/eclipseui/rider/EclipseUiLanguageServerFactory.java');
    has(command, 'EclipseUI.LanguageServer.dll');
    has(clientJs, "'server', 'EclipseUI.LanguageServer.dll'");
    for (const flag of ['--stdio', '--workspace', '--runtime', '--assembly', '--log', '--log-stderr']) {
        has(command, 'args.add("' + flag + '")', 'Rider command is missing flag');
        has(clientJs, "'" + flag + "'", 'VS Code client is missing flag');
    }
    has(command, 'root.resolve("ide").resolve("vscode").resolve("server")');
    has(command, '"Release").resolve("net10.0")');
    has(command, '"Debug").resolve("net10.0")');
    has(command, 'project.getBasePath()');
    has(provider, 'extends ProcessStreamConnectionProvider');
    has(provider, 'super.setCommands(ServerCommand.build(project, server))');
    has(factory, 'implements LanguageServerFactory');
    has(factory, 'createConnectionProvider(@NotNull Project project)');
    has(pluginXml, '<applicationService serviceImplementation="com.eclipseui.rider.EclipseUiSettings"/>');
});

check('watcher globs are identical in VS Code, Rider and the server', () => {
    const vsBlock = /const WATCH_PATTERNS = \[([\s\S]*?)\];/.exec(clientJs);
    assert(vsBlock, 'cannot find WATCH_PATTERNS in the VS Code client');
    const vsGlobs = [...vsBlock[1].matchAll(/'([^']+)'/g)].map(m => m[1]);

    const csBlock = /private static readonly string\[\] WatcherGlobs\s*=\s*\{([\s\S]*?)\};/.exec(hostCs);
    assert(csBlock, 'cannot find WatcherGlobs in LanguageServerHost.cs');
    const csGlobs = [...csBlock[1].matchAll(/"([^"]+)"/g)].map(m => m[1]);

    assert(vsGlobs.length === 6, 'expected 6 watcher globs, got ' + vsGlobs.length);
    assert(JSON.stringify(csGlobs) === JSON.stringify(vsGlobs),
        'server watcher globs drifted from the VS Code client: ' + JSON.stringify(csGlobs));
    has(hostCs, 'client/registerCapability');
    has(hostCs, 'workspace/didChangeWatchedFiles');
    has(connectionCs, 'public Task RequestAsync(');
    has(protocolTests, 'Session_RegistersWatchersDynamicallyForRider');
});

check('IntelliJ file URI forms are covered by server conversion tests', () => {
    has(projectTests, 'file:/D:/workspace/repo/Sample.ui');
    has(projectTests, 'file://D:/workspace/repo/Sample.ui');
    has(projectTests, 'file:///d%3A/workspace/repo/Sample.ui');
});

check('settings page and user guidance are wired', () => {
    has(pluginXml, '<applicationConfigurable');
    has(pluginXml, '<notificationGroup id="EclipseUI"');
    has(rider('src/main/java/com/eclipseui/rider/EclipseUiSettingsConfigurable.java'), 'implements Configurable');
    has(rider('src/main/java/com/eclipseui/rider/EclipseUiLanguageServerFactory.java'), 'NotificationGroupManager');
    const readme = read(path.join(ide, 'README.md'));
    has(readme, 'ide/rider');
    has(readme, 'buildPlugin');
});

check('gradle wrapper pins a reproducible Gradle 9 distribution', () => {
    const wrapper = rider('gradle/wrapper/gradle-wrapper.properties');
    assert(/distributionUrl=.*gradle-9\.\d+\.\d+-bin\.zip/.test(wrapper),
        'wrapper must point at a Gradle 9 bin distribution');
    assert(/distributionSha256Sum=[0-9a-f]{64}/.test(wrapper),
        'wrapper must pin the SHA-256 of the distribution (mirror swaps stay verifiable)');
    for (const file of ['gradlew', 'gradlew.bat', 'gradle/wrapper/gradle-wrapper.jar']) {
        assert(fs.existsSync(path.join(root, file)), 'missing wrapper file: ' + file);
    }
    has(gradle, 'id("org.jetbrains.intellij.platform") version "2.20.1"');
    has(properties, 'org.gradle.jvmargs=-Xmx2g');
});

console.log('\n' + passed + ' checks passed' + (process.exitCode ? ' (with failures)' : ''));
