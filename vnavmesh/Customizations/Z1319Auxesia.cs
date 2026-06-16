using Navmesh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace vnavmesh.Customizations;

[CustomizationTerritory(1319)]
internal class Z1319Auxesia : NavmeshCustomization
{
	public override int Version => 1;

	public override void CustomizeScene(SceneExtractor scene)
	{
		string[] doubleLiners = ["bg/ffxiv/cos_c1/hou/c1w4/collision/c1w4_03_t200a.pcb", "bg/ffxiv/cos_c1/hou/c1w4/collision/c1w4_03_t300a.pcb"];

		foreach (var liner in doubleLiners)
		{
			if (scene.Meshes.TryGetValue(liner, out var mesh))
			{
				// prevent agent from trying to climb the side of the ramp of a green cosmoliner - can cause issues if idiots set a very high path tolerance
				var departVerts = CollectionsMarshal.AsSpan(mesh.Parts[49].Vertices);
				departVerts[81].Y += 1;
				departVerts[85].Y += 1;

				var box = SceneExtractor.BuildBoxMesh()[0];
				foreach (ref var vert in CollectionsMarshal.AsSpan(box.Vertices))
				{
					vert *= new Vector3(1.5f, 3.75f, 1.5f);
					vert += new Vector3(4.5f, 6.25f, -1);
				}
				mesh.Parts.Add(box);
			}
		}
	}

	const float pi = MathF.PI;
	const float hpi = pi / 2;

	public override void CustomizeMesh(Navmesh.Navmesh mesh, List<uint> festivalLayers)
	{
		(Vector3 DepartPoint, Vector3 ArrivePoint) getPoints(Vector3 worldPos, Vector3 rotation, bool wide)
		{
			var q = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
			var offX = wide ? 9 : 4.5f;
			var adjD = Vector3.Transform(new(offX, 2.5f, 1.5f), q);
			var adjA = Vector3.Transform(new(-offX, 2.7f, 1.8f), q);
			return (adjD + worldPos, adjA + worldPos);
		}

		void addCosmoliner(Vector3 pointAPos, Vector3 pointARotation, Vector3 pointBPos, Vector3 pointBRotation, bool wide = false)
		{
			var (depA, arrA) = getPoints(pointAPos, pointARotation, wide);
			var (depB, arrB) = getPoints(pointBPos, pointBRotation, wide);

			LinkPoints(mesh, depA, arrB);
			LinkPoints(mesh, depB, arrA);
		}

		var festivalPhase = festivalLayers.LastOrDefault() >> 16;

		// first expansion: base
		if (festivalPhase < 5)
			return;

		#region base
		// base -> NW
		addCosmoliner(new Vector3(252.109f, 205.8f, 337.109f), new Vector3(0f, 0.785f, 0f), new Vector3(-8.908f, 184f, 23.909f), new Vector3(pi, 0.087f, pi));

		// base -> NE
		addCosmoliner(new(329.891f, 205.8f, 337.109f), new(0, -0.785f, 0), new(596.029f, 187.5f, 148.971f), new(-pi, -0.785f, -pi));

		// base -> SE
		addCosmoliner(new(329.891f, 205.8f, 414.891f), new(pi, 0.785f, pi), new(498.206f, 185, 761.160f), new(0, 0.873f, 0));

		// base -> SW
		addCosmoliner(new Vector3(252.109f, 205.8f, 414.891f), new Vector3(pi, -0.785f, pi), new Vector3(19.971f, 167.5f, 696.029f), new Vector3(0, -0.785f, 0));
		#endregion

		#region SE island
		// SE -> sprouting
		addCosmoliner(new Vector3(535.794f, 185f, 774.84f), new Vector3(0, -hpi, 0), new Vector3(677.129f, 245f, 508.754f), new Vector3(pi, 0.506f, pi));
		#endregion

		#region NE island
		// NE -> sprouting
		addCosmoliner(new Vector3(670.871f, 245f, 469.246f), new Vector3(0, -0.192f, 0), new Vector3(629.971f, 187.5f, 148.971f), new Vector3(pi, 0.785f, pi));

		// NE -> scrambling
		addCosmoliner(new Vector3(596.029f, 187.5f, 115.029f), new Vector3(0, 0.785f, 0), new Vector3(12.909f, 184, -2.092f), new Vector3(0, -hpi, 0));
		#endregion

		#region Scrambling
		// scrambling -> fullbloom
		addCosmoliner(new Vector3(-13.092f, 184, -23.909f), new Vector3(0, 0.087f, 0), new Vector3(-206.012f, 145, -544.302f), new Vector3(pi, 1.187f, pi));

		// scrambling -> orchidelirium
		addCosmoliner(new Vector3(-34.909f, 184, 2.092f), new Vector3(-pi, -hpi, -pi), new Vector3(-365.447f, 167.5f, -170.209f), new Vector3(0, -1.222f, 0));
		#endregion

		#region New Growth City
		// ngc -> unnamed island north
		addCosmoliner(new Vector3(-13.971f, 167.5f, 696.029f), new Vector3(0, 0.785f, 0), new Vector3(-17.679f, 185, 243), new Vector3(pi, 0.698f, pi));

		// ngc -> pileus pergola
		addCosmoliner(new Vector3(-13.971f, 167.5f, 729.971f), new Vector3(pi, -0.785f, pi), new Vector3(-225.615f, 167.5f, 323.427f), new Vector3(pi, 0.873f, pi));
		#endregion

		#region Pileus Pergola
		// pileus -> orchidelirium
		addCosmoliner(new Vector3(-228.573f, 167.5f, 289.615f), new Vector3(0, -0.698f, 0), new Vector3(-410.553f, 167.5f, -153.792f), new Vector3(pi, -1.222f, pi));

		// pileus -> blossomingway south
		addCosmoliner(new Vector3(-262.385f, 167.5f, 292.573f), new Vector3(0, 0.873f, 0), new Vector3(-650.59f, 185, 338.815f), new Vector3(pi, hpi, -pi));
		#endregion

		#region Blossomingway
		// blossomingway -> south island
		addCosmoliner(new Vector3(-747, 185, 17.321f), new Vector3(-pi, 0.873f, -pi), new Vector3(-687.41f, 185, 323.185f), new Vector3(0, 0.82f, 0));
		#endregion

		// second expansion: sylvan stacks
		if (festivalPhase < 17)
			return;

		#region Sylvan Stacks
		// sylvan -> fullbloom
		addCosmoliner(new Vector3(-245.988f, 145, -545.698f), new Vector3(pi, -1.257f, -pi), new Vector3(-583.029f, 206, -378.971f), new Vector3(0, -0.785f, 0));

		// sylvan -> orchidelirium
		addCosmoliner(new Vector3(-583.029f, 206, -345.029f), new Vector3(pi, 0.785f, pi), new Vector3(-396.209f, 167.5f, -184.553f), new Vector3(0, 0.349f, 0));

		// sylvan -> blossomingway
		addCosmoliner(new Vector3(-616.971f, 206, -345.029f), new Vector3(pi, -0.785f, pi), new Vector3(-767, 185, -17.321f), new Vector3(0, 0.175f, 0));
		#endregion

		// third expansion: stratostone
		if (festivalPhase < 24)
			return;

		#region Stratostone
		addCosmoliner(new Vector3(-259.427f, 167.5f, 326.385f), new Vector3(-pi, -0.698f, -pi), new Vector3(-667.941f, 115, 614.165f), new Vector3(0, -0.733f, 0));
		#endregion

		// final expansion: grandma laurel
		if (festivalPhase < 27)
			return;

		#region Grandma Laurel
		addCosmoliner(new Vector3(193, 483, -380), new Vector3(0, hpi, 0), new Vector3(126, 166, -139), new Vector3(0, -0.786f, 0), true);
		#endregion
	}
}
