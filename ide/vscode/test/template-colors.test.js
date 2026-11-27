const assert = require('assert');
const path = require('path');
const { activateTemplateColors, palette, roleForScopes } = require('../client/template-colors');

async function main() {
  const expected = {
    type: ['#267F99', '#4EC9B0'], attribute: ['#0451A5', '#9CDCFE'],
    directive: ['#AF00DB', '#C586C0'], literal: ['#A31515', '#CE9178'],
    punctuation: ['#666666', '#B4B4B4'], comment: ['#008000', '#6A9955'],
  };
  const definitions = [];
  let onChange, onVisible, listenerDisposals = 0;
  const lines = [
    '@using BubuUI.Elements', '@namespace Gallery',
    '<Grid Grid.Row="1" OnClick={CycleTheme}>',
    '<TextBlock Style="FontSize: 14;">Text</TextBlock>',
    '<!-- comment -->', '</Grid>', '@code {',
    '  void CycleTheme() { var text = "C# string"; /* C# comment */ }', '}',
  ];
  function editor(languageId, source) {
    return {
      document: { languageId, version: 1, get lineCount() { return source.length; }, lineAt: i => ({ text: source[i] }) },
      applied: new Map(),
      setDecorations(type, ranges) { this.applied.set(type, ranges); },
    };
  }
  const template = editor('bubuui', lines);
  const styles = editor('bss', ['Button { Label: "value"; }']);
  const csharp = editor('csharp', ['class Button {}']);
  const vscode = {
    Range: class { constructor(line, start, endLine, end) { Object.assign(this, { line, start, endLine, end }); } },
    window: {
      visibleTextEditors: [template, styles, csharp],
      createTextEditorDecorationType(options) {
        const type = { options, disposed: false, dispose() { this.disposed = true; } };
        definitions.push(type);
        return type;
      },
      onDidChangeVisibleTextEditors(fn) { onVisible = fn; return { dispose() { listenerDisposals++; } }; },
      showErrorMessage(message) { throw new Error(message); },
    },
    workspace: { onDidChangeTextDocument(fn) { onChange = fn; return { dispose() { listenerDisposals++; } }; } },
  };
  const context = { extensionPath: path.resolve(__dirname, '..'), subscriptions: [] };
  activateTemplateColors(vscode, context);
  async function painted(target) {
    for (let i = 0; i < 100 && target.applied.size !== 6; i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(target.applied.size, 6, 'all roles must be applied');
  }
  await painted(template);
  await painted(styles);
  const roles = Object.keys(palette);
  function colorAt(target, line, offset) {
    for (const [type, ranges] of target.applied)
      if (ranges.some(r => r.line === line && r.start <= offset && offset < r.end)) return type.options.color;
    return null;
  }
  function expect(line, needle, role, delta = 0) {
    assert.equal(colorAt(template, line, lines[line].indexOf(needle) + delta), role ? expected[role][0] : null, needle);
  }
  roles.forEach((role, i) => assert.deepStrictEqual(definitions[i].options,
    { color: expected[role][0], dark: { color: expected[role][1] } }));
  expect(0, '@', 'directive'); expect(0, 'using', 'directive'); expect(0, 'BubuUI', 'type');
  expect(1, 'namespace', 'directive'); expect(1, 'Gallery', 'type');
  expect(2, '<', 'punctuation'); expect(2, 'Grid', 'type'); expect(2, 'Row', 'attribute');
  expect(2, '"1"', 'literal'); expect(2, 'OnClick', 'attribute'); expect(2, 'CycleTheme', null);
  expect(3, 'FontSize', 'attribute'); expect(3, 'Text</', null);
  expect(4, 'comment', 'comment'); expect(7, 'CycleTheme', null); expect(7, 'C# string', null); expect(7, 'C# comment', null);
  assert.equal(colorAt(styles, 0, 0), expected.type[0]);
  assert.equal(colorAt(styles, 0, 9), expected.attribute[0]);
  assert.equal(colorAt(styles, 0, 17), expected.literal[0]);
  assert.equal(csharp.applied.size, 0, 'ordinary C# documents must not be decorated');
  assert.equal(roleForScopes(['string.quoted.double.bubuui', 'source.cs', 'string.quoted.double.cs']), null);

  // Editing a directive into a comment must replace, not accumulate, old ranges.
  lines[0] = '<!-- changed -->'; template.document.version++;
  template.applied.clear(); onChange({ document: template.document });
  await painted(template); expect(0, 'changed', 'comment');
  // Unchanged lines must be retokenized when an earlier edit opens a multiline comment.
  lines[0] = '<!--'; template.document.version++;
  template.applied.clear(); onChange({ document: template.document });
  await painted(template); expect(1, 'namespace', 'comment');
  lines[0] = '<!-- changed -->'; template.document.version++;
  template.applied.clear(); onChange({ document: template.document });
  await painted(template); expect(1, 'namespace', 'directive');
  onVisible([template]);
  context.subscriptions.forEach(item => item.dispose());
  template.applied.clear();
  await new Promise(resolve => setTimeout(resolve, 120));
  assert.equal(template.applied.size, 0, 'disposing cancels pending repaint');
  assert.equal(listenerDisposals, 2);
  assert.ok(definitions.every(type => type.disposed));
  console.log('template palette and editor decoration assertions passed');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
