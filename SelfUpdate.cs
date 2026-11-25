using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class SelfUpdate
{
    private const string Releases = "https://github.com/Nam10R/LauncherGT/releases/";
    private const string Prefix = ".LauncherGT-";
    private static readonly HttpClient Client = new HttpClient {
        Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 32 * 1024 * 1024
    };
    internal static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;
    private static string Image { get { return Assembly.GetExecutingAssembly().Location; } }

    internal static Version ParseVersion(string value)
    {
        Version version;
        if (!Version.TryParse(value, out version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != value)
            throw new InvalidDataException("La versión publicada tiene un formato desconocido.");
        return new Version(version.Major, version.Minor, version.Build, 0);
    }

    internal static async Task<Version> CheckAsync()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (var request = new HttpRequestMessage(HttpMethod.Get, Releases + "latest/download/version.txt"))
        {
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using (var reply = await Client.SendAsync(request).ConfigureAwait(false))
            {
                reply.EnsureSuccessStatusCode();
                Version published = ParseVersion((await reply.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim());
                return published > Current ? published : null;
            }
        }
    }

    internal static void Verify(string candidate, byte[] signature, Version expected)
    {
        using (var key = new RSACryptoServiceProvider(new CspParameters(24) { Flags = CspProviderFlags.CreateEphemeralKey }))
        using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Publisher.xml")))
        using (var input = File.OpenRead(candidate))
        using (var hash = SHA256.Create())
        {
            key.FromXmlString(reader.ReadToEnd());
            if (!key.VerifyHash(hash.ComputeHash(input), CryptoConfig.MapNameToOID("SHA256"), signature))
                throw new InvalidDataException("La firma del launcher no coincide con la del proyecto.");
            if (AssemblyName.GetAssemblyName(candidate).Version != expected)
                throw new InvalidDataException("La descarga no coincide con la versión anunciada.");
        }
    }

    internal static async Task PrepareAsync(Version version)
    {
        if (version <= Current) throw new InvalidOperationException("Ya tenés esa versión o una más reciente.");
        string url = Releases + "download/v" + version.ToString(3) + "/LauncherGT.exe";
        byte[] signature = await Client.GetByteArrayAsync(url + ".sig").ConfigureAwait(false);
        byte[] data = await Client.GetByteArrayAsync(url).ConfigureAwait(false);
        Stage(data, signature, version);
    }

    internal static void Stage(byte[] data, byte[] signature, Version version)
    {
        string directory = Path.Combine(Path.GetDirectoryName(Image), Prefix + Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(directory, Path.GetFileName(Image));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(candidate, data);
            File.WriteAllBytes(candidate + ".sig", signature);
            using (File.OpenRead(candidate))
            {
                Verify(candidate, signature, version);
                Process.Start(new ProcessStartInfo(candidate, "--aplicar-actualizacion " + Process.GetCurrentProcess().Id) {
                    UseShellExecute = false, WorkingDirectory = directory
                }).Dispose();
            }
        }
        catch { RemoveStage(directory); throw; }
    }

    private static bool IsStage(string name)
    {
        Guid id;
        return name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(Prefix.Length), "N", out id);
    }

    internal static void Apply(string[] args)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Image));
        int pid;
        if (args.Length != 2 || !Int32.TryParse(args[1], out pid) || pid <= 0 || !IsStage(directory.Name))
            throw new InvalidDataException("La actualización debe iniciarse desde el launcher.");
        string root = directory.Parent.FullName;
        string target = Path.Combine(root, Path.GetFileName(Image));
        Process previous = null;
        try { previous = Process.GetProcessById(pid); } catch (ArgumentException) { }
        using (previous)
            if (previous != null && !previous.WaitForExit(30000)) throw new IOException("Cerrá el launcher y volvé a intentarlo.");
        using (var gate = new FileStream(Path.Combine(root, ".LauncherGT.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            if (AssemblyName.GetAssemblyName(target).Version >= Current) return;
            string staged = Path.Combine(directory.FullName, "instalar.tmp");
            string backup = Path.Combine(directory.FullName, "anterior.bak");
            File.Copy(Image, staged);
            Verify(staged, File.ReadAllBytes(Image + ".sig"), Current);
            File.Replace(staged, target, backup);
            try
            {
                Process.Start(new ProcessStartInfo(target, "--limpiar-actualizacion " + directory.Name) {
                    UseShellExecute = false, WorkingDirectory = root
                }).Dispose();
            }
            catch { File.Replace(backup, target, null); throw; }
        }
    }

    internal static void CleanAfterRestart(string[] args)
    {
        if (args.Length != 2 || args[0] != "--limpiar-actualizacion" || !IsStage(args[1])) return;
        string directory = Path.Combine(Path.GetDirectoryName(Image), args[1]);
        Task.Run(() => {
            for (int i = 0; i < 20; ++i)
            {
                if (RemoveStage(directory)) break;
                Thread.Sleep(250);
            }
        });
    }

    private static bool RemoveStage(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            string name = Path.GetFileName(Image);
            foreach (string file in new[] {name, name + ".sig", "instalar.tmp", "anterior.bak"})
                File.Delete(Path.Combine(directory, file));
            Directory.Delete(directory);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
