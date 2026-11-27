// 扩展壳的可执行校验：用假的 vscode / vscode-languageclient 模块加载真实的 extension.js，
// 断言"服务器路径怎么找、启动参数怎么拼"。
//
// 为什么不直接开 VS Code 验：这层最容易出的错是路径与参数（bundle 目录 vs 仓库 bin 目录、
// --workspace 有没有带上），这些都可以在没有编辑器的情况下断言；真正的 UI 行为留给 F5。
const Module = require('module');
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const extensionRoot = path.resolve(__dirname, '..');
const workspace = path.resolve(extensionRoot, '..', '..');

const configuration = {
  'server.path': '',
  'server.dotnet': 'dotnet',
  'runtime.path': '',
  'catalog.assemblies': [],
};

const started = [];
const errors = [];
const contentProviders = [];

const fakeVscode = {
  RelativePattern: class {
    constructor(base, pattern) {
      this.base = base;
      this.pattern = pattern;
    }
  },
  workspace: {
    getConfiguration: () => ({ get: (key) => configuration[key] }),
    workspaceFolders: [{ uri: { scheme: 'file', fsPath: workspace } }],
    createFileSystemWatcher: (pattern) => ({ pattern, dispose() {} }),
    onDidChangeConfiguration: () => ({ dispose() {} }),
    registerTextDocumentContentProvider: (scheme, provider) => {
      contentProviders.push({ scheme, provider });
      return { dispose() {} };
    },
  },
  window: {
    showErrorMessage: (message) => errors.push(message),
    showInformationMessage: () => {},
  },
  commands: { registerCommand: () => ({ dispose() {} }) },
};

const fakeClient = {
  LanguageClient: class {
    constructor(id, name, serverOptions, clientOptions) {
      this.id = id;
      this.serverOptions = serverOptions;
      this.clientOptions = clientOptions;
    }
    start() { started.push(this); }
    stop() { return Promise.resolve(); }
    sendRequest() { return Promise.resolve({ tags: 9 }); }
  },
  TransportKind: { stdio: 'stdio' },
};

const load = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'vscode') return fakeVscode;
  if (request === 'vscode-languageclient/node') return fakeClient;
  return load.call(this, request, parent, isMain);
};

const extension = require(path.join(extensionRoot, 'client', 'extension.js'));

function fakeContext() {
  return { extensionPath: extensionRoot, subscriptions: [] };
}

function lastClient() {
  return started[started.length - 1];
}

// 1) 未配置 server.path：自动找到（打包的 server/ 或仓库 bin/），参数里带 --stdio 与 --workspace
extension.activate(fakeContext());

assert.strictEqual(started.length, 1, 'activate must start exactly one language client');
const client = lastClient();
const args = client.serverOptions.run.args;

// 反编译虚拟文档内容提供器必须挂上（design/06 §6.3 L3 第 4 路：dll/nuget 控件跳转）。
const decompiled = contentProviders.find((entry) => entry.scheme === 'eui-decompiled');
assert.ok(decompiled, 'decompiled content provider must be registered for the eui-decompiled scheme');
assert.strictEqual(typeof decompiled.provider.provideTextDocumentContent, 'function');

const serverPath = args[0];
assert.ok(serverPath.endsWith('BubuUI.LanguageServer.dll'), `unexpected server path: ${serverPath}`);
assert.ok(fs.existsSync(serverPath), `resolved server does not exist: ${serverPath}`);
assert.strictEqual(args[1], '--stdio', 'stdio transport must be requested explicitly');
assert.ok(args.includes('--workspace'), 'workspace folder should be passed for catalog probing');
assert.strictEqual(args[args.indexOf('--workspace') + 1], workspace, 'workspace path must be the folder root');
assert.strictEqual(client.serverOptions.run.command, 'dotnet');
assert.deepStrictEqual(
  client.clientOptions.documentSelector.map((s) => s.language),
  ['bubuui', 'bubuui', 'bss', 'bss'],
  '.bui and .bss documents are served');
// L7：watcher 必须覆盖项目 Compilation 的全部输入（.bss 也在内）。
const watchPatterns = client.clientOptions.synchronize.fileEvents.map((w) => w.pattern.pattern);
for (const expected of ['**/*.cs', '**/*.bui', '**/*.bss', '**/*.csproj',
  '**/Directory.Build.props', '**/obj/project.assets.json']) {
  assert.ok(watchPatterns.includes(expected), `missing file watcher for ${expected}`);
}

// 2) 显式配置优先
configuration['server.path'] = 'D:/custom/BubuUI.LanguageServer.dll';
configuration['runtime.path'] = 'D:/app/bin';
configuration['catalog.assemblies'] = ['D:/app/bin/MyApp.dll'];

extension.activate(fakeContext());
const configuredArgs = lastClient().serverOptions.run.args;
assert.strictEqual(configuredArgs[0], 'D:/custom/BubuUI.LanguageServer.dll');
assert.ok(configuredArgs.includes('--runtime'));
assert.strictEqual(configuredArgs[configuredArgs.indexOf('--runtime') + 1], 'D:/app/bin');
assert.ok(configuredArgs.includes('--assembly'));
assert.strictEqual(configuredArgs[configuredArgs.indexOf('--assembly') + 1], 'D:/app/bin/MyApp.dll');

// 3) deactivate 停掉客户端
assert.ok(extension.deactivate() instanceof Promise);

// 4) 没找到 server 时必须明确报错，而不是静默什么都不做
configuration['server.path'] = '';
const originalExists = fs.existsSync;
fs.existsSync = (candidate) => (String(candidate).endsWith('BubuUI.LanguageServer.dll') ? false : originalExists(candidate));
const before = started.length;
extension.activate(fakeContext());
fs.existsSync = originalExists;
assert.strictEqual(started.length, before, 'must not start a client without a server');
assert.ok(errors.length > 0 && errors[0].includes('language server not found'), 'user must be told what to do');

console.log('extension assertions passed');
