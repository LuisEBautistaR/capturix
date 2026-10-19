using Capturix.Common.Storage;

namespace Capturix.Capture;

/// <summary>
/// Checks save-location detection and library moves (only scratch files under &lt;outdir&gt; are
/// moved): Capturix.exe --selftest-storage &lt;outdir&gt;
/// </summary>
internal static class StorageSelfTest
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        int failures = 0;
        void Check(bool ok, string text)
        {
            report.Add($"{(ok ? "OK   " : "FALLO")} {text}");
            if (!ok)
                failures++;
        }

        foreach (var target in StorageLocations.GetTargets(customFolder: ""))
            report.Add($"      {target.Kind,-12} {(target.IsAvailable ? target.Folder : "(no disponible)")}");
        var oneDriveRoots = StorageLocations.OneDriveRoots();
        report.Add($"      Raíces de OneDrive: {(oneDriveRoots.Count == 0 ? "(ninguna)" : string.Join("; ", oneDriveRoots))}");

        Check(StorageLocations.SyncProviderOf(StorageLocations.LocalFolder) is null, "La carpeta local no está sincronizada");
        if (oneDriveRoots.Count > 0)
        {
            Check(StorageLocations.SyncProviderOf(Path.Combine(oneDriveRoots[0], "Capturix")) == "OneDrive", "Una carpeta dentro de OneDrive se reconoce");
            Check(StorageLocations.SyncProviderOf(oneDriveRoots[0] + "Falso") is null, "Un nombre que solo empieza igual no cuenta como OneDrive");
            Check(StorageLocations.Resolve(SaveLocation.OneDrive, "") == Path.Combine(oneDriveRoots[0], "Capturix"), "«onedrive» resuelve a OneDrive\\Capturix");
        }
        if (StorageLocations.GoogleDriveRoot() is null)
            Check(StorageLocations.Resolve(SaveLocation.GoogleDrive, "") == StorageLocations.LocalFolder,
                "Sin Google Drive instalado, «googledrive» vuelve a la carpeta local");
        Check(StorageLocations.Resolve(SaveLocation.Custom, outDir) == outDir, "«custom» usa la carpeta elegida");

        // Move: one name clash, one non-capture file that must stay behind.
        string from = Path.Combine(outDir, "origen"), to = Path.Combine(outDir, "destino");
        foreach (var dir in new[] { from, to })
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(Path.Combine(from, "a.cpx"), "a");
        File.WriteAllText(Path.Combine(from, "b.png"), "b");
        File.WriteAllText(Path.Combine(from, "c.mp4"), "c");
        File.WriteAllText(Path.Combine(from, "notas.txt"), "no es una captura");
        File.WriteAllText(Path.Combine(to, "b.png"), "ya existía");

        var (moved, failed) = LibraryMover.MoveAll(from, to);
        Check(moved == 3 && failed.Count == 0, $"Se mueven las 3 capturas ({moved} movidas, {failed.Count} fallos)");
        Check(File.ReadAllText(Path.Combine(to, "b.png")) == "ya existía" && File.ReadAllText(Path.Combine(to, "b_2.png")) == "b",
            "Un nombre repetido se renombra en vez de sobrescribir");
        Check(File.Exists(Path.Combine(from, "notas.txt")), "Los archivos que no son capturas se quedan");

        report.Add(failures == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {failures} fallos");
        File.WriteAllLines(Path.Combine(outDir, "storage-report.txt"), report);
        return failures == 0 ? 0 : 1;
    }
}
