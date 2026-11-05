using Microsoft.Extensions.FileProviders;

namespace BillingOperations;

public static class Settings
{
    public static void Configure(IConfigurationManager configuration)
    {
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("APPSETTINGS_FILE")
            ?? "/mnt/keyvault/appsettings.json");
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            configuration.AddJsonFile(new PhysicalFileProvider(directory), Path.GetFileName(path),
                optional: true, reloadOnChange: true);
        configuration.AddEnvironmentVariables();
    }

    public static string Required(this IConfiguration config, string key) =>
        !string.IsNullOrWhiteSpace(config[key]) ? config[key]! :
        throw new InvalidOperationException($"Missing required setting: {key}");
}
