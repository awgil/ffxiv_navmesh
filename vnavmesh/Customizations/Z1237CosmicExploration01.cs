using DotRecast.Detour;
using System;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(1237)]
internal class Z1237CosmicExploration01 : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        scene.InsertCylinderCollider(new Vector3(2, 10, 2), new Vector3(-206.5f, 29, 301.5f));
    }

    private static readonly Vector3 DepartOffset = new(4.5f, 2.5f, 3);
    private static readonly Vector3 ArriveOffset = new(-4.5f, 2.7f, 1.3f);

    public override void CustomizeMesh(DtNavMesh mesh)
    {
        (Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation)
        {
            var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var adjD = Vector3.Transform(DepartOffset, q);
            var adjA = Vector3.Transform(ArriveOffset, q);
            return (adjD + worldPos, adjA + worldPos);
        }

        void linkPortals(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation)
        {
            var (depA, arrA) = getPoints(pointAPos, pointARotation);
            var (depB, arrB) = getPoints(pointBPos, pointBRotation);

            LinkPoints(mesh, depA, arrB);
            LinkPoints(mesh, depB, arrA);
        }

        // base <-> N
        linkPortals(new(0, 1, -65), default, new(0, 38, -376), new(MathF.PI, 0, -MathF.PI));

        // base <-> E
        linkPortals(new(65, 1, 0), new(0, -MathF.PI / 2, 0), new(376, 40, 0), new(0, MathF.PI / 2, 0));

        // N <-> NE
        linkPortals(new(24, 38, -400), new(0, -MathF.PI / 2, 0), new(272, 40.5f, -320), new(0, 0.698f, 0));

        // NE <-> E
        // the Y rotation sign is flipped in vnav debug view (-0.698 vs 0.698), what causes the discrepancy?
        linkPortals(new(311.65f, 40.5f, -272.75f), new(MathF.PI, 0.698f, -MathF.PI), new(400, 40, -24), default);
    }
}
