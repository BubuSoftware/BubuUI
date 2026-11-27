using System.Security.Cryptography;
using System.Text;
using BubuUI.LanguageServer.Workspace;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>File URI fallback for clients without a virtual document content provider.</summary>
public static class DecompiledFileCache
{
    public static string Materialize(string uri, string content)
    {
        // No portion of the URI is used as a filesystem path. Content changes get a
        // new file so an open editor never retains stale metadata or wrong ranges.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri + "\n" + content)));
        var directory = Path.Combine(Path.GetTempPath(), "BubuUI", "decompiled", hash);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Decompiled.cs");
        if (!File.Exists(path) || File.ReadAllText(path) != content)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return DocumentStore.PathToUri(path);
    }
}
