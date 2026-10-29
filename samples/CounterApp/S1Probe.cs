using System;

namespace Samples;

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
