using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

internal static class PersonalProtection
{
    private static string Scanner()
    {
        const string name = "MpCmdRun.exe";
        string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform");
        if (Directory.Exists(platform))
        {
            var tools = Directory.GetDirectories(platform)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.Combine(p, name));
            string scanner = tools.FirstOrDefault(File.Exists);
            if (scanner != null) return scanner;
        }
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", name);
        if (File.Exists(inbox)) return inbox;
        throw new FileNotFoundException("No se encontró el analizador de Microsoft Defender.");
    }

    // A successful scan can also mean that Defender remediated a threat.
    // Only proceed with the same complete set of unchanged installation files.
    private static void RecordFiles(string path, Dictionary<string, byte[]> files, SHA256 hash)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("La instalación contiene un enlace a otra ubicación.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) RecordFiles(child, files, hash);
            return;
        }
        using (var input = File.OpenRead(path)) files.Add(path, hash.ComputeHash(input));
    }

    internal static void Scan(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("El análisis requiere la ruta completa de la instalación.", "path");
        string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(path);
        if (root.Length < 3 || target.Length <= root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
            throw new ArgumentException("Elegí un archivo o carpeta de instalación concretos.", "path");

        var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var hash = SHA256.Create())
        {
            RecordFiles(target, before, hash);
            if (before.Count == 0) throw new IOException("La carpeta de instalación está vacía.");

            string scanner = Scanner();
            var start = new ProcessStartInfo(scanner, "-Scan -ScanType 3 -File \"" + target + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(scanner)
            };
            using (var scan = Process.Start(start))
            {
                scan.WaitForExit();
                if (scan.ExitCode != 0)
                    throw new InvalidOperationException("Microsoft Defender no confirmó el análisis (código " +
                        scan.ExitCode + "). Revisá el historial de protección de Windows.");
            }
            RecordFiles(target, after, hash);
        }
        foreach (var entry in before)
        {
            byte[] digest;
            if (!after.TryGetValue(entry.Key, out digest) || !entry.Value.SequenceEqual(digest))
                throw new IOException("Un archivo fue retirado o cambió durante el análisis. Revisá el historial de protección de Windows.");
        }
        if (before.Count != after.Count) throw new IOException("El contenido de la instalación cambió durante el análisis.");
    }
}
