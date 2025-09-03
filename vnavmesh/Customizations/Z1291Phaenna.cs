using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Customizations;

[CustomizationTerritory(1291)]
internal class Z1291Phaenna : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/c1w2/collision/c1w2_03_t200a.pcb"];

        foreach (var liner in doubleLiners)
        {
            if (scene.Meshes.TryGetValue(liner, out var cl))
            {
                var box = SceneExtractor.BuildBoxMesh()[0];
                foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
                {
                    vert *= new Vector3(1.5f, 3.75f, 1.5f);
                    vert += new Vector3(4.5f, 6.25f, 0.5f);
                }
                cl.Parts.Add(box);
            }
        }
    }

    const float pi = MathF.PI;
    const float hpi = pi / 2;

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        (Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation)
        {
            var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var adjD = Vector3.Transform(new(4.5f, 2.5f, 2.3f), q);
            var adjA = Vector3.Transform(new(-4.5f, 2.7f, 1.8f), q);
            return (adjD + worldPos, adjA + worldPos);
        }

        void addCosmoliner(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation)
        {
            var (depA, arrA) = getPoints(pointAPos, pointARotation);
            var (depB, arrB) = getPoints(pointBPos, pointBRotation);

            LinkPoints(mesh, depA, arrB);
            LinkPoints(mesh, depB, arrA);
        }

        var festivalVersion = festivalLayers.FirstOrDefault() >> 16;

        if (festivalVersion < 6)
            return;

        #region base liners
        // base <-> N
        addCosmoliner(new(340, 52.5f, -486), default, new(300, 135, -756), new(pi, 0, -pi));

        // base <-> E
        addCosmoliner(new(406, 52.5f, -420), new(0, -hpi, 0), new(756, 52, -430), new(0, hpi, 0));

        // base <-> S
        addCosmoliner(new(340, 52.5f, -354), new(pi, 0, -pi), new(330, 52.5f, -152), default);

        // base <-> W
        addCosmoliner(new(274, 52.5f, -420), new(0, hpi, 0), new(-52, 25.5f, -402), new(0, -hpi, 0));
        #endregion

        #region inner ring liners
        // N <-> NE
        addCosmoliner(new(324, 135, -780), new(0, -hpi, 0), new(687.144f, 45, -730.321f), new(0, 1.047f, 0));

        // NE <-> E
        addCosmoliner(new(712.856f, 45, -699.679f), new(pi, 0.349f, pi), new(780, 52, -454), default);

        // E <-> SE
        addCosmoliner(new(780, 52, -406), new(-pi, 0, pi), new(730, 36, -61), default);

        // SE <-> S
        addCosmoliner(new(706, 36, -37), new(0, hpi, 0), new(354, 52.5f, -128), new(0, -hpi, 0));

        // S <-> SW
        addCosmoliner(new(306, 52.5f, -128), new(0, hpi, 0), new(26.971f, -10, -143.971f), new(0, -0.785f, 0));

        // SW <-> W
        addCosmoliner(new(-6.971f, -10, -143.971f), new(0, 0.785f, 0), new(-76, 25.5f, -378), new(-pi, 0, pi));

        // W <-> NW
        addCosmoliner(new(-76, 25.5f, -426), default, new(-130.908f, 62.5f, -731.091f), new(-pi, 0.087f, -pi));

        // NW <-> N
        addCosmoliner(new(-109.091f, 62.5f, -757.092f), new(0, -1.484f, 0), new(276, 135, -780), new(0, hpi, 0));

        // S <-> soda-lime float
        addCosmoliner(new(330, 52.5f, -104), new(-pi, 0, -pi), new(255, -9.5f, 108), default);

        // soda-lime float <-> SW
        addCosmoliner(new(231, -9.5f, 132), new(0, hpi, 0), new(26.971f, -10, -110.029f), new(pi, 0.785f, pi));
        #endregion
    }
}
