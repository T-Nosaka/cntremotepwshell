using cntremotepwshell.Tools;

namespace cntremotepwshell;


/// <summary>
/// 接続診断
/// </summary>
public static class ProbeMain
{
    public static async Task<int> Run()
    {
        var tool = new RemoteCommandTool();
        var text = await tool.ExecuteCommand(
            new ProfileStore(),
            new ServerMode { RequireProfile = false },
            WinRmSettings.FromEnvironment(),
            "hostname");
        Console.WriteLine(text);
        return text.Contains("exit=0") ? 0 : 1;
    }
}

