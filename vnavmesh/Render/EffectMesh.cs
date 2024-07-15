using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Render;

public class EffectMesh : IDisposable
{
    public record struct Mesh(int FirstVertex, int FirstPrimitive, int NumPrimitives, int FirstInstance, int NumInstances);

    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos;
        public float LightingWorldYThreshold; // to match recast demo, this should be equal to cos of max walkable angle
    }

    // can't use tuple of 3 ints directly because of alignment :(
    [StructLayout(LayoutKind.Sequential)]
    public struct Triangle
    {
        public int V1;
        public int V2;
        public int V3;

        public Triangle((int v1, int v2, int v3) tuple) => (V1, V2, V3) = tuple;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector4 WorldColX;
        public Vector4 WorldColY;
        public Vector4 WorldColZ;
        public Vector4 Color;

        public Instance(Matrix4x3 world, Vector4 color)
        {
            WorldColX = new(world.M11, world.M21, world.M31, world.M41);
            WorldColY = new(world.M12, world.M22, world.M32, world.M42);
            WorldColZ = new(world.M13, world.M23, world.M33, world.M43);
            Color = color;
        }
    }

    public class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private Data _data;
            private RenderBuffer<Vector3>.Builder _vertices;
            private RenderBuffer<Triangle>.Builder _primitives;
            private RenderBuffer<Instance>.Builder _instances;

            public int NumVertices => _vertices.CurElements;
            public int NumPrimitives => _primitives.CurElements;
            public int NumInstances => _instances.CurElements;

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

            // manually fill the mesh
            public void AddVertex(Vector3 v) => _vertices.Add(v);
            public void AddTriangle(int v1, int v2, int v3) => _primitives.Add(new((v1, v2, v3)));
            public void AddInstance(Instance i) => _instances.Add(ref i);
            public void AddMesh(int firstVertex, int firstPrimitive, int numPrimitives, int firstInstance, int numInstances) => _data._meshes.Add(new(firstVertex, firstPrimitive, numPrimitives, firstInstance, numInstances));
        }

        private RenderBuffer<Vector3> _vertexBuffer;
        private RenderBuffer<Triangle> _primBuffer;
        private RenderBuffer<Instance> _instanceBuffer;
        private List<Mesh> _meshes = new();

        public IReadOnlyList<Mesh> Meshes => _meshes;

        public Data(RenderContext ctx, int maxVertices, int maxPrimitives, int maxInstances, bool dynamic)
        {
            _vertexBuffer = new(ctx, maxVertices, BindFlags.VertexBuffer, dynamic);
            _primBuffer = new(ctx, maxPrimitives, BindFlags.IndexBuffer, dynamic);
            _instanceBuffer = new(ctx, maxInstances, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _vertexBuffer.Dispose();
            _primBuffer.Dispose();
            _instanceBuffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // bind buffers without drawing - useful for drawing multiple meshes manually in a loop
        public void Bind(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer.Buffer, _vertexBuffer.ElementSize, 0), new VertexBufferBinding(_instanceBuffer.Buffer, _instanceBuffer.ElementSize, 0));
            ctx.Context.InputAssembler.SetIndexBuffer(_primBuffer.Buffer, Format.R32_UInt, 0);
        }

        // draw custom mesh; assumes both effect and data were bound
        public void DrawManual(RenderContext ctx, Mesh mesh) => ctx.Context.DrawIndexedInstanced(mesh.NumPrimitives * 3, mesh.NumInstances, mesh.FirstPrimitive * 3, mesh.FirstVertex, mesh.FirstInstance);

        // Draw* should be called after Bind set up its state
        public void DrawSubset(RenderContext ctx, int firstMesh, int numMeshes)
        {
            Bind(ctx);
            foreach (var m in _meshes.Skip(firstMesh).Take(numMeshes))
                DrawManual(ctx, m);
        }

        public void DrawAll(RenderContext ctx)
        {
            Bind(ctx);
            foreach (var m in _meshes)
                DrawManual(ctx, m);
        }
    }

    private SharpDX.Direct3D11.Buffer _constantBuffer;
    private InputLayout _il;
    private VertexShader _vs;
    private PixelShader _psUnlit;
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
                float3 relPos : Position;
                float4 color : Color;
                float4 projPos : SV_Position;
            };

            struct Constants
            {
                float4x4 viewProj;
                float3 cameraPos;
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
                res.relPos = float3(wx, wy, wz) - k.cameraPos;
                res.color = i.color;
                res.projPos = mul(float4(wx, wy, wz, 1), k.viewProj);
                return res;
            }

            float4 psUnlit(VSOutput input) : SV_Target
            {
                return input.color;
            }

            float4 ps(VSOutput input) : SV_Target
            {
                // calculate world-space triangle normal
                float3 ab = ddx(input.relPos);
                float3 ac = ddy(input.relPos);
                float3 normal = normalize(cross(ab, ac));

                // lighting calculations are taken from recast demo
                float tint = 0.216 * (2 + normal.x + normal.z);
                float3 lighting = float3(tint, tint, tint);
                if (-normal.y < k.lightingWorldYThreshold)
                    lighting = /*float3(0,0,0);*/lerp(lighting, float3(0.45, 0.15, 0), 0.4);

                float4 color = input.color;
                color.rgb *= lighting;

                // to give some idea of depth, attenuate color by z-based 'fog'
                //float fogStart = 0.1;
                //float fogEnd = 1.25;
                //float fogFactor = saturate((input.projPos.w / 500 - fogStart) / (fogEnd - fogStart));
                //float fogFactor = input.projPos.w < 100 ? 0 : 1;
                //float3 color = lerp(input.color.rgb, float3(0.3, 0.3, 0.32), fogFactor);
                //return float4(color, input.color.a);
                return color;
            }
            """;
        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        Service.Log.Debug($"VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var psUnlit = ShaderBytecode.Compile(shader, "psUnlit", "ps_5_0");
        Service.Log.Debug($"PS Unlit compile: {psUnlit.Message}");
        _psUnlit = new(ctx.Device, psUnlit.Bytecode);

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
        _ps.Dispose();
        _rsWireframe.Dispose();
    }

    public void UpdateConstants(RenderContext ctx, Constants consts)
    {
        consts.ViewProj = Matrix4x4.Transpose(consts.ViewProj);
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Bind(RenderContext ctx, bool unlit, bool wireframe)
    {
        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.Context.InputAssembler.InputLayout = _il;
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        if (wireframe)
            ctx.Context.Rasterizer.State = _rsWireframe;
        ctx.Context.PixelShader.Set(unlit ? _psUnlit : _ps);
        ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
    }

    // shortcut to bind + draw
    public void Draw(RenderContext ctx, Data data)
    {
        Bind(ctx, false, false);
        data.DrawAll(ctx);
    }

    public void DrawSubset(RenderContext ctx, Data data, int firstMesh, int numMeshes)
    {
        Bind(ctx, false, false);
        data.DrawSubset(ctx, firstMesh, numMeshes);
    }

    public void DrawSingle(RenderContext ctx, Data data, int index)
    {
        Bind(ctx, false, false);
        data.DrawSubset(ctx, index, 1);
    }
}
