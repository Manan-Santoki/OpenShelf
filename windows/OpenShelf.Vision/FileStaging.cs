using OpenShelf.Convert;
using OpenShelf.Core;

namespace OpenShelf.Vision;

/// <summary>Writes a result next to its original through a hidden temporary file, so a cancelled or failed save leaves nothing behind.</summary>
internal static class FileStaging
{
    /// <summary>Runs <paramref name="write"/> against a temporary file in the destination folder and moves it to a free name
    /// ("Photo OCR.pdf", "Photo OCR 2.pdf"). Returns the final path.</summary>
    public static async Task<string> SaveAsync(string source, string suffix, string extension, Func<string, Task> write, CancellationToken ct)
    {
        var destination = OutputNaming.Destination(source, suffix, extension);
        var folder = Path.GetDirectoryName(destination) ?? ".";
        if (!Directory.Exists(folder))
            throw ConversionException.Failed($"The folder “{Path.GetFileName(folder)}” no longer exists.");
        var staged = Path.Combine(folder, $".openshelf-{Guid.NewGuid():N}.tmp");
        try
        {
            ct.ThrowIfCancellationRequested();
            await write(staged);
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(staged, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Someone took the name meanwhile; pick the next free one.
                destination = OutputNaming.Destination(source, suffix, extension);
                File.Move(staged, destination);
            }
            return destination;
        }
        catch (UnauthorizedAccessException)
        {
            throw ConversionException.Failed($"You don’t have permission to save in “{Path.GetFileName(folder)}”.");
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            throw ConversionException.Failed("There isn’t enough free space on the disk.");
        }
        catch (IOException)
        {
            throw ConversionException.Failed($"“{Path.GetFileName(destination)}” couldn’t be saved.");
        }
        finally
        {
            try { File.Delete(staged); } catch (Exception) { /* best effort */ }
        }
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27;
}
