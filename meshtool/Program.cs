using DotRecast.Detour.Io;

internal class Program
{
    private static int Main(string[] args)
    {
        switch (args) {
            case ["export", var path]:
                DoExport(path);
                return 0;
            default:
                Console.WriteLine($"Usage: {Environment.ProcessPath} export <path-to-file>");
                return 1;
        }

    }

    static void DoExport(string cachePath)
    {
        var cacheFile = new FileInfo(cachePath);
        using var stream = cacheFile.OpenRead();
        using var reader = new BinaryReader(stream);
        var mesh = Navmesh.Navmesh.Deserialize(reader, 1);

        var outFile = new FileInfo(cacheFile.Name);
        using var s2 = outFile.OpenWrite();
        using var writer = new BinaryWriter(s2);
        new DtMeshSetWriter().Write(writer, mesh.Mesh, DotRecast.Core.RcByteOrder.LITTLE_ENDIAN, false);

        Console.WriteLine($"wrote converted mesh to {outFile.FullName}");
    }
}
