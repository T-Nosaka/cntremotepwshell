using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using PSWSMan.Lib;

namespace cntremotepwshell.Tools;

/// <summary>
/// API定義
/// </summary>
[McpServerToolType]
public class RemoteCommandTool
{
    /// <summary>
    /// 受信最大文字数
    /// </summary>
    private const int ReceiveLimit = 1000;

    /// <summary>
    /// 起動モードを返す。http は UUID 必須、stdio は UUID 無しで環境変数を使える。
    /// </summary>
    [McpServerTool(Name = "server_mode"), Description(
        "このサーバーの起動モードを返します。http または stdio です。" +
        "http では execute_command に register_profile の UUID が必須です。" +
        "stdio では UUID を省略すると環境変数の接続先を使います。")]
    public string GetServerMode(ServerMode mode)
    {
        return mode.RequireProfile ? "mode=http" : "mode=stdio";
    }

    /// <summary>
    /// 接続プロファイルをメモリへ登録する。パスワードは応答に含めない。
    /// </summary>
    [McpServerTool(Name = "register_profile"), Description(
        "httpモード用 " +
        "WinRM 接続先をメモリ上のプロファイルとして登録し、UUID を返します。" +
        "以降の execute_command にはこの UUID を渡します。パスワードは再送しません。" +
        "プロファイルはファイルへ書きません。最終利用から30分、または登録から8時間で破棄されます。" +
        "https を省略すると HTTPS 5986。false のときだけ HTTP 5985 です。")]
    public string RegisterProfile(
        ProfileStore store,
        [Description("WinRM のホスト名または IP。")] string host,
        [Description("WinRM のユーザー名。DOMAIN\\user 形式も可。")] string user,
        [Description("WinRM のパスワード。この呼び出し以外では送信しない。")] string password,
        [Description("true または省略で HTTPS 5986。false で HTTP 5985。HTTP ではメッセージ暗号化する。")] bool https = true)
    {
        var id = store.Register(host, user, password, https);
        var profile = store.Peek(id)
            ?? throw new InvalidOperationException("プロファイルの登録に失敗しました。");

        return $"id={profile.Id}\r\nhost={profile.Host}\r\nuser={profile.UserName}\r\ntransport={Transport(profile)}\r\nexpires={Format(profile.ExpiresAt)}";
    }

    /// <summary>
    /// プロファイルの残り期限を返す。期限は延ばさない。
    /// </summary>
    [McpServerTool(Name = "profile_status"), Description(
        "httpモード用 " +
        "登録済みプロファイルのホスト、ユーザー、最終利用、残り期限を返します。" +
        "パスワードは返しません。この呼び出しでは期限を延ばしません。")]
    public string ProfileStatus(
        ProfileStore store,
        [Description("register_profile が返した UUID。")] string id)
    {
        var profile = store.Peek(id);
        if (profile is null)
            return "error=profile_not_found";

        var remaining = profile.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        return $"id={profile.Id}\r\nhost={profile.Host}\r\nuser={profile.UserName}\r\ntransport={Transport(profile)}\r\nlast_used={Format(profile.LastUsedAt)}\r\nexpires={Format(profile.ExpiresAt)}\r\nremaining_sec={(int)remaining.TotalSeconds}";
    }

    /// <summary>
    /// プロファイルを破棄する。既に無い場合も成功とする。
    /// </summary>
    [McpServerTool(Name = "release_profile"), Description(
        "httpモード用 " +
        "登録済みプロファイルを破棄します。既に存在しない、または期限切れの場合も成功します。")]
    public string ReleaseProfile(
        ProfileStore store,
        [Description("register_profile が返した UUID。")] string id)
    {
        store.Release(id);
        return "released=1";
    }

    /// <summary>
    /// リモート実行
    /// </summary>
    [McpServerTool(Name = "execute_command"), Description(
        "リモート Windows で PowerShell コマンドを1回実行します。" +
        "対象Windowsで、WinRMが許可されている必要があります。Powershell(管理者)で、Enable-PSRemoting -Force を実行して下さい。" +
        "呼び出しごとにシェルを作り、終わったら破棄します。カレントディレクトリや変数は次の呼び出しへ残りません。" +
        "渡した文字列は powershell.exe -NoProfile -NonInteractive -Command に渡します。" +
        "HTTPモード では register_profile が返した UUID が必須です。stdioモード では UUID を省略すると環境変数の接続先を使います。")]
    public async Task<string> ExecuteCommand(
        ProfileStore store,
        ServerMode mode,
        WinRmSettings? settings,
        [Description("実行する PowerShell コマンド。セッション状態は保持されない。")] string command,
        [Description("register_profile が返した UUID。HTTP では必須。stdio では省略可。")] string? id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command が空です。", nameof(command));

        var resolved = Resolve(store, mode, settings, id);
        if (resolved is null)
            return "error=profile_not_found";

        var wsman = new WSManClient(
            resolved.ConnectionUri,
            maxEnvelopeSize: 512000,
            operationTimeout: TimeSpan.FromSeconds(60),
            locale: "ja-JP");

        using var http = new WMRemoteClient(
            resolved.ConnectionUri,
            resolved.UserName,
            resolved.Password,
            resolved.SkipCertificateCheck,
            timeout: TimeSpan.FromSeconds(90));

        var shell = new WinRSClient(wsman, WinRmSettings.ShellUri);
        var created = await http.SendAsync<WSManCreateResponse>(shell.Create(), cancellationToken);
        shell.ProcessCreateResponse(created);

        try
        {
            var started = await http.SendAsync<WSManCommandResponse>(
                shell.Command(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", command],
                    noShell: true),
                cancellationToken);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            int? exitCode = null;

            for (var i = 0; i < ReceiveLimit && exitCode is null; i++)
            {
                var received = await http.SendAsync<WSManReceiveResponse>(
                    shell.Receive("stdout stderr", started.CommandId),
                    cancellationToken);

                foreach (var chunk in received.Chunks)
                {
                    var text = Encoding.UTF8.GetString(chunk.Data);
                    if (chunk.Name.Equals("stderr", StringComparison.OrdinalIgnoreCase))
                        stderr.Append(text);
                    else
                        stdout.Append(text);
                }

                if (received.State == CommandState.Done)
                    exitCode = received.ExitCode ?? 0;
            }

            if (exitCode is null)
                throw new TimeoutException("WinRM コマンドが完了しませんでした。");

            var result = new StringBuilder();
            if (stdout.Length > 0)
                result.Append(stdout);
            if (stderr.Length > 0)
            {
                if (result.Length > 0 && result[^1] != '\n')
                    result.AppendLine();
                result.Append(stderr);
            }
            if (result.Length > 0 && result[^1] != '\n')
                result.AppendLine();
            result.Append("exit=").Append(exitCode.Value);
            return result.ToString();
        }
        finally
        {
            await http.SendAsync<WSManDeleteResponse>(shell.Delete(), cancellationToken);
        }
    }

    private static WinRmSettings? Resolve(ProfileStore store, ServerMode mode, WinRmSettings? settings, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return store.Use(id)?.ToSettings();

        if (mode.RequireProfile)
            return null;

        return settings;
    }

    private static string Transport(ProfileStore.Profile profile) =>
        profile.UseHttps ? "https:5986" : "http:5985";

    private static string Format(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
}
