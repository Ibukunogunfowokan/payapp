namespace PayApp.Core.Configuration;

public static class DotEnv
{
    // Real environment variables win, so Docker/VM settings are never overridden by a stray .env file.
    public static void Load()
    {
        var path = FindFile(Directory.GetCurrentDirectory()) ?? FindFile(AppContext.BaseDirectory);
        if (path is null)
            return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"');

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? FindFile(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
