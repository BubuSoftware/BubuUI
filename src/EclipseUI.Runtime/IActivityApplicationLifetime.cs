namespace EclipseUI;

/// <summary>OS-driven lifetime: each native activity or scene owns a fresh view from this factory.</summary>
public interface IActivityApplicationLifetime
{
    Func<Element>? MainViewFactory { get; set; }
}
