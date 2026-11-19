using System.Text.Json;
using Naide.LevelManager.Simulator;

namespace Naide.LevelManager.GatewaySimulator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Contains("--headless"))return Headless(args).GetAwaiter().GetResult();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SimulatorWindow(args));return 0;
    }
    private static async Task<int> Headless(string[] args)
    {
        string Option(string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("参数缺少 "+key);}
        var ready=Option("--ready");var stop=Option("--stop-file");
        try
        {
            var model=new GatewayModel(int.Parse(Option("--count")));await using var server=new LoopbackGateway(model);server.Start(int.Parse(Option("--port")));
            File.WriteAllText(ready,JsonSerializer.Serialize(new{passed=true,server.Port,count=model.Count,loopbackOnly=true,serialPortsOpened=0,model.BootId}));
            while(!File.Exists(stop))await Task.Delay(200);
            File.WriteAllText(ready+".stopped.json",JsonSerializer.Serialize(new{server.Requests,server.Writes}));return 0;
        }
        catch(Exception e){File.WriteAllText(ready+".failure.json",JsonSerializer.Serialize(new{error=e.ToString()}));return 1;}
    }
}
