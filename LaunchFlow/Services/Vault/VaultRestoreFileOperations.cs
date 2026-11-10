using System.IO;

namespace LaunchFlow.Services;

/// <summary>
/// Narrow file-operation seam for vault restore. Default delegates to
/// <see cref="File"/>; harnesses may override individual calls to inject
/// failures without inventing an application-wide filesystem abstraction.
/// </summary>
public class VaultRestoreFileOperations
{
    public static VaultRestoreFileOperations Default { get; } = new();

    public virtual bool FileExists(string path) => File.Exists(path);

    public virtual void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);

    public virtual void MoveFile(string source, string destination) =>
        File.Move(source, destination);

    public virtual void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
