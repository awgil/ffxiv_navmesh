using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX;
using System;
using System.Runtime.InteropServices;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Render;

public class EffectQuad : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 Center;
        public Vector3 WorldX;
        public Vector3 WorldY;
        public Vector4 Color;
    }

    public class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _quads;

            internal Builder(RenderContext ctx, Data data)
            {
                _quads = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _quads.Dispose();
            }

            public void Add(ref Instance inst) => _quads.Add(ref inst);
            public void Add(Vector3 center, Vector3 wx, Vector3 wy, Vector4 color) => _quads.Add(new Instance() { Center = center, WorldX = wx, WorldY = wy, Color = color });
        }

        private RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new(ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Draw* should be called after EffectQuad.Bind set up its state
        public void DrawSubset(RenderContext ctx, int firstQuad, int numQuads)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.Draw(numQuads, firstQuad);
        }

        public void DrawAll(RenderContext ctx) => DrawSubset(ctx, 0, _buffer.CurElements);
    }

    private SharpDX.Direct3D11.Buffer _constantBuffer;
    private InputLayout _il;
    private VertexShader _vs;
    private GeometryShader _gs;
    private PixelShader _ps;

    public EffectQuad(RenderContext ctx)
    {
        var shader = """
            struct Quad
            {
                float3 center : World0;
                float3 worldX : World1;
                float3 worldY : World2;
                float4 color : Color;
            };

            struct GSOutput
            {
                float4 projPos : SV_Position;
                float4 color : Color;
            };

            struct Constants
            {
                float4x4 viewProj;
            };
            Constants k : register(c0);

            Quad vs(Quad v)
            {
                return v;
            }

            void addFace(inout TriangleStream<GSOutput> output, float3 p, float3 pa, float3 pb, float3 pc, float3 pd, float4 color)
            {
                GSOutput v;
                v.color = color;

                v.projPos = mul(float4(p + pa, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(p + pd, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(p + pb, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(p + pc, 1), k.viewProj);
                output.Append(v);

                output.RestartStrip();
            }

            float4 faceColor(float4 base, float coeff)
            {
                return float4(base.rgb * coeff, base.a);
            }

            [maxvertexcount(4)]
            void gs(point Quad input[1], inout TriangleStream<GSOutput> output)
            {
                Quad quad = input[0];
                float3 wo = quad.center;
                float3 wx = quad.worldX;
                float3 wy = quad.worldY;

                GSOutput v;
                v.color = quad.color;
                v.color.a *= 0.7;

                v.projPos = mul(float4(wo + wx + wy, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(wo + wx - wy, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(wo - wx + wy, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(wo - wx - wy, 1), k.viewProj);
                output.Append(v);

                output.RestartStrip();
            }

            float4 ps(GSOutput input) : SV_Target
            {
                return input.color;
            }
            """;
        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        Service.Log.Debug($"Quad VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var gs = ShaderBytecode.Compile(shader, "gs", "gs_5_0");
        Service.Log.Debug($"Quad GS compile: {gs.Message}");
        _gs = new(ctx.Device, gs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        Service.Log.Debug($"Quad PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 16 * 4 * 2, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vs.Bytecode,
            [
                new InputElement("World", 0, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 1, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 2, Format.R32G32B32_Float, -1, 0),
                new InputElement("Color", 0, Format.R32G32B32_Float, -1, 0),
            ]);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _gs.Dispose();
        _ps.Dispose();
    }

    public void UpdateConstants(RenderContext ctx, Constants consts)
    {
        consts.ViewProj.Transpose();
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Bind(RenderContext ctx)
    {
        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
        ctx.Context.InputAssembler.InputLayout = _il;
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.GeometryShader.Set(_gs);
        ctx.Context.GeometryShader.SetConstantBuffer(0, _constantBuffer);
        ctx.Context.PixelShader.Set(_ps);
    }

    // shortcut to bind + draw
    public void Draw(RenderContext ctx, Data data)
    {
        Bind(ctx);
        data.DrawAll(ctx);
    }

    public void DrawSubset(RenderContext ctx, Data data, int firstQuad, int numQuads)
    {
        Bind(ctx);
        data.DrawSubset(ctx, firstQuad, numQuads);
    }
}
