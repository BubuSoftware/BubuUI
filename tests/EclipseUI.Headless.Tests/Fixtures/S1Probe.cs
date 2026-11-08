using System;

namespace EclipseUI.Headless.Tests;

/// <summary>S1 验收测试探针（原在 samples/CounterApp，2026-11-09 移入测试工程，画廊不再承担测试职责）。</summary>
public partial class S1Probe
{
    public int gap = 4;
    public string message = "start";

    private void Bump() => message = "bumped";

    private void Fail()
    {
        message = "failed";
        throw new InvalidOperationException("boom");
    }
}
