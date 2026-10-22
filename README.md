# cntremotepwshell

Windows の WinRM へ接続し、PowerShell コマンドを 実行する MCP サーバーです。

LibreChat や Claude Desktop など、MCP クライアントからリモート Windows を操作する用途を想定しています。呼び出しごとにシェルを作り、終わったら破棄します。カレントディレクトリや変数は次の呼び出しへ残りません。

## 必要条件

- 実行側: .NET 10
- 対象 Windows: WinRM over HTTPS（ポート 5986）が有効であること
- 認証: Negotiate。Linux では Devolutions SSPI を使います
- Linux: ICU。素の Ubuntu 26.04 では `libicu78` が必要でした

```bash
sudo apt install libicu78
```

対象 Windows では、管理者の PowerShell で次を実行してください。

```powershell
Enable-PSRemoting -Force
```

HTTPS リスナー（5986）が無い環境では、別途リスナーの作成が必要です。

## ビルド

```powershell
dotnet build cntremotepwshell\cntremotepwshell.csproj
```

### 単一ファイル

実行先に .NET を入れない自己完結の単一ファイルです。`win-x64` を、配布先の RID に変えてください。

```powershell
dotnet publish cntremotepwshell\cntremotepwshell.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Linux 向けの例です。

```powershell
dotnet publish cntremotepwshell\cntremotepwshell.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish cntremotepwshell\cntremotepwshell.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true
```

出力は `cntremotepwshell\bin\Release\net10.0\<RID>\publish\` です。Linux 向けでも ICU は別途必要です。

## 起動

引数なし、または `--stdio` で標準入出力。`--http` で Streamable HTTP です。

```powershell
cntremotepwshell
cntremotepwshell --stdio
cntremotepwshell --http
```

HTTP の待受は既定で `http://0.0.0.0:8080` です。`ASPNETCORE_URLS` で変更できます。

## 接続先の指定

| 起動 | 接続先 |
|---|---|
| stdio | UUID を省略すると環境変数。UUID を渡すとメモリ上のプロファイル |
| http | UUID 必須。環境変数は読まない |

### stdio の環境変数

| 変数 | 内容 |
|---|---|
| `WINRM_HOST` | ホスト名または IP |
| `WINRM_USER` | ユーザー名。`DOMAIN\user` も可 |
| `WINRM_PASSWORD` | パスワード |
| `WINRM_HTTPS` | 未設定、`true`、`1` で HTTPS 5986。`false` または `0` で HTTP 5985。HTTP ではコマンドと出力が平文になる |
| `WINRM_SKIP_CERT_CHECK` | 証明書検証を省略する。未設定、`true`、`1` で省略。それ以外で検証する。HTTP では使わない |

stdio で UUID 無しの `execute_command` を使う場合、この 3 つ（ホスト、ユーザー、パスワード）が起動時に必要です。

### HTTP のプロファイル

接続先はメモリ上のプロファイルとして登録します。ファイルには書きません。プロセスを再起動すると全て消えます。

- アイドル期限: 最終利用から 30 分
- 絶対期限: 登録から 8 時間
- 期限を延ばすのは `execute_command` だけです。`profile_status` では延ばしません
- パスワードは `register_profile` の引数に一度だけ載ります。応答、状態確認、ログには出しません
- 既定は HTTPS 5986。`register_profile` の `https` を `false` にしたときだけ HTTP 5985
- HTTPS のプロファイルは、証明書検証を省略します。HTTP ではコマンドと出力が平文になります

UUID を知っていれば、そのプロファイルを使えます。一覧を返すツールはありません。

## ツール

### server_mode

起動モードを返します。`mode=http` または `mode=stdio` です。

### register_profile

接続先を登録し、UUID を返します。

| 引数 | 内容 |
|---|---|
| `host` | ホスト名または IP |
| `user` | ユーザー名 |
| `password` | パスワード |
| `https` | 省略または `true` で HTTPS 5986。`false` で HTTP 5985 |

### execute_command

PowerShell コマンドを 1 回実行します。渡した文字列は `powershell.exe -NoProfile -NonInteractive -Command` に渡します。

| 引数 | 内容 |
|---|---|
| `command` | 実行する PowerShell |
| `id` | `register_profile` が返した UUID。HTTP では必須。stdio では省略可 |

UUID が無い、または期限切れの場合は `error=profile_not_found` を返します。成功時は標準出力、標準エラー、`exit=` の終了コードを返します。

### profile_status

ホスト、ユーザー、最終利用、残り期限を返します。パスワードは返しません。期限は延ばしません。

### release_profile

プロファイルを破棄します。既に存在しない、または期限切れの場合も成功します。

## MCP クライアントの例

stdio:

```json
{
  "mcpServers": {
    "cntremotepwshell": {
      "command": "cntremotepwshell",
      "args": ["--stdio"],
      "env": {
        "WINRM_HOST": "windows.example.com",
        "WINRM_USER": "DOMAIN\\user",
        "WINRM_PASSWORD": "password"
      }
    }
  }
}
```

HTTP（LibreChat など、コンテナへ接続する場合）:

```yaml
mcpServers:
  cntremotepwshell:
    type: streamable-http
    url: http://cntremotepwshell:8080/
```

HTTP では、先に `register_profile` で UUID を取得し、以降の `execute_command` にその UUID を渡します。

## ライセンス

[MIT](LICENSE)
