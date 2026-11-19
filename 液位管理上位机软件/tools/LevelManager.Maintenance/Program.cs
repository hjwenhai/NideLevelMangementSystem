if(args.FirstOrDefault()=="restore")return await RestoreCommand.Run(args);
Console.WriteLine("液位管理系统 · 业务数据维护工具");
Console.WriteLine("restore --backup <绝对ZIP路径> --data-root <现有业务目录> --confirm-restore");
Console.WriteLine("恢复前退出后台服务。恢复保留原库与授权状态，不自动启动采集；恢复后须核查现场配置、设备及原操作。");
return args.Length==0?0:2;
