# 28 · 可替换控件模板与控件主题

状态：2026-11-14，第一阶段已实施。普通 Button 已接入 Fluent 编译模板。本文区分当前实现与后续工作；不表示 design/23 已整体实施。

## 作者入口

`ButtonSkin.ui`：

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

`controls.eustyle`：

```scss
@using EclipseUI.Elements;
@using MyApp.Themes;

@theme RoundedButton for Button {
    Template: template(ButtonSkin);
    Background: theme(#333333, #fbfbfb);
    Foreground: theme(#ffffff, #1a1a1a);
    BorderBrush: theme(#555555, #d1d1d1);
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

调用方继续使用 `<Button Class="primary">保存</Button>`。继续通过 `StyleBootstrap.Apply(registry)` 安装样式。主题也导出为该模块 `StyleBootstrap.RoundedButton` 属性，可以在 C# 或 `.ui` 表达式中赋给 `Theme`。

## 当前契约

- 普通 `.ui` 保持根类型继承和初始化语义。显式 `@template T` 文件生成 `ControlTemplate<T>` 工厂，以及承载生成代码的 `文件名__TemplateView` 类型；`Owner` 是 T。
- 每次 Build 创建独立视觉树。定义可共享，元素实例不可共享。模板视图仍复用现有编译器，因此第一阶段视觉根必须是可继承 Element，不能为 Window 或 sealed 元素。
- 模板可以包含布局、绘图和交互控件；已有条件块、循环块和 `{expr}` 使用同一发射器。宿主有效属性及状态变化刷新模板表达式，普通外部 CLR 数据变化仍要求 Invalidate。
- `Part="标识符"` 提供实例内的部件名称，`TemplateInstance.FindPart<T>()` 查找。重复部件名在应用模板时拒绝；必需部件可由控件的 OnApplyTemplate 检查。Part 不等同于代码字段 Ref。
- `ContentPresenter.Content` 展示借入的调用方内容；换模板时先解绑旧投影，再挂接新投影，不销毁内容。模板构造失败保留原视觉树；成功后销毁旧模板创建的元素。Button 最终销毁时清理自己的内容。
- 现有 Slot 调用语法保留。新模板使用 Presenter 投影内容；不改变旧 ContentSlot 的初始化展开语义。
- Theme 与 Template 都是可设置、可清除的 StyledProperty。显式 Template 优先于 Theme 提供的模板。未指定模板的旧控件继续走现有布局/绘制路径。

## 样式和值层

保持旧值层不变，在其后增加新来源：

```
local > 普通组件的 template > 旧 class 样式 > 类型化应用规则
      > 控件主题规则 > 控件模板默认值/绑定
      > inherited > scoped theme default > property default
```

控件模板中的 StyledProperty 赋值使用独立 skin 层，不占普通组件的 template 层。状态撤销后移除已失效 setter，恢复下层仍有效的模板绑定。模板实例中的 C# 显式赋值仍为 local。

包含 `@using` 或 `@theme` 的 `.eustyle` 文件使用控件主题模块语法；旧样式表保持原解析/注册路径。主题模块支持：

- 导入的类型名、类名、平台状态及其组合；嵌套 `&:state`。
- 一次显式 `/template/ Type.class` 边界，按 TemplatedParent 限定，不穿透嵌套控件自己的模板，也不将借入内容变成模板部件。
- 类型化 setter：复用 UI 目录的 StyledProperty 身份和字面量解析器；不执行用户程序集进行编译期转换。
- `theme(dark, light)` 两个字面量按同一目标类型转换；不局限于颜色。资源模式切换依托宿主 ThemeDefaults。
- `resource(Name)` 当前用于同一编译模块的控件主题；编译期检查目标类型和重复声明。尚不是完整的通用动态资源系统。
- 类型化应用规则按类/状态优先级、类型优先级、最后声明顺序竞争。旧 class 注册路径仍保持原规则；两条路径的完整统一是后续工作。

主题不接受任意树修改指令。增加内部元素通过替换模板或已有内容插槽完成。

## Fluent 迁移

`src/EclipseUI.Themes.Fluent/FluentButtonTemplate.ui` 和 `controls.eustyle` 是实际生产模板与样式，不是独立示例。当前自动接入普通 Button，派生控件与其余控件继续原实现，避免把它们的行为部件和布局一并改变。

按钮圆角边框的渐变由通用 UiLinearGradient 和绘制接口实现。模板内保留旧 Visual 背景/前景覆盖的兼容适配。渲染器通过 UsesDefaultDrawing 跳过已模板化按钮的旧 PaintButton；其他旧控件仍使用原绘制逻辑。

## 后续范围

1. 将 design/23 的通用资源字典、作用域与值快照完整统一到新旧样式路径。
2. 完善逻辑树与视觉树的独立契约；目前保留实际 Parent 树，并增加模板归属和显式内容投影。
3. 通用 ItemsPresenter、Track/Thumb、TextPresenter 编辑部件及必需部件的编译期元数据。
4. ToggleButton、CheckBox、Slider、TextBox 等逐个迁移；最终删除对应的旧绘制分发。
5. 模板根不再依赖可继承类型。

第一阶段不承诺上述尚未落地的能力，也不将修改皮肤视作自动增加业务行为。

## IDE 支持（2026-11-15）

共享 LSP 服务现已接通本阶段模板和主题语法；VS Code、Rider、Visual Studio 复用服务端与 TextMate 语法。

| 编写位置 | 支持能力 |
| --- | --- |
| `.ui` 的 `@template` | 指令及目标类型补全、导入附加编辑、悬停、类型定义跳转 |
| 模板表达式与 `@code` | 类型化 `Owner` 接入现有 C# 服务；模板有无关语法错误时仍保留 `Owner` 的代码补全 |
| `.eustyle` 的 `@using` / `@theme` | 命名空间和目标控件补全、主题大纲、悬停与语义着色 |
| setter 与状态选择器 | 按目标控件或模板部件列属性、枚举/bool/字面量候选，支持状态及 `/template/`；提供属性拼写与导入快速修复 |
| `template(Type)` | 类型兼容的工厂候选、缺失导入附加编辑、跳到 `.ui` 的 `@template` 声明；引用程序集工厂走 CLR 定义导航 |
| `resource(Name)` | 同编译模块内的兼容主题候选、悬停、定义导航与主题重命名 |
| 模板内部类选择器 | 从兼容模板的静态元素中补全类名，唯一匹配时跳到 `.ui` 的 `Class` 值 |
| `.ui` 的应用类 | 可从 `Class="primary"` 跳到类型化的 `Button.primary` 规则 |
| 主题的 C# 引用 | 项目模式下，`.ui` 中 `StyleBootstrap.ThemeName` 跳回主题声明；主题重命名同时修改本项目可绑定的 `.cs` 与 `.ui` 引用 |
| 样式编辑辅助 | `theme` / `template` / `resource` 参数提示、缩进格式化及范围格式化；保留字符串、注释及换行形式 |
| 诊断 | 项目模式复用真实生成器诊断；无项目模式按打开文档构建诊断输入；编辑后的延迟诊断同步到已打开的同项目消费文件 |

项目归属严格使用求值后的 AdditionalFiles，索引优先读取未保存缓冲区。无项目模式只共享显式打开的文档，不扫描工作区猜测模块。补全容忍未闭合的块与函数；注释和字符串不作为资源引用。歧义定义不猜落点；重命名拒绝重复名称、关键字及生成成员冲突。

这些能力覆盖**当前已实现的语言**：`resource()` 仍是同模块控件主题，尚无通用动态资源；不提供以重命名类型代替文件重命名的操作，也不跨项目改写引用。模板部件由 C# 动态创建时仍需遵守运行时 Part 契约。样式格式化采用缩进编辑，不重排声明或改写表达式。

本轮验证：IDE 完整套件 367 项通过；最后的别名与引用程序集模板修正后，样式专项 42 项、Compiler 完整套件 437 项通过。VS Code grammar/client 断言通过，Rider 静态接线 12 项通过。发布到 `out/theme-ide-server` 后，以实际 stdio 进程通过 `ide/visualstudio/verify-server.ps1`，验证初始化、控件补全、属性导航、反编译，以及控件主题资源补全和声明跳转。未在三个 IDE 的图形界面逐项人工验收，也未替换用户已安装的插件。

## 验证记录

2026-11-14 最终回归结果：

| 项目 | 通过 | 跳过 |
| --- | ---: | ---: |
| Compiler | 435 | 0 |
| Headless | 669 | 3 |
| Rendering | 214 | 0 |
| IDE | 322 | 0 |

`dotnet build samples/Gallery.Desktop -o out/lookless-gallery-build --no-restore -v:q`：零警告、零错误。

FluentCheck 导出的亮暗色、交互状态及 100/125/150/200 DPI 快照检查通过；画廊 CPU 截图为 720×960，产物位于 `out/lookless-gallery.png`，状态截图位于 `out/lookless-fluent/`。

回归期间发现共享 ILSpy 反编译器被并发调用导致导航返回空，已按实例串行化，并用并发导航测试验证修复。渲染测试宿主曾出现异常退出和挂起，原因尚未定位；最终独立运行带 hang 诊断的完整渲染套件，214 项全部通过。
