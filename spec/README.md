# EclipseUI 当前规范

> 统一根类型版，2026-11-13。ui 与纯 C# 共用一种类型模型、目录及运行时机制。

| 文档 | 定义 |
|---|---|
| [01 · 模板语法](./01-template-syntax.md) | 文件、标签、属性、表达式、控制流、内容和诊断 |
| [02 · 生成代码契约](./02-generated-code-contract.md) | 根作为基类、初始化this、动态槽、构建作用域、源映射和确定性 |
| [03 · 更新与绑定](./03-reactivity-and-binding.md) | UiObject生命周期、真实树队列、后台失效、绑定、异步和资源 |
| [04 · 类型元数据](./04-ui-type-metadata.md) | 类型/成员能力、内容准入、目录、继承与诊断 |
| [05 · 控件主题](./05-control-themes.md) | 无内置外观控件、模板投影、独立主题包与绘制边界 |

当前规范是实现依据。design保存架构、实施计划和报告；旧模型的原始决策与示例位于[历史快照](../design/history/2026-11-13-before-legacy-removal/README.md)，不再作为当前API或兼容承诺。

冻结决策：K35根标签决定基类、Window独立于Element；K36删除全部旧组件兼容层。实施及验证见[design/21](../design/21-root-type-unification.md)。

文档变化必须与对应目录/生成器/运行时及真实编译测试一起提交；不能只增加覆盖旧文本的顶部说明而让正文继续描述另一套机制。
