namespace Cida.Platform;

/// <summary>
/// Grants the unpackaged process package identity at runtime by registering a sparse
/// package: the PowerToys route to <c>Windows.Media.Ocr</c>, which Microsoft supports only
/// for apps with package identity. Registration is per-user and idempotent; the manifest
/// ships beside the executable (published with the app).
/// </summary>
public sealed class SparsePackageRegistrar
{
    public const string PackageName = "Cida.Win";

    /// <summary>Whether this process already runs with package identity.</summary>
    public static bool HasPackageIdentity()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch (InvalidOperationException)
        {
            // No identity: Package.Current throws outside a package.
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the sparse package for the current user. Returns true when the process
    /// has identity afterwards (already had it, or this call granted it).
    /// </summary>
    public bool EnsureRegistered()
    {
        if (HasPackageIdentity()) return true;

        var packagePath = FindPackage();
        if (packagePath == null)
        {
            Console.Error.WriteLine("identity package not found next to the executable or in the source tree");
            return false;
        }

        return AddPackage(packagePath) && HasPackageIdentity();
    }

    /// <summary>Removes the sparse package (used by the CLI's reset flow).</summary>
    public void Remove()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            var package = manager.FindPackagesForUser(string.Empty)
                .FirstOrDefault(candidate => candidate.Id.Name == PackageName);
            if (package != null)
            {
                var task = manager.RemovePackageAsync(
                    package.Id.FullName,
                    Windows.Management.Deployment.RemovalOptions.None).AsTask();
                task.Wait(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception)
        {
            // Not registered, or the user removed it by other means.
        }
    }

    private static string? FindPackage()
    {
        // The signed identity .msix built by scripts/sparse-package.ps1; looked up next
        // to the executable first, then in the source tree's build output.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "SparsePackage", "build", "CidaWinIdentity.msix"),
            FindSourceTreePackage(),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindSourceTreePackage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory != null; depth++, directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, "SparsePackage", "build", "CidaWinIdentity.msix");
            if (File.Exists(direct)) return direct;
            foreach (var sibling in directory.EnumerateDirectories())
            {
                var nested = Path.Combine(sibling.FullName, "SparsePackage", "build", "CidaWinIdentity.msix");
                if (File.Exists(nested)) return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// Registers the sparse package per-user through PackageManager.AddPackageByUriAsync
    /// with the external location pointing at the executable's directory — the same call
    /// PowerToys makes for its OCR identity. An unsigned manifest is accepted when
    /// Developer Mode is on; a released build ships a signed manifest.
    /// </summary>
    private static bool AddPackage(string packagePath)
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            // The manifest URI must carry the file scheme; AddPackageByUriAsync rejects
            // bare Windows paths. The external location points at the exe's directory.
            var options = new Windows.Management.Deployment.AddPackageOptions
            {
                ExternalLocationUri = new Uri(AppContext.BaseDirectory),
            };
            var task = manager.AddPackageByUriAsync(new Uri(packagePath), options).AsTask();
            task.Wait(TimeSpan.FromSeconds(60));
            var result = task.Result;
            if (!string.IsNullOrEmpty(result?.ErrorText))
            {
                Console.Error.WriteLine($"sparse package registration failed: 0x{result.ExtendedErrorCode.HResult:X} {result.ErrorText}");
            }
            return string.IsNullOrEmpty(result?.ErrorText);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"sparse package registration threw: {error.GetType().Name}: {error.Message}");
            if (error.InnerException != null)
            {
                Console.Error.WriteLine($"inner: {error.InnerException.Message}");
            }
            return false;
        }
    }
}
