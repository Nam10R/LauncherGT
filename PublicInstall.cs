using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class PublicInstall
{
    private const string DxcUrl = "https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2609/dxc_2026_09_29.zip";
    private const string DxcHash = "ad31b1fc8443175d204f77a611fdb3ef2ec42759bdc2f1167368de24a4a7e7f1";
    private const string CompilerHash = "0689e2fee0a1c9df8662a59680469f24e2a3ad7ce6bbbcc9284f7a83172bfc59";
    private const string DxilHash = "64be2368211d257a038a7d4ea6bb3e24c878eb8d588c7b8414fccf759b69c2ac";
    private const string Target = "x86_64-pc-windows-msvc";
    private static readonly Regex CommitPattern = new Regex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    // Public installations made before receipts used the same name without the final "-v2".
    private static readonly Regex InstallName = new Regex(@"\A[0-9a-f]{40}-[0-9a-f]{32}(?:-v2)?\z", RegexOptions.CultureInvariant);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static string Key(string emulator)
    {
        if (String.Equals(emulator, "xcl", StringComparison.OrdinalIgnoreCase)) return "xcl";
        if (String.Equals(emulator, "victory", StringComparison.OrdinalIgnoreCase)) return "victory";
        throw new ArgumentException("Emulador desconocido.");
    }

    private static string Repo(string key)
    {
        return key == "xcl" ? "ivall/xcl" : "Marvisak/VIctory";
    }

    private static string Root(string key)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "LanzadorGtaVI", "Emuladores", key);
    }

    private static string BundledFolder(string key)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Emuladores", key == "xcl" ? "XCL" : "VIctory");
    }

    public static bool IsBundled(string emulator, string executable)
    {
        string key = Key(emulator);
        return !String.IsNullOrEmpty(executable) &&
            String.Equals(executable, Path.Combine(BundledFolder(key), key + ".exe"), StringComparison.OrdinalIgnoreCase);
    }

    private static string BundledCommit(string key)
    {
        try { return VerifyReceipt(BundledFolder(key), key, null); }
        catch (IOException) { return ""; }
        catch (InvalidDataException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static string Active(string root, string key)
    {
        string pointer = Path.Combine(root, "activo.txt");
        try
        {
            if (!File.Exists(pointer)) return "";
            string name = File.ReadAllText(pointer).Trim();
            if (!InstallName.IsMatch(name)) return "";
            string folder = Path.Combine(root, name);
            if (!File.Exists(Path.Combine(folder, key + ".exe"))) return "";
            if (key == "xcl" && (!File.Exists(Path.Combine(folder, "dxcompiler.dll")) ||
                                 !File.Exists(Path.Combine(folder, "dxil.dll")))) return "";
            if (name.EndsWith("-v2", StringComparison.Ordinal)) VerifyReceipt(folder, key, name.Substring(0,40));
            return name;
        }
        catch (IOException) { return ""; }
        catch (InvalidDataException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    public static string ExecutablePath(string emulator)
    {
        string key = Key(emulator);
        string root = Root(key);
        string name = Active(root, key);
        if (name.Length != 0) return Path.Combine(root, name, key + ".exe");
        return BundledCommit(key).Length != 0 ? Path.Combine(BundledFolder(key), key + ".exe") : "";
    }

    public static string InstalledCommit(string emulator)
    {
        string key = Key(emulator);
        string name = Active(Root(key), key);
        return name.Length == 0 ? BundledCommit(key) : name.Substring(0, 40);
    }

    private static WebResponse Open(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "LanzadorGtaVI/1.0";
        request.Timeout = 45000;
        request.ReadWriteTimeout = 90000;
        return request.GetResponse();
    }

    public static async Task<string> LatestAsync(string emulator)
    {
        var info = await UpdateChecker.CheckAsync(emulator, null).ConfigureAwait(false);
        if (info.Online && CommitPattern.IsMatch(info.RemoteSha)) return info.RemoteSha;
        throw new InvalidDataException("No se pudo verificar la última revisión de " + emulator + ". " + info.Detail);
    }

    public static Task<string> InstallAsync(string emulator, string commit, IProgress<string> progress)
    {
        string key = Key(emulator);
        if (commit == null || !CommitPattern.IsMatch(commit)) throw new ArgumentException("La revisión seleccionada es inválida.");
        return Task.Run(() => Install(key, commit, Root(key), progress));
    }

    private static void Report(IProgress<string> progress, string message)
    {
        if (progress != null) progress.Report(message);
    }

    private static string Install(string key, string sha, string root, IProgress<string> progress)
    {
        Directory.CreateDirectory(root);
        FileStream transaction;
        try { transaction = new FileStream(Path.Combine(root, "instalar.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        catch (IOException ex) { throw new IOException("Otra instalación está usando la carpeta de " + key + ". Volvé a intentar en unos minutos.", ex); }
        using (transaction)
        {
            string active = Active(root, key);
            if (active.StartsWith(sha + "-", StringComparison.Ordinal))
            {
                string existing = Path.Combine(root, active, key + ".exe");
                VerifyManagedExecutable(key, existing);
                return existing;
            }
            Report(progress, "Comprobando Rust, las herramientas de C++ y el SDK de Windows…");
            string cargo = FindCargo();
            CheckMsvc();

            string nonce = Guid.NewGuid().ToString("N");
            string name = sha + "-" + nonce + "-v2";
            string scratch = Path.Combine(Path.GetTempPath(), "vi-" + nonce.Substring(0, 12));
            string stage = Path.Combine(root, name);
            string pointer = Path.Combine(root, "activo.txt");
            string next = Path.Combine(root, nonce + ".tmp");
            bool committed = false;
            try
            {
                Directory.CreateDirectory(scratch);
                Directory.CreateDirectory(stage);
                string source = Path.Combine(scratch, "src");
                string repo = Repo(key);
                string archive = Path.Combine(scratch, "fuente.zip");
                Report(progress, "Descargando el código oficial de " + (key == "xcl" ? "XCL" : "VIctory") + "…");
                Download("https://codeload.github.com/" + repo + "/zip/" + sha, archive);
                ExtractSource(archive, repo.Substring(repo.IndexOf('/') + 1) + "-" + sha + "/", source);

                Report(progress, "Compilando en esta PC. La primera vez puede tardar varios minutos…");
                string target = Path.Combine(scratch, "target");
                // Rustup applies the project's rust-toolchain.toml in VIctory's source directory.
                string channel = key == "xcl" ? "+stable-x86_64-pc-windows-msvc " : "";
                string args = channel + "build --locked --release --target " + Target + " -p " + key + " --bin " + key + " --jobs 4";
                Run(cargo, args, source, target, progress, "Falló la compilación de " + key);
                string executable = Path.Combine(stage, key + ".exe");
                File.Copy(Path.Combine(target, Target, "release", key + ".exe"), executable);
                File.Copy(Path.Combine(source, "LICENSE"), Path.Combine(stage, "LICENSE"));
                string notices = Path.Combine(source, "THIRD_PARTY.md");
                if (File.Exists(notices)) File.Copy(notices, Path.Combine(stage, "THIRD_PARTY.md"));
                if (key == "xcl")
                {
                    Report(progress, "Descargando y verificando el compilador de shaders de Microsoft…");
                    InstallDxc(scratch, stage);
                }
                SaveReceipt(stage, key, sha);
                Report(progress, "Analizando la instalación con Microsoft Defender…");
                PersonalProtection.Scan(stage);
                VerifyReceipt(stage, key, sha);
                Report(progress, "Comprobando que el emulador abre…");
                Run(executable, "--help", stage, null, null, "El emulador compilado no respondió a la verificación");
                VerifyReceipt(stage, key, sha);
                File.WriteAllText(next, name, Utf8);
                if (File.Exists(pointer)) File.Replace(next, pointer, null);
                else File.Move(next, pointer);
                committed = true;
                return executable;
            }
            finally
            {
                try { File.Delete(next); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (!committed) Clean(root, name);
                Clean(Path.GetTempPath(), "vi-" + nonce.Substring(0, 12));
            }
        }
    }

    private static void Clean(string parent, string name)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        string ownedDirectory = Path.GetFullPath(Path.Combine(root, name));
        if (!String.Equals(Path.GetDirectoryName(ownedDirectory), root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(ownedDirectory)) Directory.Delete(ownedDirectory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string HashFile(string path)
    {
        using (var file = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }

    private static Dictionary<string, string> FileHashes(string folder, string key, string sha, bool includeDxc = true)
    {
        var hashes = new Dictionary<string, string>();
        hashes["commit"] = sha;
        hashes["sha256"] = HashFile(Path.Combine(folder, key + ".exe"));
        if (key == "xcl" && includeDxc)
        {
            hashes["dxcompilerSha256"] = HashFile(Path.Combine(folder, "dxcompiler.dll"));
            hashes["dxilSha256"] = HashFile(Path.Combine(folder, "dxil.dll"));
        }
        return hashes;
    }

    private static void SaveReceipt(string folder, string key, string sha)
    {
        var data = new JavaScriptSerializer().Serialize(FileHashes(folder, key, sha));
        File.WriteAllText(Path.Combine(folder, key + ".exe.update.json"), data, Utf8);
    }

    private static string VerifyReceipt(string folder, string key, string expectedSha)
    {
        const string invalid = "La instalación de este emulador cambió. Volvé a descargarlo.";
        var path = new FileInfo(Path.Combine(folder, key + ".exe.update.json"));
        if (!path.Exists || path.Length > 4096) throw new InvalidDataException(invalid);
        var serializer = new JavaScriptSerializer { MaxJsonLength = 4096 };
        Dictionary<string, string> receipt;
        try { receipt = serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path.FullName)); }
        catch (ArgumentException ex) { throw new InvalidDataException(invalid, ex); }
        catch (InvalidOperationException ex) { throw new InvalidDataException(invalid, ex); }
        string sha;
        if (receipt == null || !receipt.TryGetValue("commit", out sha) || !CommitPattern.IsMatch(sha ?? "") ||
            (expectedSha != null && !String.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(invalid);
        foreach (var expected in FileHashes(folder, key, sha, expectedSha != null))
        {
            string actual;
            if (!receipt.TryGetValue(expected.Key, out actual) ||
                !String.Equals(expected.Value, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(invalid);
        }
        return sha;
    }

    internal static void VerifyManagedExecutable(string emulator, string executable)
    {
        string key = Key(emulator);
        string folder = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (IsBundled(emulator, executable))
        {
            VerifyReceipt(folder, key, null);
            return;
        }
        string name = Path.GetFileName(folder);
        if (!String.Equals(Path.GetDirectoryName(folder), Root(key), StringComparison.OrdinalIgnoreCase) ||
            !InstallName.IsMatch(name) || !name.EndsWith("-v2", StringComparison.Ordinal)) return;
        VerifyReceipt(folder, key, name.Substring(0, 40));
    }

    private static string FindCargo()
    {
        var bins = new List<string>();
        string cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME");
        if (!String.IsNullOrEmpty(cargoHome)) bins.Add(Path.Combine(cargoHome, "bin"));
        bins.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin"));
        bins.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        foreach (string candidate in bins)
        {
            string directory = candidate.Trim().Trim('"');
            if (!Path.IsPathRooted(directory)) continue;
            string cargo = Path.Combine(directory, "cargo.exe");
            if (File.Exists(cargo) && File.Exists(Path.Combine(directory, "rustup.exe"))) return cargo;
        }
        throw new InvalidOperationException("Para compilar, instalá Rust x64 para Windows con rustup desde https://rustup.rs/ y las Build Tools de Visual Studio con «Desarrollo para el escritorio con C++» y el SDK de Windows. Después volvé a pulsar Descargar.");
    }

    private static void CheckMsvc()
    {
        string vcHome = Environment.GetEnvironmentVariable("VCToolsInstallDir");
        if (!String.IsNullOrEmpty(vcHome) && File.Exists(Path.Combine(vcHome, @"bin\Hostx64\x64\cl.exe")) &&
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIB"))) return;
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                      @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (File.Exists(vswhere))
        {
            var start = new ProcessStartInfo(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var process = Process.Start(start))
            {
                string installation = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && Directory.Exists(installation)) return;
            }
        }
        throw new InvalidOperationException("Instalá las Build Tools de Visual Studio con «Desarrollo para el escritorio con C++», herramientas MSVC x64 y un SDK de Windows 10 u 11 desde https://visualstudio.microsoft.com/visual-cpp-build-tools/ y volvé a pulsar Descargar.");
    }

    private static void Download(string url, string file)
    {
        using (var response = Open(url))
        using (var input = response.GetResponseStream())
        using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            if (response.ContentLength >= 0 && output.Length != response.ContentLength)
                throw new EndOfStreamException("La descarga quedó incompleta. Volvé a intentar.");
        }
    }

    private static void ExtractSource(string archive, string prefix, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (var input = File.OpenRead(archive))
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                    entry.FullName.IndexOfAny(new[] { '\\', ':' }) >= 0)
                    throw new InvalidDataException("La estructura del ZIP no corresponde a la revisión seleccionada.");
                string relative = entry.FullName.Substring(prefix.Length);
                if (relative.Length == 0) continue;
                string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El ZIP contiene una ruta fuera del proyecto.");
                if (entry.Name.Length == 0) Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ExtractFile(entry, path);
                }
            }
        }
        if (!File.Exists(Path.Combine(root, "Cargo.lock")) || !File.Exists(Path.Combine(root, "Cargo.toml")))
            throw new InvalidDataException("Al código descargado le faltan archivos de compilación.");
    }

    private static void ExtractFile(ZipArchiveEntry entry, string path)
    {
        using (var src = entry.Open())
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            src.CopyTo(output);
    }

    private static string DxcCache()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "LanzadorGtaVI", "Componentes", "DXC", DxcHash.Substring(0,12));
    }

    private static bool HasDxc(string folder)
    {
        try
        {
            return HashFile(Path.Combine(folder, "dxcompiler.dll")) == CompilerHash &&
                   HashFile(Path.Combine(folder, "dxil.dll")) == DxilHash;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string VerifiedDxcDirectory()
    {
        string folder = DxcCache();
        if (!HasDxc(folder)) throw new InvalidDataException("Faltan componentes de XCL. Volvé a pulsar Jugar con XCL para descargarlos.");
        return folder;
    }

    internal static Task EnsureBundledDependenciesAsync(string emulator, string executable, IProgress<string> progress)
    {
        if (Key(emulator) != "xcl" || !IsBundled(emulator, executable)) return Task.FromResult(0);
        return Task.Run(() => EnsureDxc(DxcCache(), progress));
    }

    private static void EnsureDxc(string folder, IProgress<string> progress)
    {
        if (HasDxc(folder)) return;
        Directory.CreateDirectory(folder);
        FileStream transaction;
        try { transaction = new FileStream(Path.Combine(folder, "instalar.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        catch (IOException ex) { throw new IOException("Otra ventana está preparando XCL. Cuando termine, volvé a pulsar Jugar.", ex); }
        using (transaction)
        {
            if (HasDxc(folder)) return;
            string unique = ".dxc-" + Guid.NewGuid().ToString("N");
            string scratch = Path.Combine(folder, unique);
            try
            {
                Directory.CreateDirectory(scratch);
                Report(progress, "Descargando los componentes gráficos de XCL desde Microsoft…");
                InstallDxc(scratch, scratch);
                foreach (string name in new[] { "LICENSE-LLVM.txt", "LICENSE-MS.txt", "dxil.dll", "dxcompiler.dll" })
                {
                    string source = Path.Combine(scratch, name);
                    string dest = Path.Combine(folder, name);
                    if (File.Exists(dest)) File.Replace(source, dest, null);
                    else File.Move(source, dest);
                }
            }
            finally { Clean(folder, unique); }
        }
    }

    private static void InstallDxc(string scratch, string stage)
    {
        string archive = Path.Combine(scratch, "dxc.zip");
        Download(DxcUrl, archive);
        ExtractDxc(archive, stage);
    }

    private static void ExtractDxc(string archive, string stage)
    {
        using (var input = File.OpenRead(archive))
        using (var sha = SHA256.Create())
        {
            string digest = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            if (digest != DxcHash) throw new InvalidDataException("El paquete de Microsoft no coincide con su SHA-256 publicado.");
            input.Position = 0;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                    "bin/x64/dxcompiler.dll", "bin/x64/dxil.dll", "LICENSE-LLVM.txt", "LICENSE-MS.txt"
                };
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    // The official Windows archive uses backslashes in its ZIP entry names.
                    string relative = entry.FullName.Replace('\\', '/');
                    if (required.Remove(relative))
                        ExtractFile(entry, Path.Combine(stage, relative.Substring(relative.LastIndexOf('/') + 1)));
                }
                if (required.Count != 0) throw new InvalidDataException("Al paquete de Microsoft le faltan archivos de DXC para Windows x64.");
            }
        }
        if (!HasDxc(stage)) throw new InvalidDataException("Los archivos DXC no coinciden con la versión de Microsoft.");
    }

    private static void Run(string file, string args, string directory, string target, IProgress<string> progress, string error)
    {
        var start = new ProcessStartInfo(file, args) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // XCL checks its private child-process mode before it processes --help.
        start.EnvironmentVariables.Remove("XCL_HOST");
        if (target != null) start.EnvironmentVariables["CARGO_TARGET_DIR"] = target;
        var tail = new Queue<string>();
        var timer = Stopwatch.StartNew();
        DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs ev)
        {
            string line = ev.Data;
            if (String.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                if (tail.Count == 12) tail.Dequeue();
                tail.Enqueue(line.Length > 450 ? line.Substring(0, 450) : line);
                if (progress != null && timer.ElapsedMilliseconds > 6000 && line.TrimStart().StartsWith("Compiling "))
                {
                    progress.Report("Compilando " + line.Trim().Substring("Compiling ".Length).Split(' ')[0] + "…");
                    timer.Restart();
                }
            }
        };
        using (var process = new Process { StartInfo = start })
        {
            process.OutputDataReceived += receive;
            process.ErrorDataReceived += receive;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error + "." + Environment.NewLine + String.Join(Environment.NewLine, tail.ToArray()));
        }
    }
}
