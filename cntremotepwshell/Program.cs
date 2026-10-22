using cntremotepwshell.Tools;
using ModelContextProtocol.AspNetCore;
using System.Text;

namespace cntremotepwshell;

internal class Program
{
    /// <summary>
    /// エントリ。引数なしまたは --stdio で stdio、--http で Streamable HTTP。
    /// </summary>
    static async Task Main(string[] args)
    {
        if (args.Contains("-h") || args.Contains("--help"))
        {
            Console.Error.WriteLine("usage: cntremotepwshell [--stdio | --http]");
            return;
        }

        var http = args.Contains("--http");
        if (http && args.Contains("--stdio"))
        {
            Console.Error.WriteLine("--stdio と --http は同時に指定できません。");
            Environment.ExitCode = 2;
            return;
        }

        if (args.Any(arg => arg.StartsWith('-') && arg is not "--stdio" and not "--http" and not "--probe"))
        {
            Console.Error.WriteLine("usage: cntremotepwshell [--stdio | --http]");
            Environment.ExitCode = 2;
            return;
        }

        if (args.Contains("--probe"))
        {
            Environment.ExitCode = await ProbeMain.Run();
            return;
        }

        if (http)
            await RunHttpAsync(args);
        else
            await RunStdioAsync(args);
    }

    /// <summary>
    /// 標準入出力版
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    static async Task RunStdioAsync(string[] args)
    {
        // Windows(日本語環境)ではリダイレクトされたstdin/stdoutの既定コンソールコードページが
        // UTF-8ではないことがあり、MCPのJSON-RPCメッセージに含まれる非ASCII文字が文字化けする。
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        AddRemoteServices(builder.Services, requireProfile: false);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<RemoteCommandTool>();

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// RestAPI版
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    static async Task RunHttpAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls("http://0.0.0.0:8080");

        AddRemoteServices(builder.Services, requireProfile: true);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport( options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateful;
            })
            .WithTools<RemoteCommandTool>();

        var app = builder.Build();
        app.MapMcp();
        await app.RunAsync();
    }

    /// <summary>
    /// サービスセット
    /// </summary>
    /// <param name="services"></param>
    /// <param name="requireProfile">HTTP は UUID 必須。stdio は環境変数を使える。</param>
    static void AddRemoteServices(IServiceCollection services, bool requireProfile)
    {
        services.AddSingleton(new ServerMode { RequireProfile = requireProfile });
        services.AddSingleton(new ProfileStore());

        // stdio の UUID 無し呼び出しだけ環境変数を使う。HTTP では読まない。
        if (!requireProfile)
            services.AddSingleton(WinRmSettings.FromEnvironment());
    }
}
