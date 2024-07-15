using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Numerics;

namespace Navmesh.Render;

// render target texture with utilities to render to self
public class RenderTarget : IDisposable
{
    public Vector2 Size { get; private set; }
    private Texture2D _rt;
    private RenderTargetView _rtRTV;
    private ShaderResourceView _rtSRV;
    private Texture2D _depth;
    private DepthStencilView _depthDSV;

    public nint ImguiHandle => _rtSRV.NativePointer;

    public RenderTarget(RenderContext ctx, int width, int height)
    {
        Size = new(width, height);

        _rt = new(ctx.Device, new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });

        _rtRTV = new(ctx.Device, _rt, new()
        {
            Format = Format.R8G8B8A8_UNorm,
            Dimension = RenderTargetViewDimension.Texture2D,
            Texture2D = new() { }
        });

        _rtSRV = new(ctx.Device, _rt, new()
        {
            Format = Format.R8G8B8A8_UNorm,
            Dimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MostDetailedMip = 0,
                MipLevels = 1
            }
        });

        _depth = new(ctx.Device, new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });

        _depthDSV = new(ctx.Device, _depth, new()
        {
            Format = Format.D32_Float,
            Dimension = DepthStencilViewDimension.Texture2D,
            Texture2D = new() { }
        });
    }

    public void Dispose()
    {
        _rt.Dispose();
        _rtRTV.Dispose();
        _rtSRV.Dispose();
        _depth.Dispose();
        _depthDSV.Dispose();
    }

    public void Bind(RenderContext ctx)
    {
        ctx.Context.ClearRenderTargetView(_rtRTV, new());
        ctx.Context.ClearDepthStencilView(_depthDSV, DepthStencilClearFlags.Depth, 1, 0);
        ctx.Context.Rasterizer.SetViewport(0, 0, Size.X, Size.Y);
        ctx.Context.OutputMerger.SetTargets(_depthDSV, _rtRTV);
    }
}
