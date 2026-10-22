namespace cntremotepwshell;

/// <summary>
/// WinRM 接続情報。stdio の UUID 無し呼び出しだけ環境変数から読む。
/// </summary>
public sealed class WinRmSettings
{
    /// <summary>
    /// 定義
    /// </summary>
    public const string ShellUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";

    /// <summary>
    /// 接続URI
    /// HTTPS は https://{host}:5986/wsman、HTTP は http://{host}:5985/wsman
    /// </summary>
    public required Uri ConnectionUri { get; init; }

    /// <summary>
    /// ユーザー名
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// パスワード
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// 証明書チェック
    /// </summary>
    public bool SkipCertificateCheck { get; init; }

    /// <summary>
    /// 環境変数より取得
    /// </summary>
    /// <returns></returns>
    public static WinRmSettings FromEnvironment()
    {
        var host = Require("WINRM_HOST");
        var user = Require("WINRM_USER");
        var password = Require("WINRM_PASSWORD");
        var skip = Environment.GetEnvironmentVariable("WINRM_SKIP_CERT_CHECK");
        var skipCertificateCheck = string.IsNullOrWhiteSpace(skip)
            || skip.Equals("true", StringComparison.OrdinalIgnoreCase)
            || skip == "1";
        var https = IsEnabled(Environment.GetEnvironmentVariable("WINRM_HTTPS"), defaultValue: true);

        return new WinRmSettings
        {
            ConnectionUri = CreateConnectionUri(host, https),
            UserName = user,
            Password = password,
            SkipCertificateCheck = skipCertificateCheck,
        };
    }

    /// <summary>
    /// HTTPS は 5986、HTTP は 5985。ポートの任意指定はしない。
    /// </summary>
    public static Uri CreateConnectionUri(string host, bool https)
    {
        var scheme = https ? "https" : "http";
        var port = https ? 5986 : 5985;
        return new Uri($"{scheme}://{host}:{port}/wsman");
    }

    /// <summary>
    /// 未設定は defaultValue。true / 1 で有効、false / 0 で無効。
    /// </summary>
    public static bool IsEnabled(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
            return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
            return false;
        return defaultValue;
    }

    /// <summary>
    /// 環境変数値取得
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"環境変数 {name} が未設定です。");

        return value;
    }
}
