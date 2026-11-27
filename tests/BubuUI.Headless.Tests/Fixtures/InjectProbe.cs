namespace BubuUI.Headless.Tests;

/// <summary>K37 @inject 端到端夹具的服务契约（Fixtures/InjectProbe.bui 注入 Greeter）。</summary>
public interface IInjectGreeter
{
    string Greet();
}

/// <summary>端到端测试注册用实现。</summary>
public sealed class StaticGreeter : IInjectGreeter
{
    public string Greet() => "hello-di";
}

/// <summary>InjectProbe.bui 的 code-behind（无成员——注入属性由生成器产出）。</summary>
public partial class InjectProbe
{
}
