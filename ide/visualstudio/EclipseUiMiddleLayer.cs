using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EclipseUI.VisualStudio;

internal sealed class EclipseUiMiddleLayer : ILanguageClientMiddleLayer2<JToken>
{
    internal JsonRpc? Rpc { get; set; }
    internal string? Workspace { get; set; }
    internal string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EclipseUI", "VisualStudio", "decompiled");

    public bool CanHandle(string methodName) => methodName == "initialize" || methodName == "textDocument/definition";

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
        => sendNotification(methodParam);

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam,
        Func<JToken, Task<JToken?>> sendRequest)
    {
        // VS solution mode does not always supply a workspace root (open-folder mode does).
        if (methodName == "initialize" && methodParam is JObject initialization && Workspace != null)
        {
            var root = new Uri(Workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
            initialization["rootUri"] = root;
            initialization["workspaceFolders"] = new JArray(new JObject { ["uri"] = root, ["name"] = Path.GetFileName(Workspace) });
        }
        var result = await sendRequest(methodParam).ConfigureAwait(false);
        if (methodName == "textDocument/definition" && result != null)
        {
            if (result is JArray locations)
                foreach (var location in locations) await RewriteLocationAsync(location).ConfigureAwait(false);
            else await RewriteLocationAsync(result).ConfigureAwait(false);
        }
        return result;
    }

    private async Task RewriteLocationAsync(JToken location)
    {
        if (!(location is JObject item) || Rpc == null) return;
        var property = item["targetUri"] != null ? "targetUri" : "uri";
        var uri = item[property]?.Value<string>();
        if (uri == null || !uri.StartsWith("eui-decompiled:", StringComparison.OrdinalIgnoreCase)) return;
        var document = await Rpc.InvokeWithParameterObjectAsync<JObject>(
            "eclipseui/decompiledContent", new { uri }).ConfigureAwait(false);
        var content = document?["content"]?.Value<string>();
        if (content != null) item[property] = DecompiledDocumentStore.Materialize(CacheDirectory, uri, content);
    }
}
