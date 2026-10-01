namespace SherpaAxis.Profiles;

public static class ProfileStore
{
    public static async Task<PedalProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ProfileJson.MaximumFileBytes)
        {
            throw new InvalidDataException("Profile exceeds the one MiB size limit.");
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Profile changed while being read.");
        }

        return ProfileJson.Deserialize(bytes);
    }

    public static async Task SaveAsync(string path, PedalProfile profile, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ProfileJson.Serialize(profile);
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".sherpaaxis-profile-{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Keep the original save error if cleanup also fails; the destination was not replaced.
                }
            }
        }
    }
}
