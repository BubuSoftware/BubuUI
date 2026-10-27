# 3. 模板引擎设计（框架核心）

## 3.1 语法设计

```xml
<StackPanel Gap="8">
    <TextBlock FontSize="30">You pressed {count} times</TextBlock>

    <CheckBox Checked={showButton}>Button visible</CheckBox>

    @if (showButton) {
        <Button OnClick={() => count.value++}>+1</Button>
    }

    @foreach (var item in items) {
        <ListBoxItem Key={item.Id}>{item.Name}</ListBoxItem>
    }
</StackPanel>
```

语法规则与 Razor 痛点的对应：

| 特性 | 设计 | 消灭的痛点 |
|---|---|---|
| 表达式 | `{expr}`，内嵌 C#（经 Roslyn 解析） | `@("xxx")` 括号套引号 |
| 属性赋值 | 字面量按目标类型**编译期**转换，格式错=编译错误 | 运行时 `NotImplementedException` |
| 事件 | `OnClick={() => ...}` 统一语法 | `@onclick` 特例 |
| 双向绑定 | `Checked={v}` / `Checked:bind={v}` | `@bind` 约定俗成 |
| 控制流 | `@if` / `@foreach` / `@switch`，编译为带 Key 的节点组 | 区域 diff 无身份 |
| 子内容/插槽 | 默认 ChildContent；具名插槽显式 `<Slot Name="Header">` | `RenderFragment` 命名约定 |
| 泛型参数 | `DataGrid<Order>` 真实类型语法 | `TItem="List<Order>"` 字符串传类型 |
| 附加属性 | `Grid.Row="1"`，LSP 按 owner 补全、编译期校验 | 字符串注册表无提示 |
| 保留属性 | `Key=`、`Ref=`、`Class=`/`Style=` | `@key`/`@ref` 一套套特殊指令 |
| 标签解析 | 任何标签必须解析到已知组件/元素，否则编译失败 | HTML 未知标签兜底 |
| 文件结构 | 模板 + 同名 `.cs` code-behind，无继承样板 | `@code`/`@inherits` |
| 大小写 | 属性名大小写敏感，拼错即编译错误 | `fontSize`/`FontSize` 混用 |

## 3.2 编译管线

```
.ui 文本 → Lexer → Parser（带源码 span 与 trivia 的 AST）
        → 语义分析：标签解析、属性类型检查、表达式委托推断（Roslyn 语义模型）
        → 更新分析：静态子树 vs 动态绑定点（含依赖的作用域分析）
        → 代码生成：RenderUnit（结构创建指令 + 绑定更新函数）+ span 映射表
```

- 全部作为 **Roslyn 增量源生成器**实现：`.ui` 作为 AdditionalFile 进入编译。
- 一次性买齐：编译期类型检查、IDE 诊断（错误天然出现在编辑器）、增量缓存、AOT 友好
  （运行时零反射零字符串解析）。
- **诊断质量是模板引擎的生命线**：每个错误带精确 span 与修复建议
  （未知标签给最近似组件名、字面量格式错给期望格式示例）。
- 生成代码带 `#line` 指令与 span 映射表，为调试映射和热重载预留（见 07 工具链）。
- pretty-printer 从第一天就是 AST 级（parser 承载 trivia），保住"保存即格式化"。

## 3.3 逃生舱

模板覆盖 95% 场景；剩余用 C# 渲染函数 API（与生成代码同一套指令）或 code-behind 中
命令式操作 `Ref` 元素句柄。两者能力对等，模板不是能力天花板。
