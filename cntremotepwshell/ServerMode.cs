namespace cntremotepwshell;

/// <summary>
/// stdio は UUID 無しで環境変数を使う。HTTP は UUID 必須。
/// </summary>
public sealed class ServerMode
{
    public bool RequireProfile { get; init; }
}
