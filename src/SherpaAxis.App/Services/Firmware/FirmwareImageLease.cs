using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SherpaAxis.App.Services.Firmware;

public sealed class FirmwareImageLease : IDisposable
{
    private readonly FileInfo file;
    private readonly DirectoryInfo directory;
    private readonly FileSecurity originalFile;
    private readonly DirectorySecurity originalDirectory;
    private readonly FileAttributes originalAttributes;
    private bool disposed;

    private FirmwareImageLease(string path)
    {
        file = new FileInfo(path); directory = file.Directory!;
        originalFile = file.GetAccessControl(AccessControlSections.Access);
        originalDirectory = directory.GetAccessControl(AccessControlSections.Access);
        originalAttributes = file.Attributes;
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Cannot identify the firmware staging-file owner.");
        // ST's CFile reader uses exclusive sharing. Protect via ACL/attributes rather
        // than holding a read-access handle that would prevent ST from opening the image.
        var readOnly = new FileSecurity();
        readOnly.SetAccessRuleProtection(true, false);
        readOnly.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        var protectedDirectory = directory.GetAccessControl(AccessControlSections.Access);
        protectedDirectory.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
        try
        {
            if ((originalAttributes & FileAttributes.ReadOnly) == 0) { file.Attributes = originalAttributes | FileAttributes.ReadOnly; }
            file.SetAccessControl(readOnly);
            // Deny parent DELETE_CHILD as well, so it cannot override the file's ACL.
            directory.SetAccessControl(protectedDirectory);
        }
        catch { Restore(); throw; }
    }
    public static FirmwareImageLease Open(string path) => new(path);
    public void Dispose()
    {
        if (disposed) { return; }
        Restore(); disposed = true;
    }
    private void Restore()
    {
        // Loaded security objects have no dirty sections. Mark Access modified before
        // persisting, otherwise SetAccessControl silently leaves the temporary ACL in place.
        var restoredFile = new FileSecurity();
        restoredFile.SetSecurityDescriptorBinaryForm(originalFile.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        var restoredDirectory = new DirectorySecurity();
        restoredDirectory.SetSecurityDescriptorBinaryForm(originalDirectory.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        try
        {
            file.SetAccessControl(restoredFile); file.Refresh();
            if (file.Attributes != originalAttributes) { file.Attributes = originalAttributes; }
        }
        finally { directory.SetAccessControl(restoredDirectory); }
    }
}
