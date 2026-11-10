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

function serverArguments() {
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
  const args = [serverPath, ...serverArguments()];
  const options = { cwd: path.dirname(serverPath) };
  const serverOptions = {
    run: { command: dotnet, args, transport: TransportKind.stdio, options },
    debug: { command: dotnet, args, transport: TransportKind.stdio, options },
  };
  const clientOptions = {
    documentSelector: [
      { scheme: 'file', language: 'eclipseui' },
      { scheme: 'untitled', language: 'eclipseui' },
    ],
    outputChannelName: 'EclipseUI',
  };

  client = new LanguageClient('eclipseui', 'EclipseUI Language Server', serverOptions, clientOptions);
  client.start();

  context.subscriptions.push(
    vscode.commands.registerCommand('eclipseui.reloadCatalog', async () => {
      if (!client) return;
      try {
        const result = await client.sendRequest('eclipseui/reloadCatalog');
        const tags = result && typeof result.tags === 'number' ? result.tags : 0;
        if (tags === 0) {
          vscode.window.showWarningMessage(
            'EclipseUI: tag catalog is empty (0 tags) - tag completion and semantic ' +
            'diagnostics are disabled. Set eclipseui.runtime.path, or open this repository ' +
            'as the workspace folder.');
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
