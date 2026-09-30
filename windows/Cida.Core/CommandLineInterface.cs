namespace Cida.Core;

/// <summary>
/// Cida's command line: an AI assistant reads the schema, writes the configuration, stores
/// the key from stdin, a file or an environment variable, and checks the service. Output for
/// people is Chinese; <c>--json</c> gives the same content in stable English field names.
/// Ported from upstream CommandLineInterface.swift.
/// </summary>
public sealed class CommandLineInterface(
    ConfigurationStore store,
    IReadOnlyDictionary<string, string> environment,
    Func<byte[]> readStandardInput,
    Func<string, byte[]> readFile,
    Action<string> output,
    Action<string> errorOutput,
    CheckRunner check)
{
    public enum ExitCode
    {
        Success = 0,
        /// <summary>A command or value that is not valid; nothing was changed.</summary>
        Invalid = 64,
        /// <summary>The check's request failed.</summary>
        CheckFailed = 69,
        /// <summary>The secret store or the login item refused a change.</summary>
        StorageFailed = 74,
    }

    /// <summary>Whether a launch with these arguments is a command rather than the application.</summary>
    public static bool Handles(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return false;
        var first = arguments[0];
        return first is "config" or "check" or "help" or "--help" or "-h";
    }

    public async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        var json = arguments.Contains("--json");
        var words = arguments.Where(argument => argument != "--json").ToList();
        return (int)await RunCommandAsync(words, json);
    }

    private async Task<ExitCode> RunCommandAsync(IReadOnlyList<string> words, bool json)
    {
        var first = words.Count > 0 ? words[0] : null;
        var second = words.Count > 1 ? words[1] : null;
        switch (first)
        {
            case "help" or "--help" or "-h":
                return Help(json);
            case "config" when second == "schema":
                return Schema(json);
            case "config" when second == "show":
                return Show(json);
            case "config" when second == "set":
                return Set([.. words.Skip(2)], json);
            case "config" when second == "unset":
                return Unset([.. words.Skip(2)], json);
            case "config" when second == "reset":
                return Reset(json);
            case "check":
            {
                var options = words.Skip(1).ToList();
                if (!options.All(option => option == "--verbose"))
                {
                    return UsageError("check 只接受 --verbose 与 --json", json);
                }
                return await Check(options.Contains("--verbose"), json);
            }
            default:
                return UsageError($"不认识的命令：{string.Join(" ", words)}", json);
        }
    }

    // MARK: help

    private ExitCode Help(bool json)
    {
        var commands = new (string Usage, string Summary)[]
        {
            ("config schema", "全部字段的名称、类型、取值、默认值、示例与说明"),
            ("config show", "当前配置；API Key 只显示是否已保存"),
            ("config set 字段=值 …", "一次写入多项，全部合法才生效"),
            ("config set 字段 --stdin | --file 路径 | --env 名称", "从标准输入、文件或环境变量读取值；api-key 只能这样写入"),
            ("config unset 字段 …", "恢复默认"),
            ("config reset", "全部恢复默认，并删除密钥库里的 API Key"),
            ("check [--verbose]", "用当前配置真的请求一次；--verbose 在失败时列出实际请求与服务商的原文"),
        };
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["commands"] = Core.JsonValue.Array(commands.Select(command =>
                    Core.JsonValue.Object(new JsonObject
                    {
                        ["usage"] = Core.JsonValue.String(command.Usage),
                        ["summary"] = Core.JsonValue.String(command.Summary),
                    })).ToList()),
                ["exitCodes"] = Core.JsonValue.Object(new JsonObject
                {
                    ["success"] = Core.JsonValue.Integer(0),
                    ["invalid"] = Core.JsonValue.Integer(64),
                    ["checkFailed"] = Core.JsonValue.Integer(69),
                    ["storageFailed"] = Core.JsonValue.Integer(74),
                }),
            }));
        }
        else
        {
            var lines = new List<string> { "辞达的命令行：配置辞达使用的模型服务，并检查它是否可用。", "", "用法：" };
            foreach (var command in commands)
            {
                lines.Add($"  Cida {command.Usage}");
                lines.Add($"      {command.Summary}");
            }
            lines.Add("");
            lines.Add("每个命令都可以加 --json，输出稳定的 JSON。");
            lines.Add("退出码：成功 0；配置不合法 64；检查失败 69；密钥库或开机启动写入失败 74。");
            output(string.Join("\n", lines));
        }
        return ExitCode.Success;
    }

    // MARK: config schema

    private ExitCode Schema(bool json)
    {
        if (json)
        {
            var fields = ConfigurationFieldExtensions.AllCases.Select(field =>
            {
                var schema = field.GetSchema();
                var value = new JsonObject
                {
                    ["name"] = Core.JsonValue.String(field.RawValue()),
                    ["type"] = Core.JsonValue.String(schema.Type),
                };
                if (schema.Values != null)
                {
                    value["values"] = Core.JsonValue.Array(schema.Values.Select(Core.JsonValue.String).ToList());
                }
                value["default"] = Core.JsonValue.String(schema.DefaultValue);
                value["example"] = Core.JsonValue.String(schema.Example);
                value["description"] = Core.JsonValue.String(schema.Description);
                value["secret"] = Core.JsonValue.Bool(field.IsSecret());
                value["sources"] = Core.JsonValue.Array(
                    (field.IsSecret() ? new[] { "stdin", "file", "env" } : new[] { "value", "stdin", "file", "env" })
                    .Select(Core.JsonValue.String).ToList());
                return Core.JsonValue.Object(value);
            }).ToList();
            Emit(Core.JsonValue.Object(new JsonObject { ["fields"] = Core.JsonValue.Array(fields) }));
            return ExitCode.Success;
        }
        var width = ConfigurationFieldExtensions.AllCases.Max(field => field.RawValue().Length) + 3;
        var blocks = new List<string>();
        foreach (var field in ConfigurationFieldExtensions.AllCases)
        {
            var schema = field.GetSchema();
            var indent = new string(' ', width);
            var lines = new List<string>
            {
                field.RawValue().Padded(width)
                    + (schema.Values != null ? string.Join(" | ", schema.Values) : schema.Type),
                indent + schema.Description,
            };
            var defaultValue = schema.DefaultValue.Length > 60
                ? schema.DefaultValue[..60] + "…"
                : schema.DefaultValue;
            lines.Add(indent + $"默认：{defaultValue}");
            lines.Add(indent + $"例：{schema.Example}");
            blocks.Add(string.Join("\n", lines));
        }
        output(
            string.Join("\n\n", blocks)
            + "\n\n写入：Cida config set 字段=值 …；文本也可以用 字段 --stdin、--file 路径 或 --env 名称。");
        return ExitCode.Success;
    }

    // MARK: config show

    private ExitCode Show(bool json)
    {
        var configuration = LoadConfiguration();
        var hasApiKey = store.HasApiKey();
        var service = configuration.Settings.ModelService;
        if (json)
        {
            var fields = new JsonObject();
            var defaults = new List<Core.JsonValue>();
            foreach (var field in ConfigurationFieldExtensions.AllCases)
            {
                fields[field.RawValue()] = field.FieldJson(configuration, hasApiKey);
                if (field.IsDefault(configuration, hasApiKey))
                {
                    defaults.Add(Core.JsonValue.String(field.RawValue()));
                }
            }
            var value = new JsonObject
            {
                ["complete"] = Core.JsonValue.Bool(service.IsComplete(hasApiKey)),
                ["missing"] = Core.JsonValue.Array(
                    service.MissingFields(hasApiKey).Select(Core.JsonValue.String).ToList()),
                ["fields"] = Core.JsonValue.Object(fields),
                ["defaults"] = Core.JsonValue.Array(defaults),
            };
            value["lastCheck"] = LastCheckJson(configuration.Settings);
            Emit(Core.JsonValue.Object(value));
            return ExitCode.Success;
        }
        // The model service always; everything else only once it differs from the default.
        var shown = ConfigurationFieldExtensions.AllCases.Where(field =>
            ConfigurationFieldExtensions.ModelServiceFields.Contains(field)
            || !field.IsDefault(configuration, hasApiKey)).ToList();
        var nameWidth = shown.Max(field => field.RawValue().Length) + 3;
        output(string.Join("\n",
            shown.Select(field =>
                field.RawValue().Padded(nameWidth) + field.DisplayValue(configuration, hasApiKey))));
        return ExitCode.Success;
    }

    private Core.JsonValue LastCheckJson(CidaSettings settings)
    {
        var record = store.LoadLastCheck();
        if (record == null) return Core.JsonValue.Null;
        var apiKeySettings = settings with { ApiKey = store.ReadApiKey() ?? "" };
        return Core.JsonValue.Object(new JsonObject
        {
            ["passed"] = Core.JsonValue.Bool(record.Passed),
            ["status"] = record.StatusCode is int status ? Core.JsonValue.Integer(status) : Core.JsonValue.Null,
            ["reason"] = record.Reason == null ? Core.JsonValue.Null : Core.JsonValue.String(record.Reason),
            ["checkedAt"] = Core.JsonValue.String(record.CheckedAt.ToString("O")),
            ["current"] = Core.JsonValue.Bool(record.Fingerprint == apiKeySettings.ModelServiceFingerprint),
        });
    }

    // MARK: config set

    private enum SourceKind { Inline, StandardInput, File, Environment }

    private sealed record Source(SourceKind Kind, string? Argument = null)
    {
        public string Description() => Kind switch
        {
            SourceKind.Inline => "命令",
            SourceKind.StandardInput => "标准输入",
            SourceKind.File => $"文件 {Argument}",
            SourceKind.Environment => $"环境变量 {Argument}",
            _ => "",
        };
    }

    private ExitCode Set(IReadOnlyList<string> words, bool json)
    {
        var assignments = new List<(ConfigurationField Field, Source Source)>();
        var index = 0;
        while (index < words.Count)
        {
            var word = words[index];
            var equals = word.IndexOf('=');
            if (equals >= 0)
            {
                var name = word[..equals];
                var field = ParseField(name);
                if (field == null) return UnknownField(name, json);
                if (field.Value.IsSecret()) return RefusePlainSecret(field.Value, json);
                assignments.Add((field.Value, new Source(SourceKind.Inline, word[(equals + 1)..])));
                index++;
                continue;
            }
            var bareField = ParseField(word);
            if (bareField == null) return UnknownField(word, json);
            var option = index + 1 < words.Count ? words[index + 1] : null;
            var argument = index + 2 < words.Count ? words[index + 2] : null;
            switch (option)
            {
                case "--stdin":
                    assignments.Add((bareField.Value, new Source(SourceKind.StandardInput)));
                    index += 2;
                    break;
                case "--file" when argument != null:
                    assignments.Add((bareField.Value, new Source(SourceKind.File, argument)));
                    index += 3;
                    break;
                case "--env" when argument != null:
                    assignments.Add((bareField.Value, new Source(SourceKind.Environment, argument)));
                    index += 3;
                    break;
                default:
                    return Invalid(
                    [
                        new InvalidValue(bareField.Value.RawValue(),
                            $"{bareField.Value.RawValue()} 后面要跟 =值，或者 --stdin、--file 路径、--env 名称"),
                    ], json);
            }
        }
        if (assignments.Count == 0)
        {
            return UsageError("config set 需要至少一项，例如 Cida config set model=deepseek-chat", json);
        }
        var names = assignments.Select(assignment => assignment.Field.RawValue()).ToList();
        var duplicate = names.FirstOrDefault(name => names.Count(existing => existing == name) > 1);
        if (duplicate != null)
        {
            return UsageError($"{duplicate} 在同一条命令里出现了不止一次", json);
        }
        if (assignments.Count(assignment => assignment.Source.Kind == SourceKind.StandardInput) > 1)
        {
            return UsageError("--stdin 在一条命令里只能用一次", json);
        }

        var configuration = LoadConfiguration();
        var original = configuration;
        string? apiKey = null;
        var errors = new List<InvalidValue>();
        foreach (var (field, source) in assignments)
        {
            string text;
            try
            {
                text = ReadSource(source);
            }
            catch (SourceReadException error)
            {
                errors.Add(error.Error);
                continue;
            }
            if (field == ConfigurationField.ApiKey)
            {
                var key = text.Trim();
                if (key.Length == 0)
                {
                    errors.Add(new InvalidValue("api-key", $"没有从{source.Description()}读到 API Key"));
                }
                else
                {
                    apiKey = key;
                }
                continue;
            }
            try
            {
                configuration = field.Apply(text, configuration);
            }
            catch (InvalidConfiguration error)
            {
                errors.Add(error.ToInvalidValue());
            }
        }
        if (errors.Count == 0)
        {
            try
            {
                ConfigurationFieldExtensions.Validate(configuration);
            }
            catch (InvalidConfiguration error)
            {
                errors.Add(error.ToInvalidValue());
            }
        }
        if (errors.Count > 0) return Invalid(errors, json);

        if (apiKey != null)
        {
            try
            {
                store.SaveApiKey(apiKey);
            }
            catch (Exception error)
            {
                return StorageFailed($"无法保存 API Key：{error.Message}", json);
            }
        }
        var persistFailure = Persist(configuration, original);
        if (persistFailure != null) return persistFailure.Value;

        var fieldNames = assignments.Select(assignment => assignment.Field.RawValue())
            .Where(name => name != "api-key").ToList();
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(true),
                ["updated"] = Core.JsonValue.Array(fieldNames.Select(Core.JsonValue.String).ToList()),
                ["apiKeyStored"] = Core.JsonValue.Bool(apiKey != null),
            }));
        }
        else
        {
            var lines = new List<string>();
            if (fieldNames.Count > 0)
            {
                lines.Add($"已更新 {fieldNames.Count} 项：{string.Join("、", fieldNames)}");
            }
            if (apiKey != null) lines.Add("已把 API Key 存进系统密钥库");
            output(string.Join("\n", lines));
        }
        return ExitCode.Success;
    }

    private string ReadSource(Source source)
    {
        switch (source.Kind)
        {
            case SourceKind.Inline:
                return source.Argument ?? "";
            case SourceKind.StandardInput:
                return System.Text.Encoding.UTF8.GetString(readStandardInput());
            case SourceKind.File:
                try
                {
                    return System.Text.Encoding.UTF8.GetString(readFile(source.Argument!));
                }
                catch (Exception error)
                {
                    throw new SourceReadException(new InvalidValue("",
                        $"读不到文件 {source.Argument}：{error.Message}"));
                }
            case SourceKind.Environment:
            {
                if (!environment.TryGetValue(source.Argument ?? "", out var value) || value.Length == 0)
                {
                    throw new SourceReadException(new InvalidValue("", $"环境变量 {source.Argument} 不存在或为空"));
                }
                return value;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private sealed class SourceReadException(InvalidValue error) : Exception(error.Message)
    {
        public InvalidValue Error { get; } = error;
    }

    // MARK: config unset / reset

    private ExitCode Unset(IReadOnlyList<string> words, bool json)
    {
        if (words.Count == 0)
        {
            return UsageError("config unset 需要至少一个字段，例如 Cida config unset body", json);
        }
        var fields = new List<ConfigurationField>();
        foreach (var word in words)
        {
            var field = ParseField(word);
            if (field == null) return UnknownField(word, json);
            if (!fields.Contains(field.Value)) fields.Add(field.Value);
        }
        var configuration = LoadConfiguration();
        var original = configuration;
        foreach (var field in fields)
        {
            configuration = field.Reset(configuration);
        }
        try
        {
            ConfigurationFieldExtensions.Validate(configuration);
        }
        catch (InvalidConfiguration error)
        {
            return Invalid([error.ToInvalidValue()], json);
        }
        if (fields.Contains(ConfigurationField.ApiKey)) store.ClearApiKey();
        var persistFailure = Persist(configuration, original);
        if (persistFailure != null) return persistFailure.Value;

        var names = fields.Select(field => field.RawValue()).ToList();
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(true),
                ["reset"] = Core.JsonValue.Array(names.Select(Core.JsonValue.String).ToList()),
            }));
        }
        else
        {
            output($"已恢复默认 {names.Count} 项：{string.Join("、", names)}");
        }
        return ExitCode.Success;
    }

    private ExitCode Reset(bool json)
    {
        var original = LoadConfiguration();
        var configuration = original;
        foreach (var field in ConfigurationFieldExtensions.AllCases)
        {
            configuration = field.Reset(configuration);
        }
        var hadApiKey = store.HasApiKey();
        store.ClearApiKey();
        store.SaveLastCheck(null);
        var persistFailure = Persist(configuration, original);
        if (persistFailure != null) return persistFailure.Value;
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(true),
                ["apiKeyDeleted"] = Core.JsonValue.Bool(hadApiKey),
            }));
        }
        else
        {
            output(hadApiKey ? "已恢复全部默认，并删除了密钥库里的 API Key" : "已恢复全部默认");
        }
        return ExitCode.Success;
    }

    /// <summary>Writes a validated configuration and tells a running Cida.</summary>
    private ExitCode? Persist(EditableConfiguration configuration, EditableConfiguration original)
    {
        if (configuration.LaunchAtLogin != original.LaunchAtLogin)
        {
            try
            {
                store.SetLaunchAtLogin(configuration.LaunchAtLogin);
            }
            catch (Exception error)
            {
                return StorageFailed($"无法更新开机启动：{error.Message}", false);
            }
        }
        store.SaveSettings(configuration.Settings);
        store.NotifyChange();
        return null;
    }

    private EditableConfiguration LoadConfiguration()
    {
        var settings = store.LoadSettings();
        var launchAtLogin = store.LaunchAtLogin();
        return new EditableConfiguration
        {
            Settings = settings with { LaunchAtLogin = launchAtLogin },
            LaunchAtLogin = launchAtLogin,
        };
    }

    // MARK: check

    private async Task<ExitCode> Check(bool verbose, bool json)
    {
        var settings = store.LoadSettings() with { ApiKey = store.ReadApiKey() ?? "" };
        var missing = settings.ModelService.MissingFields(settings.ApiKey.Length > 0);
        if (missing.Count > 0)
        {
            var message = $"配置还不完整，缺少 {string.Join("、", missing)}；用 Cida config schema 查看字段";
            if (json)
            {
                Emit(Core.JsonValue.Object(new JsonObject
                {
                    ["ok"] = Core.JsonValue.Bool(false),
                    ["missing"] = Core.JsonValue.Array(missing.Select(Core.JsonValue.String).ToList()),
                    ["message"] = Core.JsonValue.String(message),
                }));
            }
            else
            {
                errorOutput($"✗ {message}");
            }
            return ExitCode.Invalid;
        }

        var result = await check(settings);
        store.SaveLastCheck(result.Record);
        store.NotifyChange();

        if (json)
        {
            Emit(CheckJson(result, verbose));
        }
        else if (result.Passed)
        {
            output(
                $"✓ 可用 · {result.Model} · {result.Duration.TotalSeconds:F1} 秒 · 回复「{OneLine(result.Reply, 40)}」");
        }
        else
        {
            output(CheckFailureText(result, verbose));
        }
        return result.Passed ? ExitCode.Success : ExitCode.CheckFailed;
    }

    private string CheckFailureText(ModelServiceCheckResult result, bool verbose)
    {
        var failure = result.Failure ?? new ModelServiceErrorInfo(ModelServiceError.EmptyResult);
        if (!verbose)
        {
            var status = failure.StatusCode is int code ? $"HTTP {code} · " : "";
            return $"""
                ✗ 检查失败 · {status}{failure.Reason()}
                用 Cida check --verbose 查看实际请求与服务商返回的原文
                """;
        }
        var lines = new List<string>
        {
            "✗ 检查失败 · " + (failure.StatusCode is int failureStatus ? $"HTTP {failureStatus}" : failure.Reason()),
        };
        if (result.Request is { } request)
        {
            lines.Add($"{request.Method} {request.Url}");
            lines.AddRange(request.DisplayHeaders.Select(header => $"{header.Name}: {header.Value}"));
            lines.Add(ElidedBody(request.Body));
        }
        if (failure.Kind == ModelServiceError.UnexpectedResponse && failure.Message is { } detail)
        {
            lines.Add($"说明：{detail}");
        }
        if (result.ResponseStatus != null)
        {
            var body = result.ResponseBody.Trim();
            lines.Add($"服务商返回：{(body.Length == 0 ? "（空）" : body)}");
        }
        else
        {
            lines.Add($"服务商返回：（没有收到响应：{failure.ProviderText() ?? failure.Reason()}）");
        }
        return string.Join("\n", lines);
    }

    private Core.JsonValue CheckJson(ModelServiceCheckResult result, bool verbose)
    {
        var value = new JsonObject
        {
            ["ok"] = Core.JsonValue.Bool(result.Passed),
            ["model"] = Core.JsonValue.String(result.Model),
            ["durationSeconds"] = Core.JsonValue.Number(Math.Round(result.Duration.TotalSeconds, 1)),
            ["reply"] = result.Passed ? Core.JsonValue.String(result.Reply) : Core.JsonValue.Null,
            ["status"] = result.Failure?.StatusCode is int status ? Core.JsonValue.Integer(status) : Core.JsonValue.Null,
            ["reason"] = result.Failure == null ? Core.JsonValue.Null : Core.JsonValue.String(result.Failure.Reason()),
        };
        if (verbose)
        {
            if (result.Request is { } request)
            {
                var headers = new JsonObject();
                foreach (var header in request.DisplayHeaders)
                {
                    headers[header.Name] = Core.JsonValue.String(header.Value);
                }
                value["request"] = Core.JsonValue.Object(new JsonObject
                {
                    ["method"] = Core.JsonValue.String(request.Method),
                    ["url"] = Core.JsonValue.String(request.Url.ToString()),
                    ["headers"] = Core.JsonValue.Object(headers),
                    ["body"] = request.Body,
                });
            }
            value["responseStatus"] = result.ResponseStatus is int responseStatus
                ? Core.JsonValue.Integer(responseStatus)
                : Core.JsonValue.Null;
            value["response"] = Core.JsonValue.String(result.ResponseBody);
        }
        return Core.JsonValue.Object(value);
    }

    private static string OneLine(string text, int limit)
    {
        var line = string.Join(" ", text.Split('\n'));
        return line.Length > limit ? line[..limit] + "…" : line;
    }

    /// <summary>
    /// The body on one line with the prompt and the source elided, as the board shows it.
    /// </summary>
    public static string ElidedBody(Core.JsonValue body)
    {
        if (body.ObjectValue is not { } value) return body.DisplayText;
        const string arrayMarker = "__CIDA_ELIDED_ARRAY__";
        const string textMarker = "__CIDA_ELIDED_TEXT__";
        foreach (var key in new[] { "system", "instructions", "messages", "input" })
        {
            switch (value[key]?.TokenType)
            {
                case Core.JsonValue.Kind.Array:
                    value[key] = Core.JsonValue.String(arrayMarker);
                    break;
                case Core.JsonValue.Kind.String:
                    value[key] = Core.JsonValue.String(textMarker);
                    break;
            }
        }
        return Core.JsonValue.Object(value).DisplayText
            .Replace($"\"{arrayMarker}\"", "[ … ]")
            .Replace($"\"{textMarker}\"", "\"…\"");
    }

    // MARK: Output

    private void Emit(Core.JsonValue value) => output(value.DisplayText);

    private ExitCode RefusePlainSecret(ConfigurationField field, bool json)
    {
        var message = $"{field.RawValue()} 不接受写在命令里的值，改用 --stdin、--file 或 --env";
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(false),
                ["errors"] = Core.JsonValue.Array(
                [
                    Core.JsonValue.Object(new JsonObject
                    {
                        ["field"] = Core.JsonValue.String(field.RawValue()),
                        ["message"] = Core.JsonValue.String(message),
                        ["example"] = Core.JsonValue.String("type 密钥.txt | Cida config set api-key --stdin"),
                    }),
                ]),
            }));
        }
        else
        {
            errorOutput($"""
                ✗ {message}，例如：
                  type 密钥.txt | Cida config set api-key --stdin
                """);
        }
        return ExitCode.Invalid;
    }

    private ExitCode UnknownField(string name, bool json)
    {
        return Invalid(
            [new InvalidValue(name, $"不认识的字段「{name}」；用 Cida config schema 查看全部字段")], json);
    }

    private ExitCode Invalid(IReadOnlyList<InvalidValue> errors, bool json)
    {
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(false),
                ["errors"] = Core.JsonValue.Array(errors.Select(error =>
                    Core.JsonValue.Object(new JsonObject
                    {
                        ["field"] = Core.JsonValue.String(error.Field),
                        ["message"] = Core.JsonValue.String(error.Message),
                    })).ToList()),
            }));
        }
        else
        {
            errorOutput(string.Join("\n", errors.Select(error => $"✗ {error.Message}")) + "\n没有改动任何配置");
        }
        return ExitCode.Invalid;
    }

    private ExitCode UsageError(string message, bool json)
    {
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(false),
                ["errors"] = Core.JsonValue.Array(
                [
                    Core.JsonValue.Object(new JsonObject { ["message"] = Core.JsonValue.String(message) }),
                ]),
            }));
        }
        else
        {
            errorOutput($"✗ {message}\n用 Cida --help 查看用法");
        }
        return ExitCode.Invalid;
    }

    private ExitCode StorageFailed(string message, bool json)
    {
        if (json)
        {
            Emit(Core.JsonValue.Object(new JsonObject
            {
                ["ok"] = Core.JsonValue.Bool(false),
                ["errors"] = Core.JsonValue.Array(
                [
                    Core.JsonValue.Object(new JsonObject { ["message"] = Core.JsonValue.String(message) }),
                ]),
            }));
        }
        else
        {
            errorOutput($"✗ {message}");
        }
        return ExitCode.StorageFailed;
    }

    private static ConfigurationField? ParseField(string name)
    {
        return ConfigurationFieldExtensions.AllCases.FirstOrDefault(field => field.RawValue() == name) is { } match
            ? match
            : null;
    }
}
