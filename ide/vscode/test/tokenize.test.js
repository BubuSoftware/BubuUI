// TextMate grammar 的可执行校验：用真实的 Oniguruma 引擎（vscode-textmate 与 VS Code 同源）
// 把样例逐行 tokenize，断言关键位置的 scope。
//
// 为什么要这个：TextMate grammar 写错不会报错，只会"某个地方忽然没有颜色"，
// 靠肉眼看截图既慢又容易漏。这里把「哪个字符被识别成什么」变成可断言的坐标。
//
//   node test/tokenize.test.js          # 断言
//   node test/tokenize.test.js --dump   # 打印每个 token 的 scope（调试用）
const fs = require('fs');
const path = require('path');
const vsctm = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');

const root = path.resolve(__dirname, '..');
const dump = process.argv.includes('--dump');

const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm')).buffer;
const onigLib = oniguruma.loadWASM(wasm).then(() => ({
  createOnigScanner: (patterns) => new oniguruma.OnigScanner(patterns),
  createOnigString: (s) => new oniguruma.OnigString(s),
}));

function grammarPath(scope) {
  const name = { 'source.bubuui': 'bubuui', 'source.bss': 'bss', 'source.bubuui-csharp': 'csharp' }[scope];
  return path.join(root, 'syntaxes', name + '.tmLanguage.json');
}

const registry = new vsctm.Registry({
  onigLib,
  loadGrammar: async (scopeName) => {
    if (!['source.bubuui', 'source.bss', 'source.bubuui-csharp'].includes(scopeName)) return null;
    const file = grammarPath(scopeName);
    return vsctm.parseRawGrammar(fs.readFileSync(file, 'utf8'), file);
  },
});

/** 逐行 tokenize，返回每行 [{start,end,scopes,text}]。 */
async function tokenize(scopeName, text) {
  const grammar = await registry.loadGrammar(scopeName);
  let stack = vsctm.INITIAL;
  const lines = [];
  for (const line of text.split(/\r?\n/)) {
    const result = grammar.tokenizeLine(line, stack);
    stack = result.ruleStack;
    lines.push(result.tokens.map((t) => ({
      start: t.startIndex,
      end: t.endIndex,
      scopes: t.scopes,
      text: line.slice(t.startIndex, t.endIndex),
    })));
  }
  return { lines, source: text };
}

function scopesAt(doc, line, offset) {
  for (const token of doc.lines[line]) {
    if (offset >= token.start && offset < token.end) return token.scopes;
  }
  return [];
}

const failures = [];

/**
 * 在第 line 行里找 needle，断言 needle 偏移 + delta 处的 scope。
 * 用"找子串"而不是硬编码列号：模板改一行，断言不该跟着碎。
 */
function expectAt(doc, line, needle, delta, expected, hint) {
  const source = doc.source.split(/\r?\n/)[line];
  const at = source.indexOf(needle);
  if (at < 0) {
    failures.push(`line ${line + 1}: '${needle}' not found (${hint})`);
    return;
  }
  const offset = at + delta;
  const scopes = scopesAt(doc, line, offset);
  if (!scopes.includes(expected)) {
    failures.push(
      `line ${line + 1} '${needle}'+${delta} (${hint}): expected '${expected}', got [${scopes.join(', ')}]`);
  }
}

function expectNotAt(doc, line, needle, delta, unexpected, hint) {
  const source = doc.source.split(/\r?\n/)[line];
  const at = source.indexOf(needle);
  const scopes = scopesAt(doc, line, at + delta);
  if (scopes.includes(unexpected)) {
    failures.push(`line ${line + 1} '${needle}'+${delta} (${hint}): unexpected '${unexpected}'`);
  }
}

const template = `@using BubuUI.Elements
<!-- html comment -->
@* razor comment *@
<StackPanel Gap="8" Class="card">
    <TextBlock FontSize="30">You pressed {count} times</TextBlock>
    <CheckBox @bind-Checked={showButton} Checked="true" />
    <Button Grid.Row="1" OnClick={() => count++}>+1</Button>

    @if (count > 0) {
        <Button>visible</Button>
    } else {
        <TextBlock>hidden</TextBlock>
    }

    @foreach (var row in rows) {
        <TextBlock Key={row.Id}>{row.Name}</TextBlock>
    }
</StackPanel>

@code {
    public int count;
    public bool showButton = false;
}
`;

const style = `.primary {
    Background: theme(#22a064, #1c8c58);
    CornerRadius: 12;

    &:hover { Background: theme(#30ba78, #26a268); }
}
@states { spinning; }
$accent: theme(#ffffff, #000000);
`;

(async () => {
  const loops = await tokenize('source.bubuui', `@for (var i = 0; i < 2; i++) {
<TextBlock>{i}</TextBlock>
}
@while (Ready()) {
<TextBlock />
}
@do {
<TextBlock />
} while (Ready());
<TextBlock>after</TextBlock>`);
  expectAt(loops, 0, 'for', 0, 'keyword.control.loop.bubuui', 'for directive');
  expectAt(loops, 0, 'var', 0, 'meta.embedded.line.csharp.bubuui', 'for header');
  expectAt(loops, 3, 'while', 0, 'keyword.control.loop.bubuui', 'while directive');
  expectAt(loops, 6, 'do', 0, 'keyword.control.loop.bubuui', 'do directive');
  expectAt(loops, 8, 'while', 0, 'keyword.control.loop.bubuui', 'do continuation');
  expectAt(loops, 9, 'TextBlock', 0, 'entity.name.type.class.bubuui', 'leave do condition');
  const config = JSON.parse(fs.readFileSync(path.join(root, 'language-configuration.json'), 'utf8'));
  const increases = new RegExp(config.indentationRules.increaseIndentPattern);
  for (const source of ['<A>', '<StackPanel Gap="2">', '@for (var i=0;i<2;i++) {'])
    if (!increases.test(source)) failures.push('indent expected: ' + source);
  for (const source of ['<A />', '<TextBlock>hello</TextBlock>', '<A></A>'])
    if (increases.test(source)) failures.push('unexpected indent: ' + source);

  const doc = await tokenize('source.bubuui', template);

  if (dump) {
    doc.lines.forEach((tokens, index) => {
      console.log(`--- line ${index + 1}`);
      for (const token of tokens) {
        console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
      }
    });
  }

  // spec/02 §2.2：@namespace 必填声明——指令名与点分命名空间都要有 scope，
  // 否则新语法在编辑器里是纯文本（grammar 写漏不会报错，只能靠断言发现）。
  const namespaceLine = await tokenize('source.bubuui', '@namespace Gallery.Samples');
  expectAt(namespaceLine, 0, '@namespace', 1, 'keyword.control.namespace.bubuui', '@namespace');
  expectAt(namespaceLine, 0, 'Gallery.Samples', 2, 'entity.name.namespace.bubuui', 'namespace value');

  // 指令与注释
  expectAt(doc, 0, '@using', 1, 'keyword.control.import.bubuui', '@using');
  expectAt(doc, 0, 'BubuUI.Elements', 2, 'entity.name.namespace.bubuui', 'namespace');
  expectAt(doc, 1, 'html comment', 0, 'comment.block.bubuui', '<!-- -->');
  expectAt(doc, 2, 'razor comment', 0, 'comment.block.bubuui', '@* *@');

  // 标签 / 属性 / 属性字面量
  expectAt(doc, 3, 'StackPanel', 0, 'entity.name.type.class.bubuui', 'tag name');
  expectAt(doc, 3, 'Gap', 0, 'entity.other.attribute-name.bubuui', 'attribute name');
  expectAt(doc, 3, '"8"', 1, 'string.quoted.double.bubuui', 'attribute literal');

  // 文本插值与表达式边界
  expectAt(doc, 4, '{count}', 0, 'punctuation.section.expression.begin.bubuui', '{');
  expectAt(doc, 4, '{count}', 2, 'meta.embedded.expression.bubuui', 'count');
  expectNotAt(doc, 4, '<TextBlock', 0, 'meta.embedded.expression.bubuui', '</TextBlock> 不在表达式里');

  // 双向绑定（@bind- 前缀与属性名分开上色）
  expectAt(doc, 5, '@bind-Checked', 1, 'keyword.control.binding.bubuui', '@bind-');
  expectAt(doc, 5, '@bind-Checked', 9, 'entity.other.attribute-name.bubuui', 'bind target');
  expectAt(doc, 5, 'Checked="true"', 10, 'string.quoted.double.bubuui', 'bool literal');

  // 附加属性
  expectAt(doc, 6, 'Grid.Row', 0, 'support.class.attached-owner.bubuui', 'owner');
  expectAt(doc, 6, 'Grid.Row', 5, 'entity.other.attribute-name.bubuui', 'attached name');

  // 条件块：@if 关键词、条件里的 C# 词法、else 续接
  expectAt(doc, 8, '@if', 1, 'keyword.control.conditional.bubuui', '@if');
  expectAt(doc, 8, 'count > 0', 8, 'constant.numeric.decimal.cs', 'condition');
  expectAt(doc, 9, 'visible', 0, 'source.bubuui', 'block body is plain markup');
  expectAt(doc, 10, 'else', 1, 'keyword.control.conditional.bubuui', 'else');

  // 集合块
  expectAt(doc, 14, '@foreach', 1, 'keyword.control.loop.bubuui', '@foreach');
  expectAt(doc, 15, 'Key', 0, 'entity.other.attribute-name.bubuui', 'Key');

  // @code 内部按 C# 上色
  expectAt(doc, 20, 'public', 1, 'storage.modifier.public.cs', 'access modifier');
  expectAt(doc, 20, 'int count', 1, 'keyword.type.int.cs', 'int');
  expectAt(doc, 21, 'bool showButton', 1, 'keyword.type.bool.cs', 'bool');
  expectAt(doc, 21, 'false', 1, 'constant.language.boolean.false.cs', 'bool literal in C#');

  // 回归：@bind- 与 {expr} 内的标识符必须真的拿到 scope，不能只剩默认前景。
  // 曾经的 bug：@bind- 用 keyword.operator（多数主题与正文同色），表达式里的普通标识符
  // 没有任何 C# scope，整行看上去「没有代码高亮」。
  const bindForms = `@using BubuUI.Elements
<TextBox @bind-p={model.Name} />
<CheckBox @bind-Checked={showButton} />
@code {
    public int count;
}
`;
  const bindDoc = await tokenize('source.bubuui', bindForms);
  if (dump) {
    bindDoc.lines.forEach((tokens, index) => {
      console.log(`--- bindForms line ${index + 1}`);
      for (const token of tokens) console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
    });
  }

  expectAt(bindDoc, 1, '@bind-p', 1, 'keyword.control.binding.bubuui', '@bind- prefix');
  expectAt(bindDoc, 1, '@bind-p', 6, 'entity.other.attribute-name.bubuui', 'lowercase bind target');
  expectAt(bindDoc, 1, 'model.Name', 0, 'variable.other.object.cs', 'member access object');
  expectAt(bindDoc, 1, 'model.Name', 5, 'punctuation.accessor.cs', 'member access dot');
  expectAt(bindDoc, 1, 'model.Name', 6, 'variable.other.object.property.cs', 'member access property');
  expectAt(bindDoc, 2, '{showButton}', 2, 'variable.other.readwrite.cs', 'identifier in bind expression');
  expectAt(bindDoc, 4, 'count', 1, 'entity.name.variable.field.cs', 'identifier in @code block');

  // Real C# grammar: declarations, interpolation and braces inside strings must
  // remain C# until the outer island closes, then return to template highlighting.
  const nativeCSharp = await tokenize('source.bubuui', [
    '@code {',
    '    [Obsolete("}")] public string Caption { get; set; } = "}";',
    '    public string Format(int value) {',
    '        // A comment containing } must not close @code.',
    '        var text = $"value: {value + 1}";',
    '        var path = @"C:\\folder\\""quoted}"; ',
    '        var raw = """',
    '            { "text": "}" }',
    '            """;',
    '        return text;',
    '    }',
    '}',
    '<Button OnClick={() => { var message = $"{Caption}"; Save(message); }} />',
    '<TextBlock>{new[] { 1, 2 }.Length}</TextBlock>',
    '<TextBlock>{$"Count: {Format(2)}"}</TextBlock>',
    '<Button Label="not C# {value}" />',
  ].join('\n'));
  expectAt(nativeCSharp, 1, 'Caption', 0, 'entity.name.variable.property.cs', 'C# property declaration');
  expectAt(nativeCSharp, 2, 'Format', 0, 'entity.name.function.cs', 'C# method declaration');
  expectAt(nativeCSharp, 2, 'value', 0, 'entity.name.variable.parameter.cs', 'C# parameter declaration');
  expectAt(nativeCSharp, 3, 'comment', 0, 'comment.line.double-slash.cs', 'C# comment containing brace');
  expectAt(nativeCSharp, 4, 'value + 1', 8, 'constant.numeric.decimal.cs', 'code inside interpolated string');
  expectAt(nativeCSharp, 5, 'quoted}', 1, 'string.quoted.double.cs', 'verbatim string with doubled quote');
  expectAt(nativeCSharp, 7, 'text', 0, 'string.quoted.double.cs', 'multiline raw string');
  expectAt(nativeCSharp, 9, 'return', 0, 'keyword.control.flow.return.cs', 'C# after raw string');
  expectAt(nativeCSharp, 12, 'Save', 0, 'entity.name.function.cs', 'lambda block in attribute expression');
  expectAt(nativeCSharp, 13, '2', 0, 'constant.numeric.decimal.cs', 'nested initializer in text expression');
  expectAt(nativeCSharp, 14, 'Format', 0, 'entity.name.function.cs', 'method in text interpolation');
  for (const line of [12, 13, 14, 15]) {
    const tag = line === 12 || line === 15 ? 'Button' : 'TextBlock';
    expectAt(nativeCSharp, line, tag, 0, 'entity.name.type.class.bubuui', 'tag after C# island');
    expectNotAt(nativeCSharp, line, tag, 0, 'source.cs', 'C# must not consume following tags');
  }
  expectAt(nativeCSharp, 15, '{value}', 1, 'string.quoted.double.bubuui', 'quoted template attribute stays literal');

  const styleDoc = await tokenize('source.bss', style);
  if (dump) {
    styleDoc.lines.forEach((tokens, index) => {
      console.log(`--- bss line ${index + 1}`);
      for (const token of tokens) {
        console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
      }
    });
  }

  expectAt(styleDoc, 0, '.primary', 1, 'entity.other.attribute-name.class.bss', 'class selector');
  expectAt(styleDoc, 1, 'Background', 1, 'support.type.property-name.bss', 'property');
  expectAt(styleDoc, 1, 'theme(', 1, 'support.function.bss', 'theme()');
  expectAt(styleDoc, 1, '#22a064', 1, 'constant.other.color.bss', 'hex color');
  expectAt(styleDoc, 4, ':hover', 2, 'entity.other.attribute-name.pseudo-class.bss', 'pseudo');
  expectAt(styleDoc, 6, '@states', 2, 'keyword.control.directive.bss', '@states');
  expectAt(styleDoc, 7, '$accent', 1, 'variable.other.bss', '$variable');

  const templateDoc = await tokenize('source.bubuui', '@template Button\n<Border Part="PART_Chrome" />');
  expectAt(templateDoc, 0, '@template', 2, 'keyword.control.directive.bubuui', 'template directive');
  expectAt(templateDoc, 0, 'Button', 1, 'entity.name.type.bubuui', 'template target');
  const themeDoc = await tokenize('source.bss', '@using Demo;\n@theme Rounded for Button {\nTemplate: template(ButtonSkin);\n}\nButton /template/ Border.chrome { Theme: resource(Rounded); }\nButton { Label: "resource(fake)"; }');
  expectAt(themeDoc, 0, '@using', 2, 'keyword.control.directive.bss', 'theme import');
  expectAt(themeDoc, 1, 'Rounded', 2, 'entity.name.type.bss', 'theme declaration');
  expectAt(themeDoc, 2, 'template(', 2, 'support.function.bss', 'template factory');
  expectAt(themeDoc, 4, '/template/', 3, 'keyword.operator.template.bss', 'template boundary');
  expectAt(themeDoc, 4, 'resource(', 3, 'support.function.bss', 'theme reference');
  expectAt(themeDoc, 5, 'resource(fake)', 3, 'string.quoted.double.bss', 'string remains literal');

  // 回归：块定界符独占一行（Showcase.bui 的真实写法）。
  // 曾经的 bug：directive 的 end 要求 `) {` 同行，换行写 `{` 时规则永不结束，
  // 之后整个文件被吞进 meta.directive 里（没有任何补色）。
  const multiline = `@using BubuUI.Elements

<StackPanel>
    @foreach (var row in rows)
    {
        <TextBlock Key={row.Id}>{row.Name}</TextBlock>
    }
    @if (count > 0)
    {
        <Button>ok</Button>
    }
</StackPanel>
`;
  const multiDoc = await tokenize('source.bubuui', multiline);
  if (dump) {
    multiDoc.lines.forEach((tokens, index) => {
      console.log(`--- multiline line ${index + 1}`);
      for (const token of tokens) console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
    });
  }

  expectAt(multiDoc, 4, '{', 0, 'punctuation.section.block.begin.bubuui', 'lone block brace');
  expectAt(multiDoc, 5, 'TextBlock', 0, 'entity.name.type.class.bubuui', 'tag after multi-line block');
  expectAt(multiDoc, 5, '{row.Id}', 1, 'meta.embedded.expression.bubuui', 'expression in block');
  expectAt(multiDoc, 6, '}', 0, 'punctuation.section.block.end.bubuui', 'lone block end');
  expectAt(multiDoc, 7, '@if', 1, 'keyword.control.conditional.bubuui', '@if');
  expectAt(multiDoc, 9, 'Button', 0, 'entity.name.type.class.bubuui', 'tag after multi-line if');

  const leaked = multiDoc.lines.some((tokens) =>
    tokens.some((token) => token.text.includes('<TextBlock') && token.scopes.includes('meta.directive.foreach.bubuui')));
  if (leaked) failures.push('multi-line @foreach leaked its scope onto the following tag');

  // 回归：@switch 体的 case/default/break 是模板级关键字（生成器无逐字映射，语义着色丢弃），
  // 必须由 TextMate 上色。曾经的 bug：switch-directive 只管头部，体内这几个词落回文本模式
  // 没有任何 scope。同时钉住正文里的同名词不被误伤（靠冒号/分号界定）。
  const switchTemplate = `@using BubuUI.Elements
<StackPanel>
    @switch (mode) {
        case 0:
            <TextBlock>zero</TextBlock>
            break;
        default:
            <TextBlock>other case break default</TextBlock>
            break;
    }
</StackPanel>
`;
  const switchDoc = await tokenize('source.bubuui', switchTemplate);
  if (dump) {
    switchDoc.lines.forEach((tokens, index) => {
      console.log(`--- switch line ${index + 1}`);
      for (const token of tokens) console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
    });
  }

  expectAt(switchDoc, 2, '@switch', 1, 'keyword.control.switch.bubuui', '@switch');
  expectAt(switchDoc, 3, 'case', 0, 'keyword.control.switch.bubuui', 'case label');
  expectAt(switchDoc, 5, 'break', 0, 'keyword.control.switch.bubuui', 'break statement');
  expectAt(switchDoc, 6, 'default', 0, 'keyword.control.switch.bubuui', 'default label');
  expectNotAt(switchDoc, 7, 'case', 0, 'keyword.control.switch.bubuui', 'plain-text case');
  expectNotAt(switchDoc, 7, 'break', 0, 'keyword.control.switch.bubuui', 'plain-text break');
  expectNotAt(switchDoc, 7, 'default', 0, 'keyword.control.switch.bubuui', 'plain-text default');

  const embeddedStyle = await tokenize('source.bubuui', '@style {\n.a { &:hover { Background: #fff; } }\n.b { Background: theme(#111, #eee); }\n}\n<Border Style="Background: theme(#111, #fff); CornerRadius: 4;" />');
  expectAt(embeddedStyle, 2, 'Background', 1, 'support.type.property-name.bss', 'second rule stays inside @style');
  expectAt(embeddedStyle, 2, 'theme(', 1, 'support.function.bss', 'nested style function');
  expectAt(embeddedStyle, 4, 'Border', 1, 'entity.name.type.class.bubuui', 'leaving @style restores UI grammar');
  expectAt(embeddedStyle, 4, 'Background', 1, 'support.type.property-name.bss', 'inline Style property');
  expectAt(embeddedStyle, 4, '#fff', 1, 'constant.other.color.bss', 'inline Style color');

  // Resolve the shipped VS theme against real tokens, not just their scope names.
  // A grammar-specific theme can leave template tokens uncoloured even when
  // tokenization itself is correct. Use unique test colours as classification IDs.
  const themePath = path.join(root, '../visualstudio/Grammars/bubuui.tmLanguage.tmTheme');
  const vsTheme = vsctm.parseRawGrammar(fs.readFileSync(themePath, 'utf8'), themePath);
  const classColors = new Map();
  for (const rule of vsTheme.settings) {
    const classification = rule.settings.vsclassificationtype;
    if (!classColors.has(classification))
      classColors.set(classification, '#' + (0x100000 + classColors.size).toString(16).toUpperCase());
  }
  registry.setTheme({ settings: [
    { settings: { foreground: '#000000', background: '#FFFFFF' } },
    ...vsTheme.settings.map(rule => ({ scope: rule.scope,
      settings: { foreground: classColors.get(rule.settings.vsclassificationtype) } })),
  ] });
  const coloredSource = [
    '@using BubuUI.Elements',
    '@namespace Gallery',
    '<Grid Grid.Row="1" Width="320" OnClick={HandleClick} @bind-Width={width}>',
    '    <TextBlock Style="FontSize: 14;">Hello</TextBlock>',
    '</Grid>',
    '@code {',
    '    public int width = 42;',
    '}',
  ];
  const coloredGrammar = await registry.loadGrammar('source.bubuui');
  let coloredStack = vsctm.INITIAL;
  const coloredLines = coloredSource.map(line => {
    const result = coloredGrammar.tokenizeLine2(line, coloredStack);
    coloredStack = result.ruleStack;
    return result.tokens;
  });
  function expectClass(line, needle, classification, delta = 0) {
    const offset = coloredSource[line].indexOf(needle) + delta;
    const tokens = coloredLines[line];
    let actual;
    for (let i = 0; i < tokens.length; i += 2) {
      if (tokens[i] <= offset && (i + 2 === tokens.length || tokens[i + 2] > offset)) {
        // TextMate's encoded token foreground occupies bits 15..23.
        actual = registry.getColorMap()[(tokens[i + 1] >>> 15) & 0x1FF];
        break;
      }
    }
    if (actual !== classColors.get(classification))
      failures.push(`VS theme '${needle}' on line ${line + 1}: expected ${classification}, got ${actual}`);
  }
  expectClass(0, '@using', 'BubuUI directive');
  expectClass(0, 'using', 'BubuUI directive');
  expectClass(0, 'BubuUI.Elements', 'BubuUI type');
  expectClass(1, 'namespace', 'BubuUI directive');
  expectClass(1, 'Gallery', 'BubuUI type');
  expectClass(2, '<', 'BubuUI punctuation');
  expectClass(2, 'Grid', 'BubuUI type');
  expectClass(2, 'Grid.Row', 'BubuUI type');
  expectClass(2, 'Grid.Row', 'BubuUI attribute', 5);
  expectClass(2, 'Width', 'BubuUI attribute');
  expectClass(2, 'OnClick', 'BubuUI attribute');
  expectClass(2, '@bind-', 'BubuUI directive');
  expectClass(2, '@bind-Width', 'BubuUI attribute', 6);
  expectClass(2, '320', 'BubuUI literal');
  expectClass(3, 'Style', 'BubuUI attribute');
  expectClass(3, 'FontSize', 'BubuUI attribute');
  expectClass(4, 'Grid', 'BubuUI type');
  expectClass(5, 'code', 'BubuUI directive');
  expectClass(6, 'public', 'keyword');
  expectClass(6, 'int', 'keyword');
  expectClass(6, 'width', 'field name');
  expectClass(6, '42', 'number');

  if (failures.length > 0) {
    console.error(`\n${failures.length} grammar assertion(s) failed:`);
    for (const failure of failures) console.error('  - ' + failure);
    process.exit(1);
  }
  console.log('grammar assertions passed');
})();
