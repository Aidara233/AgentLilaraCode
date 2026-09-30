namespace Lilara.Core.Persistence;

/// <summary>
/// 全局数据根的解析（01 §8）。
/// LILARA_HOME ?? ~/.lilara（Windows 下 ~ = %USERPROFILE%）。
/// 多用户天然隔离：每个 OS 用户一份数据根，不需要额外的换用户机制。
/// </summary>
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
