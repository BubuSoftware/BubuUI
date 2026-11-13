# EclipseUI 设计文档

当前模型：`.ui` 根标签作为基类，实例即根；UiObject 统一初始化、更新及资源，Element 与 Window 分别表达控件与窗口能力。

规范正本是 [spec](../spec/README.md)。本目录说明架构与实施；历史设计不代表当前 API。

## 当前入口

| 文档 | 用途 |
|---|---|
| [04 · 运行时与元素](./04-runtime-and-elements.md) | UiObject、实际内容树、值层和宿主 |
| [05 · 布局渲染输入](./05-layout-rendering-input.md) | 几何与绘制分工；具体契约以源码和当前spec为准 |
| [06 · IDE](./06-ide-support.md) | LSP与Roslyn生成文档、语义能力 |
| [09 · 开发计划](./09-development-plan.md) | 总体路线；最新统一工作见21 |
| [17 · 样式](./17-style-system.md) | Class、主题、样式编译 |
| [19 · 自定义样式属性](./19-custom-style-properties.md) | 元数据扩展与值语法 |
| [20 · 窗口根类型](./20-window-template-root.md) | 通过一般类型与内容规则实现窗口 |
| [21 · UI/C#统一](./21-root-type-unification.md) | 实施切片、旧层删除、验证结果 |
| [验证报告](./reports/root-type-unification-2026-11-13.md) | 构建、测试、Gallery及既有像素基线证据 |

控件/类型元数据已迁至 [spec/04](../spec/04-ui-type-metadata.md)，不再以design/14的旧模型为规范。

## 历史与研究

01–03、07–08、10–16、18保留早期痛点、阶段路线、技术调研与实施台账。引用其旧类名或旧生成代码时只用于追溯，当前行为以spec为准。

[统一前快照](./history/2026-11-13-before-legacy-removal/README.md)保存旧README、生成与更新规范、元数据和窗口方案。历史机制已从运行代码删除；保留记录不构成兼容层。

其他报告：[渲染Gate](./reports/render-gate-2026-11.md)、[目标机性能](./reports/target-machine-60fps-2026-11-08.md)。
