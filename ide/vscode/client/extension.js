// EclipseUI VS Code 壳：design/06 §6.4 —— 这个文件只做三件事：
// 找到语言服务器、用 stdio 拉起它、把配置变化转成 server 参数。
// 补全/诊断/悬停/大纲的逻辑一律在 server 里（Rider 用 lsp4ij 接同一个 server）。
const fs = require('fs');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

/** @type {LanguageClient | undefined} */
let client;

function firstExisting(candidates) {
  return candidates.find((candidate) => candidate && fs.existsSync(candidate));
}

function resolveServer(context) {
  const configured = (vscode.workspace.getConfiguration('eclipseui').get('server.path') || '').trim();
  if (configured) {
    return configured;
  }

  // 1) 打包形态：build.ps1 把 publish 产物放进扩展的 server/ 目录。
  const bundled = path.join(context.extensionPath, 'server', 'EclipseUI.LanguageServer.dll');
  // 2) 仓库形态：ide/vscode → ../../ide/EclipseUI.LanguageServer/bin/<cfg>/net10.0/
  const repoRoot = path.resolve(context.extensionPath, '..', '..');
  const devBuilds = ['Release', 'Debug'].map((configuration) =>
    path.join(repoRoot, 'ide', 'EclipseUI.LanguageServer', 'bin', configuration, 'net10.0',
      'EclipseUI.LanguageServer.dll'));

  return firstExisting([bundled, ...devBuilds]);
}

// L7（design/06 §6.9.3）：项目 Compilation 依赖 .cs / .ui / .eustyle / csproj / props /
// assets.json，任何一项变化都要让 server 重建快照。vscode-languageclient 会把这些 watcher
// 转成 workspace/didChangeWatchedFiles 通知（Rider 侧由 lsp4ij 配同一组 glob）。
const WATCH_PATTERNS = [
  '**/*.cs',
  '**/*.ui',
  '**/*.eustyle',
  '**/*.csproj',
  '**/Directory.Build.props',
  '**/obj/project.assets.json',
];

function fileWatchers() {
  const folders = (vscode.workspace.workspaceFolders || [])
    .filter((folder) => folder.uri && folder.uri.scheme === 'file');
  const watchers = [];
  for (const folder of folders) {
    for (const pattern of WATCH_PATTERNS) {
      watchers.push(vscode.workspace.createFileSystemWatcher(
        new vscode.RelativePattern(folder, pattern)));
    }
  }
  return watchers;
}

function serverArguments(context) {
  const configuration = vscode.workspace.getConfiguration('eclipseui');
  const args = ['--stdio'];

  const runtime = (configuration.get('runtime.path') || '').trim();
  if (runtime) {
    args.push('--runtime', runtime);
  }

  for (const assembly of configuration.get('catalog.assemblies') || []) {
    if (assembly && assembly.trim()) {
      args.push('--assembly', assembly.trim());
    }
  }

  const folder = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
  if (folder && folder.uri.scheme === 'file') {
    args.push('--workspace', folder.uri.fsPath);
  }

  // 诊断日志：stdout 是 LSP 协议通道，日志只能走文件 + stderr。
  // 镜像到 stderr 是有意的——vscode-languageclient 会把 server 的 stderr 转到扩展宿主
  // 控制台（也就是调试控制台），这样"打开的文件属于几个项目""F12 有没有结果"
  // 这类问题不需要再去翻机器上的日志目录。
  const logDirectory = (context && (context.logUri || context.globalStorageUri)) || null;
  if (logDirectory) {
    args.push('--log', path.join(logDirectory.fsPath, 'eclipseui-server.log'), '--log-stderr');
  }

  return args;
}

function activate(context) {
  const serverPath = resolveServer(context);
  if (!serverPath) {
    vscode.window.showErrorMessage(
      'EclipseUI: language server not found. Run `dotnet build ide/EclipseUI.LanguageServer` ' +
      'or set `eclipseui.server.path`.');
    return;
  }

  const dotnet = (vscode.workspace.getConfiguration('eclipseui').get('server.dotnet') || 'dotnet').trim();
  const args = [serverPath, ...serverArguments(context)];
  const options = { cwd: path.dirname(serverPath) };
  const serverOptions = {
    run: { command: dotnet, args, transport: TransportKind.stdio, options },
    debug: { command: dotnet, args, transport: TransportKind.stdio, options },
  };
  const clientOptions = {
    documentSelector: [
      { scheme: 'file', language: 'eclipseui' },
      { scheme: 'untitled', language: 'eclipseui' },
      // .eustyle 现在有 LSP 诊断（生成器 ExecuteStylesheet 的 EUI050–052）。
      { scheme: 'file', language: 'eustyle' },
      { scheme: 'untitled', language: 'eustyle' },
    ],
    synchronize: { fileEvents: fileWatchers() },
    outputChannelName: 'EclipseUI',
  };

  client = new LanguageClient('eclipseui', 'EclipseUI Language Server', serverOptions, clientOptions);
  client.start();

  // 反编译虚拟文档（design/06 §6.3 L3 第 4 路）：dll/nuget 引用的控件没有源码可达，
  // server 端用 ILSpy 引擎把类型反编译成 C#，definition 返回 eui-decompiled: uri；
  // 这里按 scheme 取回文本（eclipseui/decompiledContent 自定义请求），文档只读、
  // 以 .cs 结尾所以自动按 C# 高亮。
  const decompiledProvider = {
    async provideTextDocumentContent(uri) {
      if (!client) return '';
      const result = await client.sendRequest('eclipseui/decompiledContent', {
        uri: uri.toString(true),
      });
      return (result && result.content) || '';
    },
  };

  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider('eui-decompiled', decompiledProvider),
    vscode.commands.registerCommand('eclipseui.reloadCatalog', async () => {
      if (!client) return;
      try {
        const result = await client.sendRequest('eclipseui/reloadCatalog');
        const tags = result && typeof result.tags === 'number' ? result.tags : 0;
        if (tags === 0) {
          vscode.window.showWarningMessage(
            'EclipseUI: tag catalog is empty (0 tags) - tag completion and semantic ' +
            'diagnostics are disabled. Open a folder containing a .csproj, run `dotnet restore` ' +
            'so obj/project.assets.json exists, and check the EclipseUI output channel.');
        } else {
          vscode.window.showInformationMessage(`EclipseUI: tag catalog reloaded (${tags} tags).`);
        }
      } catch (error) {
        vscode.window.showErrorMessage(`EclipseUI: reload failed — ${error.message}`);
      }
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('eclipseui.runtime') || event.affectsConfiguration('eclipseui.catalog')) {
        vscode.window.showInformationMessage(
          'EclipseUI: restart the window (or run "Reload Tag Catalog") to apply catalog changes.');
      }
    }),
    { dispose: () => { client.stop(); } },
  );
}

function deactivate() {
  if (!client) return undefined;
  const stopping = client;
  client = undefined;
  return stopping.stop();
}

module.exports = { activate, deactivate };
