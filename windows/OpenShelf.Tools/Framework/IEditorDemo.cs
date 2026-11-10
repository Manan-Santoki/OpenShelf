namespace OpenShelf.Tools;

/// <summary>Lets the demo executable (OpenShelf.Tools.Demo) put an editor into a named state for screenshots.</summary>
internal interface IEditorDemo
{
    void ApplyDemoState(string state);
}
