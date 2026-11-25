using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

internal static class PublicLibrary
{
    private const string PreferenceSubkey = @"Software\LanzadorGtaVI";
    private const string PreferenceKey = @"HKEY_CURRENT_USER\" + PreferenceSubkey;
    private const string FolderValue = "CarpetaDelJuego";
    private const string ManifestName = "MicrosoftGame.Config";

    public static string GameFolder
    {
        get
        {
            string folder = Registry.GetValue(PreferenceKey, FolderValue, null) as string;
            return String.IsNullOrWhiteSpace(folder) ? null : folder;
        }
    }

    public static string SelectGameFolder()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "Elegí la carpeta de tu juego que contiene MicrosoftGame.Config.";
            dialog.ShowNewFolderButton = false;
            string current = GameFolder;
            if (Directory.Exists(current)) dialog.SelectedPath = current;
            if (dialog.ShowDialog() != DialogResult.OK) return null;
            SetGameFolder(dialog.SelectedPath);
            return GameFolder;
        }
    }

    public static void SetGameFolder(string folder)
    {
        ResolveTitle(folder);
        Registry.SetValue(PreferenceKey, FolderValue, Path.GetFullPath(folder), RegistryValueKind.String);
    }

    private static string ExecutableName(string emulator)
    {
        if (String.Equals(emulator, "VIctory", StringComparison.OrdinalIgnoreCase)) return "victory.exe";
        if (String.Equals(emulator, "XCL", StringComparison.OrdinalIgnoreCase)) return "xcl.exe";
        throw new ArgumentException("El emulador debe ser XCL o VIctory.", "emulator");
    }

    private static bool MatchesExecutable(string path, string expected)
    {
        return !String.IsNullOrWhiteSpace(path) && File.Exists(path) && Path.IsPathRooted(path) &&
            String.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static string ExternalExecutable(string emulator)
    {
        string expected = ExecutableName(emulator);
        string path = Registry.GetValue(PreferenceKey, expected, null) as string;
        return MatchesExecutable(path, expected) ? path : null;
    }

    private sealed class Receipt
    {
        public string sha256 { get; set; }
        public string commit { get; set; }
    }

    public static string ExternalCommit(string executable)
    {
        try
        {
            var file = new FileInfo(executable + ".update.json");
            if (!file.Exists || file.Length > 4096) return null;
            var receipt = new JavaScriptSerializer { MaxJsonLength = 4096 }.Deserialize<Receipt>(File.ReadAllText(file.FullName));
            if (receipt == null || !Regex.IsMatch(receipt.commit ?? "", @"\A[0-9a-fA-F]{40}\z")) return null;
            using (var input = File.OpenRead(executable))
            using (var hash = SHA256.Create())
            {
                string sum = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
                return String.Equals(sum, receipt.sha256, StringComparison.OrdinalIgnoreCase) ? receipt.commit.ToLowerInvariant() : null;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string SelectExecutable(string emulator)
    {
        string expected = ExecutableName(emulator);
        using (var dialog = new System.Windows.Forms.OpenFileDialog())
        {
            dialog.Title = "Elegí el ejecutable de " + emulator;
            dialog.Filter = expected + "|" + expected;
            dialog.FileName = expected;
            dialog.CheckFileExists = true;
            dialog.RestoreDirectory = true;
            string current = ExternalExecutable(emulator);
            if (current != null) dialog.InitialDirectory = Path.GetDirectoryName(current);
            if (dialog.ShowDialog() != DialogResult.OK) return null;
            if (!MatchesExecutable(dialog.FileName, expected))
                throw new FileNotFoundException("Elegí el archivo " + expected + " de tu instalación.");
            string path = Path.GetFullPath(dialog.FileName);
            Registry.SetValue(PreferenceKey, expected, path, RegistryValueKind.String);
            return path;
        }
    }

    public static void ClearExternal(string emulator)
    {
        string expected = ExecutableName(emulator);
        using (var key = Registry.CurrentUser.OpenSubKey(PreferenceSubkey, true))
        {
            if (key != null) key.DeleteValue(expected, false);
        }
    }

    public static string ValidateGame(string folder)
    {
        try { ResolveTitle(folder); return null; }
        catch (InvalidDataException error) { return error.Message; }
        catch (IOException error) { return error.Message; }
        catch (UnauthorizedAccessException) { return "Windows no permitió leer la carpeta del juego."; }
        catch (ArgumentException error) { return error.Message; }
        catch (NotSupportedException) { return "La ruta de la carpeta no es válida."; }
    }

    private static string ResolveTitle(string folder)
    {
        if (String.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Primero elegí la carpeta de tu juego.");
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("No se encontró la carpeta de tu juego.");
        string manifest = Path.Combine(root, ManifestName);
        if (!File.Exists(manifest)) throw new FileNotFoundException("La carpeta elegida debe contener MicrosoftGame.Config.");

        XDocument document;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 };
        try
        {
            using (var reader = XmlReader.Create(manifest, settings)) document = XDocument.Load(reader);
        }
        catch (XmlException error)
        {
            throw new InvalidDataException("MicrosoftGame.Config no es un manifiesto XML válido.", error);
        }

        var game = document.Root;
        if (game == null || game.Name.LocalName != "Game")
            throw new InvalidDataException("MicrosoftGame.Config no tiene el elemento Game.");
        XNamespace ns = game.Name.Namespace;
        var entries = game.Elements(ns + "ExecutableList").Elements(ns + "Executable")
            .Where(x => !String.Equals((string)x.Attribute("IsDevOnly"), "true", StringComparison.OrdinalIgnoreCase) && (string)x.Attribute("IsDevOnly") != "1")
            .OrderByDescending(x => String.Equals((string)x.Attribute("TargetDeviceFamily"), "Scarlett", StringComparison.OrdinalIgnoreCase));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            string name = ((string)entry.Attribute("Name") ?? "").Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains(":") || Path.IsPathRooted(name)) continue;
            if (!String.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string title = Path.GetFullPath(Path.Combine(root, name));
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(title)) return title;
        }
        throw new FileNotFoundException("No se encontró el ejecutable de juego indicado por MicrosoftGame.Config en la carpeta elegida.");
    }

    public static ProcessStartInfo Prepare(string emulator, string executablePath, string gameFolder, bool inspectOnly)
    {
        string expected = ExecutableName(emulator);
        bool victory = expected == "victory.exe";
        if (!MatchesExecutable(executablePath, expected))
            throw new FileNotFoundException("Primero instalá " + emulator + " o elegí su ejecutable.");

        string title = ResolveTitle(gameFolder);
        string binary = Path.GetFullPath(executablePath);
        if (String.Equals(title, binary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La carpeta elegida corresponde al emulador. Elegí la carpeta del juego.");
        PublicInstall.VerifyManagedExecutable(emulator, binary);
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(binary) };
        // XCL uses the package root for content lookup. Inspection and VIctory take the title itself.
        string argument = !inspectOnly && !victory ? Path.GetFullPath(gameFolder) : title;
        string trimmed = argument.TrimEnd('\\');
        string quoted = "\"" + trimmed + new string('\\', (argument.Length - trimmed.Length) * 2) + "\"";
        start.Arguments = (inspectOnly ? "inspect " : "run ") + quoted + (!inspectOnly && victory ? " --execute" : "");
        if (victory)
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanzadorGtaVI", "VIctory");
            start.EnvironmentVariables.Remove("VICTORY_CONFIG");
            start.EnvironmentVariables.Remove("VICTORY_TEST_TITLES");
            start.EnvironmentVariables["VICTORY_GAME_DIR"] = Path.GetFullPath(gameFolder);
            start.EnvironmentVariables["VICTORY_CACHE_DIR"] = Path.Combine(local, "cache");
            start.EnvironmentVariables["VICTORY_DATA_DIR"] = Path.Combine(local, "data");
        }
        else
        {
            // The launcher starts the XCL CLI. Only XCL itself may start a host child.
            start.EnvironmentVariables.Remove("XCL_HOST");
            if (!inspectOnly && PublicInstall.IsBundled(emulator, binary))
                start.EnvironmentVariables["XCL_DXC_DIR"] = PublicInstall.VerifiedDxcDirectory();
        }
        return start;
    }

    public static Process Start(string emulator, string executablePath)
    {
        return Process.Start(Prepare(emulator, executablePath, GameFolder, false));
    }
}
