using VelloSharp;

namespace BubuUI.Rendering.Vello;

/// <summary>Reference-counted device resources shared by surfaces from one factory.</summary>
internal sealed class VelloDeviceContext(WgpuInstance instance, WgpuAdapter adapter, WgpuDevice device, WgpuBackend? backend)
{
    private readonly object _gate = new();
    private int _references = 1;
    internal WgpuInstance Instance { get; } = instance;
    internal WgpuAdapter Adapter { get; } = adapter;
    internal WgpuDevice Device { get; } = device;
    internal WgpuBackend? Backend { get; } = backend;

    internal VelloDeviceContext Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            _references++;
            return this;
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (--_references != 0) return;
            try { Device.Dispose(); }
            finally
            {
                try { Adapter.Dispose(); }
                finally { Instance.Dispose(); }
            }
        }
    }
}
