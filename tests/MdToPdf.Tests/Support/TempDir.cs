namespace MdToPdf.Tests.Support;

/// <summary>A fresh temporary directory, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdtopdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string? text = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        if (text is not null)
        {
            System.IO.File.WriteAllText(path, text);
        }

        return path;
    }

    public string File(string name, byte[] data)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering temp dir is harmless.
        }
    }
}
