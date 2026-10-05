namespace Lilara.Core.Storage;

// 全局数据根：LILARA_HOME ?? ~/.lilara（Windows 下 ~ = %USERPROFILE%）。
// 每个 OS 用户一份，天然隔离，不需要额外的换用户机制。
public static class DataRoot
{
    public const string EnvVar = "LILARA_HOME";

    public static string Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var configured = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".lilara");
    }
}
