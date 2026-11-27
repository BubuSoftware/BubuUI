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
  return scope === 'source.bubuui'
    ? path.join(root, 'syntaxes', 'bubuui.tmLanguage.json')
    : path.join(root, 'syntaxes', 'bss.tmLanguage.json');
}

const registry = new vsctm.Registry({
  onigLib,
  loadGrammar: async (scopeName) => {
    if (scopeName !== 'source.bubuui' && scopeName !== 'source.bss') return null;
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
  expectAt(doc, 3, 'StackPanel', 0, 'entity.name.tag.bubuui', 'tag name');
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
  expectAt(doc, 8, 'count > 0', 8, 'constant.numeric.csharp', 'condition');
  expectAt(doc, 9, 'visible', 0, 'source.bubuui', 'block body is plain markup');
  expectAt(doc, 10, 'else', 1, 'keyword.control.conditional.bubuui', 'else');

  // 集合块
  expectAt(doc, 14, '@foreach', 1, 'keyword.control.loop.bubuui', '@foreach');
  expectAt(doc, 15, 'Key', 0, 'entity.other.attribute-name.bubuui', 'Key');

  // @code 内部按 C# 上色
  expectAt(doc, 20, 'public', 1, 'keyword.control.csharp', 'access modifier');
  expectAt(doc, 20, 'int count', 1, 'storage.type.csharp', 'int');
  expectAt(doc, 21, 'bool showButton', 1, 'storage.type.csharp', 'bool');
  expectAt(doc, 21, 'false', 1, 'constant.language.csharp', 'bool literal in C#');

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
  expectAt(bindDoc, 1, 'model.Name', 0, 'variable.other.object.csharp', 'member access object');
  expectAt(bindDoc, 1, 'model.Name', 5, 'punctuation.accessor.csharp', 'member access dot');
  expectAt(bindDoc, 1, 'model.Name', 6, 'variable.other.object.property.csharp', 'member access property');
  expectAt(bindDoc, 2, '{showButton}', 2, 'variable.other.readwrite.csharp', 'identifier in bind expression');
  expectAt(bindDoc, 4, 'count', 1, 'variable.other.readwrite.csharp', 'identifier in @code block');

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
  expectAt(multiDoc, 5, 'TextBlock', 0, 'entity.name.tag.bubuui', 'tag after multi-line block');
  expectAt(multiDoc, 5, '{row.Id}', 1, 'meta.embedded.expression.bubuui', 'expression in block');
  expectAt(multiDoc, 6, '}', 0, 'punctuation.section.block.end.bubuui', 'lone block end');
  expectAt(multiDoc, 7, '@if', 1, 'keyword.control.conditional.bubuui', '@if');
  expectAt(multiDoc, 9, 'Button', 0, 'entity.name.tag.bubuui', 'tag after multi-line if');

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
  expectAt(embeddedStyle, 4, 'Border', 1, 'entity.name.tag.bubuui', 'leaving @style restores UI grammar');
  expectAt(embeddedStyle, 4, 'Background', 1, 'support.type.property-name.bss', 'inline Style property');
  expectAt(embeddedStyle, 4, '#fff', 1, 'constant.other.color.bss', 'inline Style color');

  if (failures.length > 0) {
    console.error(`\n${failures.length} grammar assertion(s) failed:`);
    for (const failure of failures) console.error('  - ' + failure);
    process.exit(1);
  }
  console.log('grammar assertions passed');
})();
