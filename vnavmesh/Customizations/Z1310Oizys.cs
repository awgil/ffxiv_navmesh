using DotRecast.Detour;
using Navmesh;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace vnavmesh.Customizations;

[CustomizationTerritory(1310)]
internal class Z1310Oizys : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/c1w3/collision/c1w3_03_t200a.pcb"];

        foreach (var liner in doubleLiners)
        {
            if (scene.Meshes.TryGetValue(liner, out var cl))
            {
                // prevent agent from trying to climb the side of the ramp of a green cosmoliner - can cause issues if idiots set a very high path tolerance
                var departVerts = CollectionsMarshal.AsSpan(cl.Parts[29].Vertices);
                departVerts[129].Y += 1;
                departVerts[130].Y += 1;
                departVerts[132].Y += 1;
                departVerts[133].Y += 1;

                var box = SceneExtractor.BuildBoxMesh()[0];
                foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
                {
                    vert *= new Vector3(1.5f, 3.75f, 1.5f);
                    vert += new Vector3(4.5f, 6.25f, -1);
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
            var adjD = Vector3.Transform(new(4.5f, 2.5f, 0.8f), q);
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

        // add jump down point for a raised rock that a drone spawns on
        LinkPoints(mesh, new(148.5f, -92, -540), new(150.25f, -92.725f, -536f));

        #region base
        // base -> N
        addCosmoliner(new(-180, 0.5f, 52), default, new(-150, -23, -213), new(pi, 0, pi));

        // base -> W
        addCosmoliner(new(-299, 0.5f, 138), new Vector3(0, hpi, 0), new(-518, 23, 120), new(0, -hpi, 0));

        // base -> E
        addCosmoliner(new(-61, 0.5f, 138), new Vector3(0, -hpi, 0), new(156, -0.5f, -8), new(0, hpi, 0));
        #endregion

        #region E
        // North
        addCosmoliner(new(180, -0.5f, -32), default, new(192, -54.5f, -376), new(pi, 0, pi));

        // East
        addCosmoliner(new(204, -0.5f, -8), new(0, -hpi, 0), new(496, -52.5f, -289), new(-pi, 0, pi));
        #endregion

        #region NE
        // E -> Far East (against map)
        addCosmoliner(new(216, -54.5f, -400), new(0, -hpi, 0), new(472, -52.5f, -313), new(0, hpi, 0));

        // N -> Far N (against map, beside pin)
        addCosmoliner(new(192, -54.5f, -424), default, new(310, -154, -602), new(-pi, 0, -pi));
        #endregion

        #region NE far
        // NE above location -> E below location name
        addCosmoliner(new(334, -154, -626), new(0, -hpi, 0), new(496, -52.5f, -337), default);
        #endregion

        #region SE
        // North -> Mid East
        addCosmoliner(new(92, 97.5f, 329), default, new(180, -0.5f, 16), new(pi, 0, pi));

        // E -> Single on E
        addCosmoliner(new(116, 97.5f, 353), new(0, -hpi, 0), new(319.215f, 100, 407), new(pi, -1.047f, pi));
        #endregion

        #region SW far
        // North Side -> SW Cosmo
        addCosmoliner(new(-445, 101.5f, 746), default, new(-388, 44.5f, 425), new(pi, 0, pi));

        // West Side -> West Cosmo
        addCosmoliner(new(-469, 101.5f, 770), new(0, hpi, 0), new(-661, 28, 455), new(-pi, 0, -pi));
        #endregion

        #region SW wall
        // N -> West of base
        addCosmoliner(new(-661, 28, 407), default, new(-566, 23, 120), new(0, hpi, 0));

        // E -> SE Cosmoliner
        addCosmoliner(new(-637, 28, 431), new(0, -hpi, 0), new(-412, 44.5f, 401), new(0, hpi, 0));
        #endregion

        #region SW
        // N -> West of base
        addCosmoliner(new(-388, 44.5f, 377), default, new(-542, 23, 144), new(-pi, 0, -pi));

        // E -> SE of base
        addCosmoliner(new(-364, 44.5f, 401), new(0, -hpi, 0), new(68, 97.5f, 353), new(0, hpi, 0));
        #endregion

        #region W
        // North -> NW (Below Erg Eris)
        addCosmoliner(new(-542, 23, 96), default, new(-530, -27.5f, -216), new(pi, 0, -pi));
        #endregion

        #region NW
        // N -> NW (Above Erg Eris, below flower looking hole)
        addCosmoliner(new(-530, -27.5f, -264), default, new(-702, -88, -470), new(pi, 0, -pi));

        // E -> Cosmoliner N of base
        addCosmoliner(new(-506, -27.5f, -240), new(0, -hpi, 0), new(-174, -23, -237), new(0, hpi, 0));
        #endregion

        #region N
        // N -> Far North
        addCosmoliner(new(-150, -23, -261), default, new(-132, -75, -558), new(pi, 0, -pi));

        // E -> NE
        addCosmoliner(new(-126, -23, -237), new(0, -hpi, 0), new(168, -54.5f, -400), new(0, hpi, 0));
        #endregion

        #region NN
        // W -> Far NW (East Side
        addCosmoliner(new(-156, -75.5f, -582), new(0, hpi, 0), new(-678, -88, -494), new(0, -hpi, 0));

        // N -> FARR NW/Against N Wall
        addCosmoliner(new(-132, -75.5f, -606), default, new(-456, -105f, -760), new(0, -hpi, 0));
        #endregion

        #region NNW
        // North -> NW Cosmoliner
        addCosmoliner(new(-702, -88, -518), default, new(-504, -105, -760), new(0, hpi, 0));
        #endregion
    }
}

