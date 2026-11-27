using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BubuUI.VisualStudio;

internal static class DecompiledDocumentStore
{
    // VS navigates file URIs. Materialize the server's virtual C# documents as read-only files.
    internal static string Materialize(string cacheDirectory, string sourceUri, string content)
    {
        Directory.CreateDirectory(cacheDirectory);
        string key;
        using (var hash = SHA256.Create())
            key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sourceUri + "\n" + content)))
                .Replace("-", "").ToLowerInvariant();
        var path = Path.Combine(cacheDirectory, key + ".cs");
        if (!File.Exists(path)) File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return new Uri(path).AbsoluteUri;
    }
}
