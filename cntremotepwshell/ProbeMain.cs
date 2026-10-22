using System.Text;
using PSWSMan.Lib;

namespace cntremotepwshell;

public static class ProbeMain
{
    public static async Task<int> Run()
    {
        var settings = WinRmSettings.FromEnvironment();
        Console.WriteLine("uri=" + settings.ConnectionUri);
        using var http = new WMRemoteClient(
            settings.ConnectionUri,
            settings.UserName,
            settings.Password,
            settings.SkipCertificateCheck,
            TimeSpan.FromSeconds(30));
        var soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><probe/></s:Body></s:Envelope>";
        var request = new WSManRequest(Guid.NewGuid(), Encoding.UTF8.GetBytes(soap));
        try
        {
            var body = await http.SendAsync(request).ConfigureAwait(false);
            Console.WriteLine("bodyLen=" + body.Length);
            Console.WriteLine(Encoding.UTF8.GetString(body));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().FullName);
            Console.WriteLine(ex);
            return 1;
        }
    }
}
