namespace cntremotepwshell;

/// <summary>
/// WinRM 接続情報。stdio と HTTP のどちらでも同じ環境変数から読む。
/// </summary>
public sealed class WinRmSettings
{
    /// <summary>
    /// 定義
    /// </summary>
    public const string ShellUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";

    /// <summary>
    /// 接続URI
    /// 例)https://{host}:5986/wsman
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

        return new WinRmSettings
        {
            ConnectionUri = new Uri($"https://{host}:5986/wsman"),
            UserName = user,
            Password = password,
            SkipCertificateCheck = skipCertificateCheck,
        };
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
