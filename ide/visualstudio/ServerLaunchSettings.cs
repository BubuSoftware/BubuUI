using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace EclipseUI.VisualStudio;

internal static class ServerLaunchSettings
{
    internal static readonly string[] WatchPatterns =
    {
        "**/*.cs", "**/*.ui", "**/*.eustyle", "**/*.csproj",
        "**/Directory.Build.props", "**/obj/project.assets.json",
    };

    internal static string? FindWorkspace(string? solution, string? document)
    {
        if (!string.IsNullOrWhiteSpace(solution))
            return Directory.Exists(solution) ? Path.GetFullPath(solution!) : Path.GetDirectoryName(solution);
        if (string.IsNullOrWhiteSpace(document) || !Path.IsPathRooted(document)) return null;
        var folder = Path.GetDirectoryName(document);
        foreach (var ancestor in Ancestors(folder))
        {
            if (!Directory.Exists(ancestor)) continue;
            if (Directory.Exists(Path.Combine(ancestor, ".git")) || File.Exists(Path.Combine(ancestor, ".git"))
                || Directory.EnumerateFiles(ancestor, "*.sln*").Any()) return ancestor;
        }
        return folder;
    }

    internal static string ResolveServer(string extensionDirectory, string? workspace,
        string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath!.Trim()));
            if (!File.Exists(configured))
                throw new FileNotFoundException("EclipseUI: ECLIPSEUI_SERVER_DLL does not exist.", configured);
            return configured;
        }

        var bundled = Path.Combine(extensionDirectory, "server", "EclipseUI.LanguageServer.dll");
        if (File.Exists(bundled)) return bundled;
        foreach (var root in Ancestors(workspace).Concat(Ancestors(extensionDirectory)))
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(root, "ide", "EclipseUI.LanguageServer", "bin",
                configuration, "net11.0", "EclipseUI.LanguageServer.dll");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("EclipseUI language server not found. Reinstall the VSIX, " +
            "run ide/visualstudio/build.ps1, or set ECLIPSEUI_SERVER_DLL.");
    }

    internal static ProcessStartInfo CreateStartInfo(string serverPath, string? workspace,
        string logPath, string? dotnet, string? runtime, string? assemblies)
    {
        var args = new List<string> { serverPath, "--stdio", "--log", logPath, "--log-stderr" };
        if (!string.IsNullOrWhiteSpace(workspace)) args.AddRange(new[] { "--workspace", workspace! });
        if (!string.IsNullOrWhiteSpace(runtime)) args.AddRange(new[] { "--runtime", runtime!.Trim() });
        foreach (var assembly in (assemblies ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(assembly)) args.AddRange(new[] { "--assembly", assembly.Trim() });
        return new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet!.Trim(),
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            WorkingDirectory = workspace ?? Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    // Windows CommandLineToArgvW rules: double backslashes before quotes and the closing quote.
    internal static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') result.Append('\\', backslashes * 2 + 1);
            else result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static IEnumerable<string> Ancestors(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            yield return directory.FullName;
    }
}
