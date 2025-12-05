using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(1291)]
internal class Z1291Phaenna : NavmeshCustomization
{
    public override int Version => 3;

    public override void CustomizeTile(Tile tile)
    {
        tile.AddCylinder(new Vector3(2, 10, 2), new Vector3(-206.5f, 29, 301.5f));

        List<InstanceWithMesh> toAdd = [];

        string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/common/collision/c1w0_03_t200a.pcb"];

        foreach (var liner in doubleLiners)
        {
            foreach (var obj in tile.ObjectsByPath(liner))
            {
                var mtx = Matrix.CreateTransform(new(4.5f, 6.25f, 0.5f), Quaternion.Identity, new Vector3(1.5f, 3.75f, 1.5f)) * obj.Instance.WorldTransform.FullMatrix();
                toAdd.Add(SceneTool.CreateSimpleBox(obj.Instance.Id + 0x100, mtx, obj.Instance.WorldBounds));
            }
        }

        foreach (var rock in tile.ObjectsByPath("bg/ffxiv/cos_c1/hou/c1w2/collision/c1w2_t0_roc31.pcb"))
        {
            var mtx = Matrix.CreateTransform(new(-1, 0, -0.5f), Quaternion.Identity, new(0.5f, 2, 0.5f)) * rock.Instance.WorldTransform.FullMatrix();
            toAdd.Add(SceneTool.CreateSimpleBox(rock.Instance.Id + 0x100, mtx, rock.Instance.WorldBounds));
        }

        foreach (var obj in toAdd)
            tile.Objects.TryAdd(obj.Instance.Id, obj);
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

        if (festivalVersion < 0x06)
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

        if (festivalVersion < 0x0F)
            return;

        #region peninsula
        // soda-lime float <-> peninsula E
        addCosmoliner(new(255, -9.5f, 156), new(pi, 0, pi), new(185, -5.5f, 406), default);

        // peninsula E <-> peninsula SW
        addCosmoliner(new(185, -5.5f, 454), new(pi, 0, pi), new(-64, 34, 660), new(0, -hpi, 0));

        // peninsula E <-> peninsula NW
        addCosmoliner(new(161, -5.5f, 430), new(0, hpi, 0), new(-136, 28.5f, 305), new(0, -hpi, 0));

        // peninsula SW <-> peninsula NW
        addCosmoliner(new(-88, 34, 636), default, new(-160, 28.5f, 329), new(pi, 0, pi));
        #endregion

        #region scoresheen sands
        // N sands <-> NW
        addCosmoliner(new(-623.029f, -2, -656.971f), new(0, -0.785f, 0), new(-156.909f, 62.5f, -752.908f), new(-pi, -1.484f, -pi));

        // N sands <-> E1 sands
        addCosmoliner(new(-623.029f, -2, -623.029f), new(pi, 0.785f, -pi), new(-422, -2, -430.785f), new(0, 0.524f, 0));

        // N sands <-> W sands
        addCosmoliner(new(-656.971f, -2, -623.029f), new(pi, -0.785f, pi), new(-768, 13.5f, -294), default);

        // E1 sands <-> W
        addCosmoliner(new(-389.215f, -2, -422), new(0, -1.047f, 0), new(-100, 25.5f, -402), new(0, hpi, 0));

        // E1 sands <-> W sands
        addCosmoliner(new(-430.785f, -2, -398), new(pi, -1.047f, pi), new(-744, 13.5f, -270), new(0, -hpi, 0));

        // E1 sands <-> E2 sands
        addCosmoliner(new(-398, -2, -389.215f), new(-pi, 0.524f, -pi), new(-326.971f, -5, -151.971f), new(0, 0.785f, 0));

        // E2 sands <-> SW
        addCosmoliner(new(-293.029f, -5, -151.971f), new(0, -0.785f, 0), new(-6.971f, -10, -110.029f), new(pi, -0.785f, pi));

        // E2 sands <-> peninsula NW
        addCosmoliner(new(-293.029f, -5, -118.029f), new(pi, 0.785f, pi), new(-160, 28.5f, 281), default);

        // E2 sands <-> S sands
        addCosmoliner(new(-326.971f, -5, -118.029f), new(pi, -0.785f, pi), new(-556, 24.5f, 50), new(0, -hpi, 0));

        // W sands <-> S sands
        addCosmoliner(new(-768, 13.5f, -246), new(pi, 0, pi), new(-604, 24.5f, 50), new(0, hpi, 0));
        #endregion

        if (festivalVersion < 0x14)
            return;

        #region pools
        // soda-lime float <-> pools E
        addCosmoliner(new(279, -9.5f, 132), new(0, -hpi, 0), new(830, -168, 415), new(0, 0.349f, 0));

        // pools E <-> pools S
        addCosmoliner(new(830, -168, 455), new(pi, -0.349f, pi), new(549.696f, -220, 748.473f), new(0, -1.396f, 0));

        // pools S <-> chasm
        addCosmoliner(new(510.304f, -220, 741.527f), new(0, 1.047f, 0), new(405.832f, -230, 253.635f), new(-pi, -0.175f, pi));

        // chasm <-> pools middle
        addCosmoliner(new(433.635f, -230, 234.168f), new(pi, 1.396f, pi), new(660, -242, 420), new(0, 0.785f, 0));
        #endregion

        if (festivalVersion < 0x25)
            return;

        #region southwestern penis
        addCosmoliner(new(-580, 24.5f, 74), new(pi, 0, -pi), new(-363.473f, 11, 375.304f), new(0, 0.524f, 0));

        addCosmoliner(new(-356.527f, 11, 414.696f), new(pi, -0.175f, pi), new(-580, 28, 715), new(0, -hpi, 0));
        #endregion
    }
}
