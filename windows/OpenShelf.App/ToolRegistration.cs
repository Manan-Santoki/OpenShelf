using OpenShelf.Tools;

namespace OpenShelf.App;

/// <summary>Adds the tools that live outside OpenShelf.Tools (PDF, media, OCR, Read QR, Remove BG) to the Tools wheel.</summary>
public static class ToolRegistration
{
    public static void RegisterAll(ToolsCatalog tools)
    {
        OpenShelf.Tools.VisionEditors.VisionTools.Register(tools);
        OpenShelf.Tools.PdfEditors.PdfTools.Register(tools);
        OpenShelf.Tools.MediaEditors.MediaTools.Register(tools);
    }
}
