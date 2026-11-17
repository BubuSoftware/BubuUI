using EclipseUI.Rendering;
using VelloSharp;

namespace EclipseUI.Rendering.Vello;

/// <summary>Caches native scene fragments while preserving shared clip, opacity and blend groups.</summary>
public sealed class VelloRetainedSceneBuilder : IDisposable
{
    private readonly VelloSceneBuilder _builder = new();
    private Dictionary<Key, Scene> _fragments = new();
    public int ReusedFragments { get; private set; }
    public int BuiltFragments { get; private set; }

    public void Build(Scene scene, DisplayList list, Size targetSize)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(list);
        ReusedFragments = BuiltFragments = 0;
        // Transform scopes are independent of layer scopes in DisplayList. Splitting a
        // layer that borrows a surrounding scope would change its state for later draws.
        if (!HasNestedScopes(list))
        {
            _builder.Build(scene, list, targetSize);
            foreach (var fragment in _fragments.Values) fragment.Dispose();
            _fragments.Clear();
            BuiltFragments = 1;
            return;
        }
        var current = new Dictionary<Key, Scene>();
        var transforms = new Stack<Transform>();
        var transform = Transform.Identity;
        var index = 0;

        Scene GetFragment(Key key, Action<Scene> build)
        {
            if (current.TryGetValue(key, out var fragment)) { ReusedFragments++; return fragment; }
            if (_fragments.TryGetValue(key, out fragment)) ReusedFragments++;
            else
            {
                fragment = new Scene();
                try { build(fragment); BuiltFragments++; }
                catch { fragment.Dispose(); throw; }
            }
            current.Add(key, fragment);
            return fragment;
        }

        List<Scene> ReadChildren(bool grouped)
        {
            var children = new List<Scene>();
            while (index < list.Commands.Count)
            {
                var start = index;
                var command = list.Commands[index++];
                if (command is PushTransform push) { transforms.Push(transform); transform = push.Transform * transform; continue; }
                if (command is PopTransform) { transform = transforms.Pop(); continue; }
                if (command is PopClip or PopLayer)
                {
                    if (!grouped) throw new InvalidOperationException("Unbalanced native scene group.");
                    return children;
                }
                var initialTransform = transform;
                if (command is PushLayer or PushClip or PushClipPath)
                {
                    var nested = ReadChildren(true);
                    var commands = new DrawCommand[index - start + 1];
                    commands[0] = new PushTransform(initialTransform);
                    list.Commands.CopyTo(start, commands, 1, index - start);
                    var key = new Key(targetSize, commands);
                    children.Add(GetFragment(key, fragment =>
                    {
                        // A shared clip must composite its children once; distributing
                        // an antialiased clip over individual draws changes edge pixels.
                        var begin = new DisplayList(targetSize).Add(new PushTransform(initialTransform)).Add(command);
                        _builder.Build(fragment, begin, targetSize);
                        foreach (var child in nested) fragment.Append(child);
                        fragment.PopLayer();
                    }));
                }
                else
                {
                    var part = new DisplayList(targetSize).Add(new PushTransform(initialTransform)).Add(command).Add(new PopTransform());
                    var key = new Key(targetSize, part.Commands.ToArray());
                    children.Add(GetFragment(key, fragment => _builder.Build(fragment, part, targetSize)));
                }
            }
            if (grouped) throw new InvalidOperationException("Unbalanced native scene group.");
            return children;
        }

        try
        {
            foreach (var fragment in ReadChildren(false)) scene.Append(fragment);
        }
        catch
        {
            foreach (var fragment in current.Values)
                if (!_fragments.ContainsValue(fragment)) fragment.Dispose();
            throw;
        }
        foreach (var pair in _fragments)
            if (!current.ContainsKey(pair.Key)) pair.Value.Dispose();
        _fragments = current;
    }

    private static bool HasNestedScopes(DisplayList list)
    {
        var scopes = new Stack<DrawCommand>();
        foreach (var command in list.Commands)
        {
            if (command is PushTransform or PushClip or PushClipPath or PushLayer) scopes.Push(command);
            else if (command is PopTransform or PopClip or PopLayer)
            {
                if (!scopes.TryPop(out var open) || !(command switch
                {
                    PopTransform => open is PushTransform,
                    PopClip => open is PushClip or PushClipPath,
                    PopLayer => open is PushLayer,
                    _ => false
                })) return false;
            }
        }
        return scopes.Count == 0;
    }

    public void Dispose()
    {
        foreach (var fragment in _fragments.Values) fragment.Dispose();
        _fragments.Clear();
        _builder.Dispose();
    }

    private sealed class Key(Size size, DrawCommand[] commands) : IEquatable<Key>
    {
        private readonly int _hash = Hash(size, commands);
        public bool Equals(Key? other) => other is not null && size == other.Size && commands.AsSpan().SequenceEqual(other.Commands);
        private Size Size => size;
        private DrawCommand[] Commands => commands;
        public override bool Equals(object? obj) => obj is Key key && Equals(key);
        public override int GetHashCode() => _hash;
        private static int Hash(Size size, DrawCommand[] commands)
        {
            var hash = new HashCode(); hash.Add(size);
            foreach (var command in commands) hash.Add(command);
            return hash.ToHashCode();
        }
    }
}
