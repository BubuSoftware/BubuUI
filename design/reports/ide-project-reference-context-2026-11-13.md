# IDE 项目引用上下文 · 2026-11-13

在本次补全协议验收中，override 和 `StringBuilder` 自动导入的 LSP 文本、附加编辑、snippet
光标及方法体断言均已通过，但提交编辑后对 Gallery 项目整体编译的检查暴露了原有的 `CS0121`。
该错误位于 `GalleryApp.cs` 的 `Services.GetRequiredService<GalleryLaunchOptions>()`，并非新生成的
`.ui` 方法或类型使用位置。

## 原因与修正

项目快照原先按路径去重，将服务端进程的 TPA 运行时程序集和项目 NuGet 编译资产同时加入引用。
同一个程序集的不同版本、不同路径因而可以并存，使依赖注入扩展方法出现两份候选。

引用上下文应优先使用项目求值取得的编译资产；TPA 只补齐项目没有提供的程序集。合并以程序集
simple name 为边界，避免服务端自身依赖改变用户项目的类型绑定。生产修正位于项目工作区的引用
组装，不修改 Gallery 源码、不改项目包版本，也不隐藏 `CS0121` 诊断。

## 回归检查

新增 `ProjectReferenceContextTests`：

- Gallery 项目快照中的程序集 simple name 唯一。
- `Microsoft.Extensions.DependencyInjection.Abstractions` 使用项目求值给出的编译资产路径。
- 真实 `GalleryApp.GetRequiredService` 调用绑定为一个方法，返回 `GalleryLaunchOptions`，不再有
  `CS0121` 候选冲突。

原有三个补全协议场景继续保留“应用全部编辑后，项目编译没有错误”的断言；没有按文件、诊断编号
或错误数量放宽验收。最终运行结果随本次补全验证一并记录。
