# 28 · 可替换控件模板与控件主题

2026-11-17。内置交互控件已采用统一的 Element 模板机制，Fluent 包提供完整的控件模板和外观规则。当前规范见 [spec/05](../spec/05-control-themes.md)，主题接入见 [design/27](./27-fluent-theme.md)。

## 作者入口

`ButtonSkin.ui` 定义视觉结构：

```xml
@namespace MyApp.Themes
@using EclipseUI.Elements
@template Button

<Border Class="chrome" Background={Owner.Background}
        BorderColor={Owner.BorderBrush} CornerRadius={Owner.CornerRadius}>
    <ContentPresenter Part="PART_ContentPresenter"
                      Content={Owner.Content} Label={Owner.Label}
                      HorizontalPadding={Owner.HorizontalPadding}
                      VerticalPadding={Owner.VerticalPadding}
                      Foreground={Owner.Foreground} />
</Border>
```

`controls.eustyle` 定义命名主题及应用规则：

```scss
@using EclipseUI.Elements;
@using MyApp.Themes;

@theme RoundedButton for Button {
    Template: template(ButtonSkin);
    Background: theme(#333333, #fbfbfb);
    Foreground: theme(#ffffff, #1a1a1a);
    CornerRadius: 4;
    HorizontalPadding: 12;
    VerticalPadding: 5;
    &:hover { Background: theme(#3c3c3c, #f6f6f6); }
    &:pressed { Background: theme(#292929, #eeeeee); }
}

Button.primary { Theme: resource(RoundedButton); }
Button.primary:focus-visible /template/ Border.chrome {
    BorderColor: #0078d4;
}
```

调用方继续使用 `<Button Class="primary">保存</Button>`，并通过 `StyleBootstrap.Apply(registry)` 安装选择器规则。主题导出为项目根命名空间下的 `StyleBootstrap.RoundedButton` 属性，也可直接赋给控件的 `Theme`。

## Element 模板契约

所有 `Element` 都提供 `Theme`、`Template`、`TemplateRoot` 和 `ApplyTemplate()`。`ControlTemplate<T>` 的 T 可以是任意 Element，不要求控件改为继承 TemplatedControl。普通 `.ui` 仍按根类型生成组件；显式 `@template T` 文件生成模板工厂和承载代码的模板视图，`Owner` 类型为 T。

显式 `Template` 优先于 `Theme.Template`。共享的是模板定义，每次 Build 创建独立视觉树。编译模板继续复用现有组件发射器，因此视觉根需为可继承的 Element，不能使用 Window 或 sealed 元素作为根。模板视图使用强类型 `Owner` 指向行为控件；已经声明 `Owner` 属性的呈现器或主题视觉元素应放在 Grid/Border 根的内部，分别绑定其 Owner。

模板内部可以包含布局、通用视觉元素和交互控件，支持已有的条件块、循环块和表达式。交互状态、属性、数据、动画及主题变化刷新模板表达式和视觉失效；切换亮暗不重建未改变的模板。普通外部 CLR 状态仍遵守显式 `Invalidate()` 契约。

`Part="标识符"` 定义模板部件，行为控件通过 `TemplateInstance.FindPart<T>()` 及 `OnApplyTemplate()` 接入。重复名称和不符合必需部件契约的模板被拒绝；`OnTemplateRemoving()` 解除旧部件事件。`Part` 不等于生成字段 `Ref`。模板构造或安装失败保留旧视觉树，成功后释放旧模板资源。

ComboBox 要求按钮、列表和浮层部件，DatePicker 要求编辑器、按钮、日历和浮层部件，DataGrid 要求滚动部件。它们在模板安装前仍可设置数据、选择、日期和范围；模板更换后继续使用既有状态。

## 内容投影与视觉树

模板根单独存储，不占控件的公开 `Children` 集合。`ContentPresenter` 投影单项调用方内容，`ItemsPresenter` 投影行为控件的内容集合。公开集合保留内容归属和生命周期，投影后的真实 `Parent` 指向呈现器；需要原内容所有者时使用 `ContentParent`。

更换模板先解除旧投影，再挂接新投影，释放模板生成的元素并保留借入内容。所有更新、绘制、命中测试、焦点和动画遍历统一使用 `VisualChildren`。布局回调可保留在行为控件中，由呈现器委托调用；布局所需的皮肤尺寸通过 StyledProperty 提供并在主题中赋值。

原有 Slot 调用语法保留，模板通过 Presenter 展示内容，不改变普通组件的插槽初始化语义。

## 样式和值来源

同一属性的优先级为：

```text
local > 普通组件 template > class > 类型化应用样式
      > 控件主题规则 > 视觉模板 skin
      > inherited > 应用主题默认值 > 属性默认值
```

控件模板中的属性赋值使用独立 skin 层；模板实例中的显式 C# 赋值仍为 local。状态退出时清除失效 setter，恢复下层有效值。

`.eustyle` 主题模块支持命名空间导入、类型/类/状态选择器、嵌套 `&:state`、一次显式 `/template/ Type.class` 边界及类型化 setter。模板边界按 `TemplatedParent` 限定，不穿透嵌套控件自己的模板，也不把借入内容当作模板部件。多个应用规则按类/状态、类型和声明顺序决定逐属性结果。

`theme(dark, light)` 接受相同目标类型的两个字面量，按接收元素的明暗状态解析；不限于颜色。`@theme` 声明需要一个 `Template: template(Type)`，其余声明设置属性和状态规则，不直接修改元素树。

## 类型化共享资源

主题包或应用可声明公开静态 `ThemeResource<T>` 字段：

```csharp
public static readonly ThemeResource<UiColor> ControlBackground =
    new(element => element.IsLightTheme ? lightColor : darkColor);
```

```scss
@using EclipseUI.Themes.Fluent;
Button.primary {
    Background: resource(FluentResources.ControlBackground);
}
```

资源字段可位于当前项目或引用程序集，支持命名空间导入及类型全名。编译器验证公开、静态、可访问、无歧义，以及 `ThemeResource<T>` 的 T 与目标 StyledProperty 值类型相同。生成代码直接调用字段的 `Resolve(element)`，在状态或主题改变时重新求值；编译期不执行资源代码，运行期不使用反射或名称注册表。

`Theme: resource(RoundedButton)` 仍引用同一编译模块的命名控件主题。类型化静态资源不等于通用动态资源字典、运行期名称搜索或隐式资源继承。

## Fluent 与框架边界

Fluent 包为按钮、选择控件、输入控件、数值范围控件、列表、树、页签、表格、日期选择、滚动视口和浮层提供模板。默认映射按最具体的注册 CLR 类型查找，派生控件复用兼容皮肤，独立部件契约的控件单独注册。

Runtime 只保留行为、属性、状态、数据、输入、焦点和部件契约。主题包拥有控件皮肤的绘制、配色、尺寸和结构；复杂视觉元素也位于主题包，由模板创建。渲染器保留通用绘制和视觉树调度，不再包含按钮、复选框、滚动条等皮肤类型分发。平台包默认中性，应用显式安装 Fluent 或自定义主题。

## 验证与工具

编译器、Headless、Rendering 和平台测试分别验证类型检查、模板及资源生成、生命周期、内容保留、部件重接、输入/编辑/选择、裁剪、浮层、焦点、动画、明暗切换和 retained 绘制。架构测试约束模块依赖和主题绘制边界；性能预算继续使用独立回归。

IDE 对 `.ui`、`@template`、`@theme`、类型化属性、状态、模板边界、命名控件主题和静态 `ThemeResource<T>` 提供共享 LSP 支持。资源补全按属性值类型过滤，并支持悬停、定义跳转及类型错误诊断。项目范围以求值后的 AdditionalFiles 为准，未保存文本参与分析；歧义导航不猜测目标。

历史构建数字与截图保留在对应日期的报告中，不代表当前工作区测试结果。跨模块动态资源字典、无需可继承根的模板工厂及更多专用呈现部件仍是独立扩展范围。

2026-11-17 迁移验收：解决方案构建通过；Compiler 完整套件 476、Headless 707、IDE 完整套件 393 全过，最终 IDE 分流/共享资源相关 64 项及编译器补充回归 2 项通过。Rendering 215 项通过，唯一失败为基础图形像素基准差异，未修改 HEAD 的独立副本复现相同哈希；未更新基线。Linux 平台 33 通过/2 跳过，macOS 35 通过，Windows 平台 4 项因本机平台跳过。亮暗画廊、完整控件目录、交互状态及 100/125/150/200% 缩放快照已生成并核对。详细日志与截图位于本地 `out/lookless-migration/`。
