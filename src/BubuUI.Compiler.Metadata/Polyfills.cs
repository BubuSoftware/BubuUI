// netstandard2.0 缺少 IsExternalInit（record 的 init 所需）——编译期垫片。
// C2 把本项目源码链接进生成器时，与 BubuUI.Compiler/Polyfills.cs 同名：在 Generator.csproj 里 Exclude 本文件。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
