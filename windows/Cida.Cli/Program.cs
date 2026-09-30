using Cida.Core;
using Cida.Platform;

// Cida's command line entry point: the same executable serves the GUI (when built) and the
// config/check commands, mirroring the upstream "the app binary is the CLI" design.

var arguments = args.Skip(0).ToList();
if (arguments.FirstOrDefault() == "probe")
{
    Cida.Cli.SelectionProbe.Run();
    return;
}
if (arguments.FirstOrDefault() == "probe-identity")
{
    // P0 smoke: does this process get package identity, and can OcrEngine be created?
    Console.WriteLine($"包身份（注册前）: {Cida.Platform.SparsePackageRegistrar.HasPackageIdentity()}");
    var registrar = new Cida.Platform.SparsePackageRegistrar();
    var registered = registrar.EnsureRegistered();
    _ = registered;
    Console.WriteLine($"注册结果: {registered}");
    Console.WriteLine($"包身份（注册后）: {Cida.Platform.SparsePackageRegistrar.HasPackageIdentity()}");
    try
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
            new Windows.Globalization.Language("zh-Hans"));
        Console.WriteLine(engine == null
            ? "OcrEngine: 不可用（zh-Hans 语言包缺失，或身份未生效）"
            : $"OcrEngine: 可用（{engine.RecognizerLanguage.LanguageTag}，最大边 {Windows.Media.Ocr.OcrEngine.MaxImageDimension}px）");
    }
    catch (Exception error)
    {
        Console.WriteLine($"OcrEngine: 抛出异常 {error.GetType().Name}: {error.Message}");
    }
    return;
}
if (CommandLineInterface.Handles(arguments))
{
    var environment = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .GroupBy(entry => (string)entry.Key, entry => (string?)entry.Value)
        .ToDictionary(group => group.Key, group => group.First() ?? "");
    byte[] ReadStandardInput()
    {
        using var memory = new MemoryStream();
        Console.OpenStandardInput().CopyTo(memory);
        return memory.ToArray();
    }
    byte[] ReadFile(string path) => File.ReadAllBytes(path);
    var cli = new CommandLineInterface(
        PlatformConfiguration.Production(),
        environment,
        ReadStandardInput,
        ReadFile,
        text => Console.Out.WriteLine(text),
        text => Console.Error.WriteLine(text),
        settings => ModelServiceCheck.RunAsync(settings));
    var code = await cli.RunAsync(arguments);
    Environment.Exit(code);
}

Console.Error.WriteLine("这是辞达的命令行。用 Cida --help 查看命令；启动应用请运行 Cida.Desktop。");
Environment.ExitCode = 64;
