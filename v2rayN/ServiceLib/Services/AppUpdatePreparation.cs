namespace ServiceLib.Services;

public static class AppUpdatePreparation
{
    public static string PrepareHelper(string installedHelper, string temporaryRoot)
    {
        var source = Path.GetDirectoryName(Path.GetFullPath(installedHelper))!;
        var destination = Path.Combine(Path.GetFullPath(temporaryRoot), "freedom-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        // Relocate the runtime as well: a self-contained helper running in the
        // application directory would lock the DLLs it needs to replace.
        foreach (var file in Directory.EnumerateFiles(source, "*.dll")
                     .Concat(Directory.EnumerateFiles(source, "AmazTool.*")).Distinct(StringComparer.OrdinalIgnoreCase))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var culture in new[] { "zh-Hans", "zh-Hant" })
        {
            var resource = Path.Combine(source, culture, "AmazTool.resources.dll");
            if (!File.Exists(resource)) continue;
            Directory.CreateDirectory(Path.Combine(destination, culture));
            File.Copy(resource, Path.Combine(destination, culture, "AmazTool.resources.dll"));
        }
        var helper = Path.Combine(destination, Path.GetFileName(installedHelper));
        if (!File.Exists(helper)) throw new FileNotFoundException("Update helper was not staged.");
        return helper;
    }
}
