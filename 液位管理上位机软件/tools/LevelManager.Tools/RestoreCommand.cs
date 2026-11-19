using System.Text.Json;
using Naide.LevelManager.Infrastructure;
internal static class RestoreCommand
{
    public static async Task<int> Run(string[] args)
    {
        string Required(string key){var index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException("缺少参数 "+key);}
        if(!args.Contains("--confirm-restore")){Console.WriteLine("先退出采集软件或停止后台服务。恢复将替换当前业务数据、保留恢复前副本并注销旧登录会话；授权不延期，现场核查前不开放新动作。");Console.WriteLine("restore --backup <绝对ZIP路径> --data-root <现有业务目录> --confirm-restore");return 2;}
        try{var result=await OfflineDatabaseRestore.RestoreAsync(Required("--backup"),Required("--data-root"),true);Console.WriteLine(JsonSerializer.Serialize(result));Console.WriteLine("恢复完成。启动软件后请重新登录、核对现场接线和当前设备状态；不会自动启动采集或续跑原任务。");return 0;}catch(Exception e)when(e is IOException or ArgumentException or InvalidOperationException){Console.Error.WriteLine("恢复未完成："+e.Message+"。请确认后台服务已退出、备份有效并保留现场核查条件。");return 3;}
    }
}
