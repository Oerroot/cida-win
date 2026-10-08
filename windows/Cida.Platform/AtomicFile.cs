namespace Cida.Platform;

internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes, bool backup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (backup && File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WriteText(string path, string text, bool backup = false) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(text), backup);
}
