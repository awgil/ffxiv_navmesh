using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Navmesh.Render;

public class EffectMesh : IDisposable
{
    public record struct Mesh(int FirstVertex, int FirstPrimitive, int NumPrimitives, int NumInstances);
    public record struct Instance(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 World, System.Numerics.Vector4 Color);

    public class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private Data _data;
            private RenderBuffer.Builder _vertices;
            private RenderBuffer.Builder _primitives;
            private RenderBuffer.Builder _instances;

            internal Builder(RenderContext ctx, Data data)
            {
                _data = data;
                _vertices = data._vertexBuffer.Map(ctx);
                _primitives = data._primBuffer.Map(ctx);
                _instances = data._instanceBuffer.Map(ctx);

                data._meshes.Clear();
            }

            public void Dispose()
            {
                _vertices.Dispose();
                _primitives.Dispose();
                _instances.Dispose();
            }

            public void Add(IMesh mesh, IEnumerable<Instance> instances)
            {
                var ni = 0;
                foreach (var i in instances)
                {
                    _instances.Advance(1);
                    _instances.Stream.Write(new System.Numerics.Vector4(i.World.M11, i.World.M21, i.World.M31, i.World.M41));
                    _instances.Stream.Write(new System.Numerics.Vector4(i.World.M12, i.World.M22, i.World.M32, i.World.M42));
                    _instances.Stream.Write(new System.Numerics.Vector4(i.World.M13, i.World.M23, i.World.M33, i.World.M43));
                    _instances.Stream.Write(i.Color);
                    ++ni;
                }

                var nv = mesh.NumVertices();
                var nt = mesh.NumTriangles();
                _data._meshes.Add(new(_vertices.CurElements, _primitives.CurElements, nt, ni));

                _vertices.Advance(nv);
                for (int i = 0; i < nv; ++i)
                    _vertices.Stream.Write(mesh.Vertex(i));

                _primitives.Advance(nt);
                for (int i = 0; i < nt; ++i)
                {
                    var (v1, v2, v3) = mesh.Triangle(i);
                    _primitives.Stream.Write(v1);
                    _primitives.Stream.Write(v2);
                    _primitives.Stream.Write(v3);
                }
            }

            public void Add(IMesh mesh, ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world, System.Numerics.Vector4 color) => Add(mesh, [new(world, color)]);
        }

        private RenderBuffer _vertexBuffer;
        private RenderBuffer _primBuffer;
        private RenderBuffer _instanceBuffer;
        private List<Mesh> _meshes = new();

        public Data(RenderContext ctx, int maxVertices, int maxPrimitives, int maxInstances, bool dynamic)
        {
            _vertexBuffer = new(ctx, 3 * 4, maxVertices, BindFlags.VertexBuffer, dynamic);
            _primBuffer = new(ctx, 4 * 3, maxPrimitives, BindFlags.IndexBuffer, dynamic);
            _instanceBuffer = new(ctx, 16 * 4, maxInstances, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _vertexBuffer.Dispose();
            _primBuffer.Dispose();
            _instanceBuffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Draw* should be called after EffectBox.Bind set up its state
        public void DrawAll(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer.Buffer, _vertexBuffer.ElementSize, 0), new VertexBufferBinding(_instanceBuffer.Buffer, _instanceBuffer.ElementSize, 0));
            ctx.Context.InputAssembler.SetIndexBuffer(_primBuffer.Buffer, Format.R32_UInt, 0);
            int startInst = 0;
            foreach (var m in _meshes)
            {
                ctx.Context.DrawIndexedInstanced(m.NumPrimitives * 3, m.NumInstances, m.FirstPrimitive * 3, m.FirstVertex, startInst);
                startInst += m.NumInstances;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public float LightingWorldYThreshold; // to match recast demo, this should be equal to cos of max walkable angle
    }

    private SharpDX.Direct3D11.Buffer _constantBuffer;
    private InputLayout _il;
    private VertexShader _vs;
    private GeometryShader _gs;
    private PixelShader _ps;
    private RasterizerState _rsWireframe;

    public EffectMesh(RenderContext ctx)
    {
        var shader = """
            struct Vertex
            {
                float3 pos : Position;
            };

            struct Instance
            {
                float4 worldColX : World0;
                float4 worldColY : World1;
                float4 worldColZ : World2;
                float4 color : Color;
            };

            struct VSOutput
            {
                float3 worldPos : Position;
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
                float lightingWorldYThreshold;
            };
            Constants k : register(c0);

            VSOutput vs(Vertex v, Instance i)
            {
                VSOutput res;
                float4 lp = float4(v.pos, 1.0);
                float wx = dot(lp, i.worldColX);
                float wy = dot(lp, i.worldColY);
                float wz = dot(lp, i.worldColZ);
                res.worldPos = float3(wx, wy, wz);
                res.color = i.color;
                return res;
            }

            [maxvertexcount(3)]
            void gs(triangle VSOutput input[3], inout TriangleStream<GSOutput> output)
            {
                float3 a = input[0].worldPos;
                float3 b = input[1].worldPos;
                float3 c = input[2].worldPos;
                float3 ab = b - a;
                float3 bc = c - b;
                float3 normal = normalize(cross(ab, bc));

                // lighting calculations are taken from recast demo
                float tint = 0.216 * (2 + normal.x + normal.z);
                float3 lighting = float3(tint, tint, tint);
                if (normal.y < k.lightingWorldYThreshold)
                    lighting = lerp(lighting, float3(0.75, 0.5, 0), 0.25);

                GSOutput v;
                v.color = input[0].color;
                v.color.rgb *= lighting;
                v.color.a *= 0.7;

                v.projPos = mul(float4(a, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(b, 1), k.viewProj);
                output.Append(v);
                v.projPos = mul(float4(c, 1), k.viewProj);
                output.Append(v);

                output.RestartStrip();
            }

            float4 ps(GSOutput input) : SV_Target
            {
                // to give some idea of depth, attenuate color by z-based 'fog'
                //float fogStart = 0.1;
                //float fogEnd = 1.25;
                //float fogFactor = saturate((input.projPos.w / 500 - fogStart) / (fogEnd - fogStart));
                //float fogFactor = input.projPos.w < 100 ? 0 : 1;
                //float3 color = lerp(input.color.rgb, float3(0.3, 0.3, 0.32), fogFactor);
                //return float4(color, input.color.a);
                return input.color;
            }
            """;
        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        Service.Log.Debug($"VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var gs = ShaderBytecode.Compile(shader, "gs", "gs_5_0");
        Service.Log.Debug($"GS compile: {gs.Message}");
        _gs = new(ctx.Device, gs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        Service.Log.Debug($"PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 4 * 64, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vs.Bytecode,
            [
                new InputElement("Position", 0, Format.R32G32B32_Float, 0),
                new InputElement("World", 0, Format.R32G32B32A32_Float, -1, 1, InputClassification.PerInstanceData, 1),
                new InputElement("World", 1, Format.R32G32B32A32_Float, -1, 1, InputClassification.PerInstanceData, 1),
                new InputElement("World", 2, Format.R32G32B32A32_Float, -1, 1, InputClassification.PerInstanceData, 1),
                new InputElement("Color", 0, Format.R32G32B32A32_Float, -1, 1, InputClassification.PerInstanceData, 1),
            ]);

        var rsDesc = RasterizerStateDescription.Default();
        rsDesc.FillMode = FillMode.Wireframe;
        _rsWireframe = new(ctx.Device, rsDesc);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _gs.Dispose();
        _ps.Dispose();
        _rsWireframe.Dispose();
    }

    public void UpdateConstants(RenderContext ctx, Constants consts)
    {
        consts.ViewProj.Transpose();
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Bind(RenderContext ctx, bool wireframe)
    {
        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.Context.InputAssembler.InputLayout = _il;
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        ctx.Context.GeometryShader.Set(_gs);
        ctx.Context.GeometryShader.SetConstantBuffer(0, _constantBuffer);
        if (wireframe)
            ctx.Context.Rasterizer.State = _rsWireframe;
        ctx.Context.PixelShader.Set(_ps);
    }

    // shortcut to bind + draw
    public void Draw(RenderContext ctx, Data data)
    {
        Bind(ctx, false);
        data.DrawAll(ctx);
    }
}
