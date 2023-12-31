using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX;
using System;
using System.Runtime.InteropServices;

namespace Navmesh.Render;

public unsafe class BoxRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix View;
        public Matrix Proj;
    }

    public class Builder : IDisposable
    {
        private BoxRenderer _renderer;
        private DynamicBuffer.Builder _boxes;

        internal Builder(RenderContext ctx, BoxRenderer renderer)
        {
            _renderer = renderer;
            _boxes = renderer._buffer.Map(ctx);

            renderer._count = 0;
        }

        public void Dispose()
        {
            _boxes.Dispose();
        }

        public void Add(ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world, System.Numerics.Vector4 color)
        {
            ++_renderer._count;
            _boxes.Advance(1);
            _boxes.Stream.Write(world.Row0);
            _boxes.Stream.Write(world.Row1);
            _boxes.Stream.Write(world.Row2);
            _boxes.Stream.Write(world.Row3);
            _boxes.Stream.Write(color);
        }
    }

    public int MaxCount { get; init; }

    private DynamicBuffer _buffer;
    private SharpDX.Direct3D11.Buffer _constantBuffer;
    private InputLayout _il;
    private VertexShader _vs;
    private GeometryShader _gs;
    private PixelShader _ps;
    private int _count;

    public BoxRenderer(RenderContext ctx, int maxCount)
    {
        MaxCount = maxCount;

        var shader = """
            struct Box
            {
                float3 worldRow0 : World0;
                float3 worldRow1 : World1;
                float3 worldRow2 : World2;
                float3 worldRow3 : World3;
                float4 color : Color;
            };

            struct GSOutput
            {
                float4 projPos : SV_Position;
                float4 color : COLOR;
            };

            struct Constants
            {
                float4x4 view;
                float4x4 proj;
            };
            Constants k : register(c0);

            Box vs(Box v)
            {
                return v;
            }

            void addFace(inout TriangleStream<GSOutput> output, float3 origin, float3 op, float3 pa, float3 pb, float3 pc, float3 pd, float4 color)
            {
                float3 normal = normalize(op);

                GSOutput v;
                v.color = color;
                v.color.rgb *= lerp(0.1, 1.0, normal.z);
                v.color.a *= 0.7;

                float3 p = origin + op;
                v.projPos = mul(float4(p + pa, 1), k.proj);
                output.Append(v);
                v.projPos = mul(float4(p + pd, 1), k.proj);
                output.Append(v);
                v.projPos = mul(float4(p + pb, 1), k.proj);
                output.Append(v);
                v.projPos = mul(float4(p + pc, 1), k.proj);
                output.Append(v);

                output.RestartStrip();
            }

            [maxvertexcount(24)]
            void gs(point Box input[1], inout TriangleStream<GSOutput> output)
            {
                Box box = input[0];
                float3 vx = mul(float4(box.worldRow0, 0), k.view).xyz;
                float3 vy = mul(float4(box.worldRow1, 0), k.view).xyz;
                float3 vz = mul(float4(box.worldRow2, 0), k.view).xyz;
                float3 origin = mul(float4(box.worldRow3, 1), k.view).xyz;

                addFace(output, origin, vx, vy + vz, -vy + vz, -vy - vz, vy - vz, box.color);
                addFace(output, origin, vy, vx + vz, vx - vz, -vx - vz, -vx + vz, box.color);
                addFace(output, origin, vz, vx + vy, -vx + vy, -vx - vy, vx - vy, box.color);
                addFace(output, origin, -vx, vy + vz, vy - vz, -vy - vz, -vy + vz, box.color);
                addFace(output, origin, -vy, vx + vz, -vx + vz, -vx - vz, vx - vz, box.color);
                addFace(output, origin, -vz, vx + vy, vx - vy, -vx - vy, -vx + vy, box.color);
            }

            float4 ps(GSOutput input) : SV_Target
            {
                return input.color;
            }
            """;
        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        Service.Log.Debug($"Box VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var gs = ShaderBytecode.Compile(shader, "gs", "gs_5_0");
        Service.Log.Debug($"Box GS compile: {gs.Message}");
        _gs = new(ctx.Device, gs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        Service.Log.Debug($"Box PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _buffer = new(ctx, 16 * 4, maxCount, BindFlags.VertexBuffer);
        _constantBuffer = new(ctx.Device, 16 * 4 * 2, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vs.Bytecode,
            [
                new InputElement("World", 0, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 1, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 2, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 3, Format.R32G32B32_Float, -1, 0),
                new InputElement("Color", 0, Format.R32G32B32A32_Float, -1, 0),
            ]);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _gs.Dispose();
        _ps.Dispose();
    }

    public Builder Build(RenderContext ctx, Constants consts)
    {
        consts.View.Transpose();
        consts.Proj.Transpose();
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
        return new Builder(ctx, this);
    }

    public void Draw(RenderContext ctx, bool wireframe)
    {
        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
        ctx.Context.InputAssembler.InputLayout = _il;
        ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.GeometryShader.Set(_gs);
        ctx.Context.GeometryShader.SetConstantBuffer(0, _constantBuffer);
        ctx.Context.PixelShader.Set(_ps);
        ctx.Context.Draw(_count, 0);
    }
}
