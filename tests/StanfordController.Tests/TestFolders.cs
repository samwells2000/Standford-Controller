namespace StanfordController.Tests;

internal static class TestFolders
{
    /// <summary>
    /// Deletes a test's temporary folder. The audit trail is written in the background, so a record may still be
    /// landing in it when the test ends; wait for that rather than failing. Audit trail files are read-only, which
    /// Windows will not delete, so that is cleared first.
    /// </summary>
    public static void Delete(string folder)
    {
        for (var attempt = 1; Directory.Exists(folder); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
