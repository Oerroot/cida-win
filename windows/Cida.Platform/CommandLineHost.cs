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
                Console.WriteLine($"注册结果: {new SparsePackageRegistrar().EnsureRegistered()}");
                Console.WriteLine($"中文 OCR 可用: {new LocalOcr().IsAvailable()}");
                return 0;
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
