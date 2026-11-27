const fs = require('fs');
const path = require('path');
const tm = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');
const palette = require('../highlighting/palette.json');

function roleForScopes(scopes) {
  // Semantic tokens and the host's C# theme own every embedded C# token.
  if (scopes.includes('source.cs')) return null;
  for (let i = scopes.length - 1; i >= 0; i--) {
    for (const [role, entry] of Object.entries(palette)) {
      if (entry.scopes.some(scope => scopes[i] === scope || scopes[i].startsWith(scope + '.'))) return role;
    }
  }
  return null;
}

async function createRegistry(extensionRoot) {
  const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
  await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
  return new tm.Registry({
    onigLib: Promise.resolve({
      createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
      createOnigString: text => new oniguruma.OnigString(text),
    }),
    loadGrammar: async scope => {
      const name = { 'source.bubuui': 'bubuui', 'source.bss': 'bss', 'source.bubuui-csharp': 'csharp' }[scope];
      if (!name) return null;
      const file = path.join(extensionRoot, 'syntaxes', name + '.tmLanguage.json');
      return tm.parseRawGrammar(fs.readFileSync(file, 'utf8'), file);
    },
  });
}

function activateTemplateColors(vscode, context) {
  const decorations = Object.fromEntries(Object.entries(palette).map(([role, entry]) => [role,
    vscode.window.createTextEditorDecorationType({ color: entry.light, dark: { color: entry.dark } })]));
  let disposed = false;
  const pending = new Map();
  const tokenCache = new WeakMap();
  const registryPromise = createRegistry(context.extensionPath);
  // Report initialization failures instead of silently reverting to host colours.
  registryPromise.catch(error => vscode.window.showErrorMessage('BubuUI highlighting: ' + error.message));

  async function paint(editor) {
    const document = editor.document;
    if (!['bubuui', 'bss'].includes(document.languageId)) return;
    const version = document.version;
    const registry = await registryPromise;
    if (disposed) return;
    const grammar = await registry.loadGrammar(document.languageId === 'bss' ? 'source.bss' : 'source.bubuui');
    const ranges = Object.fromEntries(Object.keys(palette).map(role => [role, []]));
    const previousLines = tokenCache.get(document) || [];
    const cachedLines = [];
    let stack = tm.INITIAL;
    for (let line = 0; line < document.lineCount; line++) {
      const text = document.lineAt(line).text;
      const previous = previousLines[line];
      // Most keystrokes change one line. Reuse tokens until either the text or
      // incoming multiline state changes (comments, strings and C# blocks).
      const result = previous && previous.text === text && stack.equals(previous.stack)
        ? previous.result : grammar.tokenizeLine(text, stack);
      cachedLines.push({ text, stack, result });
      stack = result.ruleStack;
      for (const token of result.tokens) {
        const role = roleForScopes(token.scopes);
        const end = Math.min(token.endIndex, text.length);
        if (role && end > token.startIndex)
          ranges[role].push(new vscode.Range(line, token.startIndex, line, end));
      }
    }
    if (disposed || document.version !== version || editor.document !== document) return;
    tokenCache.set(document, cachedLines);
    for (const role of Object.keys(palette)) editor.setDecorations(decorations[role], ranges[role]);
  }
  function schedule(editor) {
    if (!['bubuui', 'bss'].includes(editor.document.languageId)) return;
    clearTimeout(pending.get(editor));
    pending.set(editor, setTimeout(() => {
      pending.delete(editor);
      paint(editor).catch(error => console.error('BubuUI highlighting:', error));
    }, 80));
  }
  const listeners = [
    vscode.window.onDidChangeVisibleTextEditors(editors => editors.forEach(schedule)),
    vscode.workspace.onDidChangeTextDocument(event => {
      vscode.window.visibleTextEditors.filter(editor => editor.document === event.document).forEach(schedule);
    }),
  ];
  vscode.window.visibleTextEditors.forEach(schedule);
  context.subscriptions.push({ dispose() {
    disposed = true;
    for (const timer of pending.values()) clearTimeout(timer);
    pending.clear();
    listeners.forEach(listener => listener.dispose());
    Object.values(decorations).forEach(decoration => decoration.dispose());
    registryPromise.then(registry => registry.dispose(), () => {});
  } });
}

module.exports = { activateTemplateColors, createRegistry, roleForScopes, palette };
