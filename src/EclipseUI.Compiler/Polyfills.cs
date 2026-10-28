// netstandard2.0 缺少 IsExternalInit（record struct 的 init 所需）——编译期垫片
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
