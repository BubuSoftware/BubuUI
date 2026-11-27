using System;
using System.Collections.Generic;
using BubuUI.Elements;

namespace BubuUI.Headless.Tests;

internal static class ConstructionFailureState
{
    public static readonly List<UiObject> Created = new();
    public static readonly List<string> Released = new();

    public static void Reset()
    {
        Created.Clear();
        Released.Clear();
    }
}

internal sealed class ConstructionResource(string name) : IDisposable
{
    public void Dispose() => ConstructionFailureState.Released.Add(name);
}

public class ConstructionFirstChild : StackPanel
{
    public ConstructionFirstChild()
    {
        ConstructionFailureState.Created.Add(this);
        Track(new ConstructionResource("first"));
    }
}

public class ConstructionThrowingChild : StackPanel
{
    public ConstructionThrowingChild()
    {
        ConstructionFailureState.Created.Add(this);
        Track(new ConstructionResource("throwing"));
    }

    public bool Fail
    {
        get => false;
        set
        {
            if (value) throw new InvalidOperationException("fixture-parameter-failure");
        }
    }
}
