using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX;
using System;
using System.Runtime.InteropServices;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace Navmesh.Render;

public class EffectBox : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Matrix4x3 World;
        public Vector4 ColorTop;
        public Vector4 ColorSide;
    }

    public class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _boxes;

            internal Builder(RenderContext ctx, Data data)
            {
                _boxes = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _boxes.Dispose();
            }

            public void Add(ref Instance inst) => _boxes.Add(ref inst);
            public void Add(ref Matrix4x3 world, Vector4 colorTop, Vector4 colorSide) => _boxes.Add(new Instance() { World = world, ColorTop = colorTop, ColorSide = colorSide });

            public void Add(Vector3 min, Vector3 max, Vector4 colorTop, Vector4 colorSide)
            {
                var center = (max + min) * 0.5f;
                var extent = (max - min) * 0.5f;
                _boxes.Add(new() { World = new() { M11 = extent.X, M22 = extent.Y, M33 = extent.Z, M41 = center.X, M42 = center.Y, M43 = center.Z }, ColorTop = colorTop, ColorSide = colorSide });
            }
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

        // Draw* should be called after EffectBox.Bind set up its state
        public void DrawSubset(RenderContext ctx, int firstBox, int numBoxes)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.Draw(numBoxes, firstBox);
        }

        public void DrawAll(RenderContext ctx) => DrawSubset(ctx, 0, _buffer.CurElements);
    }

    private SharpDX.Direct3D11.Buffer _constantBuffer;
    private InputLayout _il;
    private VertexShader _vs;
    private GeometryShader _gs;
    private PixelShader _ps;

    public EffectBox(RenderContext ctx)
    {
        var shader = """
            struct Box
            {
                float3 worldRow0 : World0;
                float3 worldRow1 : World1;
                float3 worldRow2 : World2;
                float3 worldRow3 : World3;
                float4 colorTop : Color0;
                float4 colorSide : Color1;
            };

            struct GSOutput
            {
                float4 projPos : SV_Position;
                float4 color : Color;
                //float3 bary : Barycentric;
            };

            struct Constants
            {
                float4x4 viewProj;
            };
            Constants k : register(c0);

            Box vs(Box v)
            {
                return v;
            }

            void addFace(inout TriangleStream<GSOutput> output, float3 p, float3 pa, float3 pb, float3 pc, float3 pd, float4 color)
            {
                GSOutput v;
                v.color = color;

                v.projPos = mul(float4(p + pa, 1), k.viewProj);
                //v.bary = float3(1, 0, 0);
                output.Append(v);
                v.projPos = mul(float4(p + pd, 1), k.viewProj);
                //v.bary = float3(0, 1, 0);
                output.Append(v);
                v.projPos = mul(float4(p + pb, 1), k.viewProj);
                //v.bary = float3(0, 0, 1);
                output.Append(v);
                v.projPos = mul(float4(p + pc, 1), k.viewProj);
                //v.bary = float3(1, 0, 0);
                output.Append(v);

                output.RestartStrip();
            }

            float4 faceColor(float4 base, float coeff)
            {
                return float4(base.rgb * coeff, base.a);
            }

            [maxvertexcount(24)]
            void gs(point Box input[1], inout TriangleStream<GSOutput> output)
            {
                Box box = input[0];
                float3 wx = box.worldRow0;
                float3 wy = box.worldRow1;
                float3 wz = box.worldRow2;
                float3 wo = box.worldRow3;

                addFace(output, wo + wx, wy + wz, -wy + wz, -wy - wz, wy - wz, faceColor(box.colorSide, 0.65));
                addFace(output, wo + wy, wx + wz, wx - wz, -wx - wz, -wx + wz, faceColor(box.colorTop, 0.98));
                addFace(output, wo + wz, wx + wy, -wx + wy, -wx - wy, wx - wy, faceColor(box.colorSide, 0.65));
                addFace(output, wo - wx, wy + wz, wy - wz, -wy - wz, -wy + wz, faceColor(box.colorSide, 0.85));
                addFace(output, wo - wy, wx + wz, -wx + wz, -wx - wz, wx - wz, faceColor(box.colorSide, 0.55));
                addFace(output, wo - wz, wx + wy, wx - wy, -wx - wy, -wx + wy, faceColor(box.colorSide, 0.85));
            }

            float4 ps(GSOutput input) : SV_Target
            {
                bool edge = false; //min(input.bary.y, input.bary.z) < 0.01; - this doesn't really work that well, the threshold should depend on edge length...
                float alpha = input.color.a * (edge ? 1 : 0.7);
                return float4(input.color.rgb, alpha);
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

        _constantBuffer = new(ctx.Device, 16 * 4 * 2, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vs.Bytecode,
            [
                new InputElement("World", 0, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 1, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 2, Format.R32G32B32_Float, -1, 0),
                new InputElement("World", 3, Format.R32G32B32_Float, -1, 0),
                new InputElement("Color", 0, Format.R32G32B32A32_Float, -1, 0),
                new InputElement("Color", 1, Format.R32G32B32A32_Float, -1, 0),
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

    public void DrawSubset(RenderContext ctx, Data data, int firstBox, int numBoxes)
    {
        Bind(ctx);
        data.DrawSubset(ctx, firstBox, numBoxes);
    }
}
