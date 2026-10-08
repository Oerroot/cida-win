using Cida.Core;

namespace Cida.Platform;

/// <summary>The shared CLI host, used by both the GUI binary and the console binary.</summary>
public static class CommandLineHost
{
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        try
        {
            if (arguments.FirstOrDefault() == "probe") { SelectionProbe.Run(); return 0; }
            if (arguments.FirstOrDefault() == "probe-identity")
            {
                Console.WriteLine($"包身份: {SparsePackageRegistrar.HasPackageIdentity()}");
                Console.WriteLine(LocalOcr.CapabilitySummary());
                return 0;
            }
            if (arguments.FirstOrDefault() == "probe-ocr")
            {
                if (arguments.Count < 2 || !File.Exists(arguments[^1])) { Console.Error.WriteLine("Usage: Cida.Cli.exe probe-ocr [--offline] <image.png>"); return 2; }
                var bytes = File.ReadAllBytes(arguments[^1]);
                using var bitmap = new System.Drawing.Bitmap(new MemoryStream(bytes));
                var lines = await new LocalOcr().RecognizeAsync(bytes, bitmap.Width, bitmap.Height, forceFallback: arguments.Contains("--offline"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { count = lines?.Count ?? 0, text = string.Join("\n\n", RecognizedLayout.Paragraphs(lines ?? []).Select(p => p.Text)), identity = SparsePackageRegistrar.HasPackageIdentity() }));
                return lines is { Count: > 0 } ? 0 : 1;
            }
            var environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "");
            byte[] ReadStandardInput()
            {
                using var memory = new MemoryStream();
                Console.OpenStandardInput().CopyTo(memory);
                return memory.ToArray();
            }
            var cli = new CommandLineInterface(
                PlatformConfiguration.Production(executablePath: Path.Combine(AppContext.BaseDirectory, "Cida.exe")),
                environment, ReadStandardInput, File.ReadAllBytes,
                Console.Out.WriteLine, Console.Error.WriteLine,
                settings => ModelServiceCheck.RunAsync(settings));
            return await cli.RunAsync(arguments);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Console.Error.WriteLine($"配置存储失败：{error.Message}");
            return (int)CommandLineInterface.ExitCode.StorageFailed;
        }
    }
}
