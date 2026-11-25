using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

internal sealed class BuildTools
{
    internal string Cargo;
    internal string Rustup;

    internal static BuildTools Detect()
    {
        var bins = new List<string>();
        string home = Environment.GetEnvironmentVariable("CARGO_HOME");
        if (!String.IsNullOrWhiteSpace(home)) bins.Add(Path.Combine(home, "bin"));
        bins.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin"));
        bins.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        return Check(bins, HasMsvc(), HasSdk());
    }

    internal static BuildTools Check(IEnumerable<string> bins, bool msvc, bool sdk)
    {
        var tools = new BuildTools();
        foreach (string candidate in bins)
        {
            string directory = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(directory)) continue;
            string cargo = Path.Combine(directory, "cargo.exe");
            string rustup = Path.Combine(directory, "rustup.exe");
            if (tools.Cargo == null && File.Exists(cargo)) tools.Cargo = cargo;
            if (tools.Rustup == null && File.Exists(rustup)) tools.Rustup = rustup;
        }
        var missing = new List<string>();
        if (tools.Cargo == null) missing.Add("• Cargo (cargo.exe). Instalá o repará Rust desde https://rustup.rs/.");
        if (tools.Rustup == null) missing.Add("• Rustup (rustup.exe). Ejecutá el instalador de https://rustup.rs/.");
        if (!msvc) missing.Add("• Herramientas de C++ (MSVC x64/x86). En Visual Studio Installer, elegí Modificar y marcá «Desarrollo para el escritorio con C++».");
        if (!sdk) missing.Add("• SDK de Windows para x64. En Visual Studio Installer, elegí Modificar → Componentes individuales y marcá un Windows 10 SDK o Windows 11 SDK.");
        if (missing.Count != 0)
            throw new InvalidOperationException("Para compilar, el launcher necesita estos componentes que no pudo encontrar:"
                + Environment.NewLine + String.Join(Environment.NewLine, missing) + Environment.NewLine + Environment.NewLine
                + "Si ya los instalaste, cerrá y volvé a abrir el launcher."
                + (tools.Cargo == null || tools.Rustup == null ? " Si Rust sigue sin aparecer, comprobá que las carpetas de las herramientas estén en PATH." : ""));
        return tools;
    }

    internal string BuildPath(string toolchainBin)
    {
        return String.Join(";", new[] { toolchainBin, Path.GetDirectoryName(Cargo), Path.GetDirectoryName(Rustup),
            Environment.GetEnvironmentVariable("PATH") });
    }

    private static bool HasMsvc()
    {
        string vc = Environment.GetEnvironmentVariable("VCToolsInstallDir");
        if (!String.IsNullOrEmpty(vc) && File.Exists(Path.Combine(vc, @"bin\Hostx64\x64\cl.exe"))) return true;
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere)) return false;
        var start = new ProcessStartInfo(vswhere,
            @"-all -prerelease -utf8 -products * -find VC\Tools\MSVC\**\bin\Host*\x64\cl.exe") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using (var process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (string file in output.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))
                if (process.ExitCode == 0 && File.Exists(file.Trim())) return true;
        }
        return false;
    }

    private static bool HasSdk()
    {
        string[] libraries = (Environment.GetEnvironmentVariable("LIB") ?? "").Split(';');
        string[] headers = (Environment.GetEnvironmentVariable("INCLUDE") ?? "").Split(';');
        if (HasEnvironmentFile(libraries, "kernel32.lib", true) && HasEnvironmentFile(libraries, "ucrt.lib", true) &&
            HasEnvironmentFile(headers, "Windows.h", false) && HasEnvironmentFile(headers, "stdio.h", false)) return true;
        var roots = new List<string> { Environment.GetEnvironmentVariable("WindowsSdkDir"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Windows Kits\10") };
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots"))
                if (key != null) roots.Add(key.GetValue("KitsRoot10") as string);
        }
        foreach (string root in roots)
            if (HasSdkFiles(root)) return true;
        return false;
    }

    private static bool HasEnvironmentFile(string[] directories, string name, bool requireX64)
    {
        foreach (string item in directories)
        {
            string directory = item.Trim().Trim('"').TrimEnd('\\', '/');
            if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(directory)) continue;
            if (requireX64 && !String.Equals(Path.GetFileName(directory), "x64", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(directory, name))) return true;
        }
        return false;
    }

    internal static bool HasSdkFiles(string root)
    {
        if (String.IsNullOrWhiteSpace(root)) return false;
        string lib = Path.Combine(root, "Lib");
        if (!Directory.Exists(lib)) return false;
        foreach (string version in Directory.GetDirectories(lib, "10.*"))
        {
            string headers = Path.Combine(root, "Include", Path.GetFileName(version));
            if (File.Exists(Path.Combine(version, @"um\x64\kernel32.lib")) &&
                File.Exists(Path.Combine(version, @"ucrt\x64\ucrt.lib")) &&
                File.Exists(Path.Combine(headers, @"um\Windows.h")) &&
                File.Exists(Path.Combine(headers, @"ucrt\stdio.h"))) return true;
        }
        return false;
    }
}
