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
  return scope === 'source.eclipseui'
    ? path.join(root, 'syntaxes', 'eclipseui.tmLanguage.json')
    : path.join(root, 'syntaxes', 'eustyle.tmLanguage.json');
}

const registry = new vsctm.Registry({
  onigLib,
  loadGrammar: async (scopeName) => {
    if (scopeName !== 'source.eclipseui' && scopeName !== 'source.eustyle') return null;
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

const template = `@using EclipseUI.Elements
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
  const doc = await tokenize('source.eclipseui', template);

  if (dump) {
    doc.lines.forEach((tokens, index) => {
      console.log(`--- line ${index + 1}`);
      for (const token of tokens) {
        console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
      }
    });
  }

  // 指令与注释
  expectAt(doc, 0, '@using', 1, 'keyword.control.import.eclipseui', '@using');
  expectAt(doc, 0, 'EclipseUI.Elements', 2, 'entity.name.namespace.eclipseui', 'namespace');
  expectAt(doc, 1, 'html comment', 0, 'comment.block.eclipseui', '<!-- -->');
  expectAt(doc, 2, 'razor comment', 0, 'comment.block.eclipseui', '@* *@');

  // 标签 / 属性 / 属性字面量
  expectAt(doc, 3, 'StackPanel', 0, 'entity.name.tag.eclipseui', 'tag name');
  expectAt(doc, 3, 'Gap', 0, 'entity.other.attribute-name.eclipseui', 'attribute name');
  expectAt(doc, 3, '"8"', 1, 'string.quoted.double.eclipseui', 'attribute literal');

  // 文本插值与表达式边界
  expectAt(doc, 4, '{count}', 0, 'punctuation.section.expression.begin.eclipseui', '{');
  expectAt(doc, 4, '{count}', 2, 'meta.embedded.expression.eclipseui', 'count');
  expectNotAt(doc, 4, '<TextBlock', 0, 'meta.embedded.expression.eclipseui', '</TextBlock> 不在表达式里');

  // 双向绑定（@bind- 前缀与属性名分开上色）
  expectAt(doc, 5, '@bind-Checked', 1, 'keyword.operator.binding.eclipseui', '@bind-');
  expectAt(doc, 5, '@bind-Checked', 9, 'entity.other.attribute-name.eclipseui', 'bind target');
  expectAt(doc, 5, 'Checked="true"', 10, 'string.quoted.double.eclipseui', 'bool literal');

  // 附加属性
  expectAt(doc, 6, 'Grid.Row', 0, 'support.class.attached-owner.eclipseui', 'owner');
  expectAt(doc, 6, 'Grid.Row', 5, 'entity.other.attribute-name.eclipseui', 'attached name');

  // 条件块：@if 关键词、条件里的 C# 词法、else 续接
  expectAt(doc, 8, '@if', 1, 'keyword.control.conditional.eclipseui', '@if');
  expectAt(doc, 8, 'count > 0', 8, 'constant.numeric.csharp', 'condition');
  expectAt(doc, 9, 'visible', 0, 'source.eclipseui', 'block body is plain markup');
  expectAt(doc, 10, 'else', 1, 'keyword.control.conditional.eclipseui', 'else');

  // 集合块
  expectAt(doc, 14, '@foreach', 1, 'keyword.control.loop.eclipseui', '@foreach');
  expectAt(doc, 15, 'Key', 0, 'entity.other.attribute-name.eclipseui', 'Key');

  // @code 内部按 C# 上色
  expectAt(doc, 20, 'public', 1, 'keyword.control.csharp', 'access modifier');
  expectAt(doc, 20, 'int count', 1, 'storage.type.csharp', 'int');
  expectAt(doc, 21, 'bool showButton', 1, 'storage.type.csharp', 'bool');
  expectAt(doc, 21, 'false', 1, 'constant.language.csharp', 'bool literal in C#');

  const styleDoc = await tokenize('source.eustyle', style);
  if (dump) {
    styleDoc.lines.forEach((tokens, index) => {
      console.log(`--- eustyle line ${index + 1}`);
      for (const token of tokens) {
        console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
      }
    });
  }

  expectAt(styleDoc, 0, '.primary', 1, 'entity.other.attribute-name.class.eustyle', 'class selector');
  expectAt(styleDoc, 1, 'Background', 1, 'support.type.property-name.eustyle', 'property');
  expectAt(styleDoc, 1, 'theme(', 1, 'support.function.eustyle', 'theme()');
  expectAt(styleDoc, 1, '#22a064', 1, 'constant.other.color.eustyle', 'hex color');
  expectAt(styleDoc, 4, ':hover', 2, 'entity.other.attribute-name.pseudo-class.eustyle', 'pseudo');
  expectAt(styleDoc, 6, '@states', 2, 'keyword.control.directive.eustyle', '@states');
  expectAt(styleDoc, 7, '$accent', 1, 'variable.other.eustyle', '$variable');

  // 回归：块定界符独占一行（Showcase.ui 的真实写法）。
  // 曾经的 bug：directive 的 end 要求 `) {` 同行，换行写 `{` 时规则永不结束，
  // 之后整个文件被吞进 meta.directive 里（没有任何补色）。
  const multiline = `@using EclipseUI.Elements

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
  const multiDoc = await tokenize('source.eclipseui', multiline);
  if (dump) {
    multiDoc.lines.forEach((tokens, index) => {
      console.log(`--- multiline line ${index + 1}`);
      for (const token of tokens) console.log(`  ${JSON.stringify(token.text)}  ${token.scopes.slice(1).join(' ')}`);
    });
  }

  expectAt(multiDoc, 4, '{', 0, 'punctuation.section.block.begin.eclipseui', 'lone block brace');
  expectAt(multiDoc, 5, 'TextBlock', 0, 'entity.name.tag.eclipseui', 'tag after multi-line block');
  expectAt(multiDoc, 5, '{row.Id}', 1, 'meta.embedded.expression.eclipseui', 'expression in block');
  expectAt(multiDoc, 6, '}', 0, 'punctuation.section.block.end.eclipseui', 'lone block end');
  expectAt(multiDoc, 7, '@if', 1, 'keyword.control.conditional.eclipseui', '@if');
  expectAt(multiDoc, 9, 'Button', 0, 'entity.name.tag.eclipseui', 'tag after multi-line if');

  const leaked = multiDoc.lines.some((tokens) =>
    tokens.some((token) => token.text.includes('<TextBlock') && token.scopes.includes('meta.directive.foreach.eclipseui')));
  if (leaked) failures.push('multi-line @foreach leaked its scope onto the following tag');

  if (failures.length > 0) {
    console.error(`\n${failures.length} grammar assertion(s) failed:`);
    for (const failure of failures) console.error('  - ' + failure);
    process.exit(1);
  }
  console.log('grammar assertions passed');
})();
