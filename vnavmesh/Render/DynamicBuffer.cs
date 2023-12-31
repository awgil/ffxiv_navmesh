using SharpDX;
using SharpDX.Direct3D11;
using System;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Navmesh.Render;

public unsafe class DynamicBuffer : IDisposable
{
    public class Builder : IDisposable
    {
        public int NextElement { get; private set; }
        private RenderContext _ctx;
        private DynamicBuffer _buffer;
        private DataStream _stream;

        internal Builder(RenderContext ctx, DynamicBuffer buffer)
        {
            _ctx = ctx;
            _buffer = buffer;
            ctx.Context.MapSubresource(buffer.Buffer, MapMode.WriteDiscard, MapFlags.None, out _stream);
        }

        public void Dispose()
        {
            _ctx.Context.UnmapSubresource(_buffer.Buffer, 0);
        }

        // TODO: reconsider this api
        public DataStream Stream => _stream;
        public void Advance(int count)
        {
            NextElement += count;
            if (NextElement > _buffer.NumElements)
                throw new ArgumentOutOfRangeException("Buffer overflow");
        }
    }

    public int ElementSize { get; init; }
    public int NumElements { get; init; }
    public Buffer Buffer { get; init; }

    public DynamicBuffer(RenderContext ctx, int elementSize, int numElements, BindFlags bindFlags)
    {
        ElementSize = elementSize;
        NumElements = numElements;
        Buffer = new(ctx.Device, new()
        {
            SizeInBytes = elementSize * numElements,
            Usage = ResourceUsage.Dynamic,
            BindFlags = bindFlags,
            CpuAccessFlags = CpuAccessFlags.Write,
        });
    }

    public void Dispose()
    {
        Buffer.Dispose();
    }

    public Builder Map(RenderContext ctx) => new Builder(ctx, this);
}
