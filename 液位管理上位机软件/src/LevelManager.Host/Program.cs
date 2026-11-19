using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Naide.LevelManager.Communication;
using Naide.LevelManager.Host;

var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,ContentRootPath=AppContext.BaseDirectory,WebRootPath="wwwroot"});
if(builder.Configuration["urls"] is null)builder.WebHost.UseUrls("http://127.0.0.1:8780");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=1024*1024);
var dataRoot=Path.GetFullPath(builder.Configuration["data-root"]??builder.Configuration["DataRoot"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Naide","LevelManager"));
Directory.CreateDirectory(dataRoot);
using var directoryLock=new FileStream(Path.Combine(dataRoot,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
var protection=builder.Services.AddDataProtection().SetApplicationName("Naide.LevelManager").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot,"keys")));
if(OperatingSystem.IsWindows())protection.ProtectKeysWithDpapi(protectToLocalMachine:true);
builder.Services.AddSingleton(new SqliteConfigurationStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<IConfigurationStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<IUserStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<IRolePermissionStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<ISecurityAudit>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<ICapacityTableStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<IRecordBasisStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<MeteringService>();
builder.Services.AddSingleton<IDataJobStore>(new SqliteDataJobStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<DataJobService>(sp=>new(sp.GetRequiredService<IDataJobStore>(),sp.GetRequiredService<IConfigurationStore>(),sp.GetRequiredService<AcquisitionService>(),sp.GetRequiredService<IHistoryStore>(),sp.GetRequiredService<TimeProvider>(),sp.GetRequiredService<ProductAuthorization>(),Path.Combine(dataRoot,"exports"),Path.Combine(dataRoot,"level-manager.db"),sp.GetRequiredService<ILogger<DataJobService>>()));builder.Services.AddHostedService(sp=>sp.GetRequiredService<DataJobService>());
builder.Services.AddSingleton<IInstrumentFaultStore>(new SqliteInstrumentFaultStore(Path.Combine(dataRoot,"level-manager.db")));builder.Services.AddSingleton<InstrumentFaultTracking>();
builder.Services.AddSingleton<IAlarmStore>(new SqliteAlarmStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<AlarmService>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<AlarmService>());
builder.Services.AddSingleton<IEventWriter>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());
builder.Services.AddSingleton<IHistoryStore>(new SqliteHistoryStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<IRegisterClientFactory,RegisterClientFactory>();
builder.Services.AddSingleton<IControlStore>(new SqliteControlStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<IWirelessTaskStore>(new SqliteWirelessTaskStore(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<IMotionEvidenceStore>(new SqliteMotionEvidence(Path.Combine(dataRoot,"level-manager.db")));
builder.Services.AddSingleton<MotionEvidenceGuard>(sp=>new(sp.GetRequiredService<IMotionEvidenceStore>(),sp.GetRequiredService<ProductAuthorization>(),sp.GetRequiredService<IControlStore>(),sp.GetRequiredService<IWirelessTaskStore>(),sp.GetRequiredService<TimeProvider>(),Path.Combine(dataRoot,"restoration-review.required"),sp.GetRequiredService<ILogger<MotionEvidenceGuard>>()));builder.Services.AddHostedService(sp=>sp.GetRequiredService<MotionEvidenceGuard>());
builder.Services.AddSingleton<WirelessRuntime>();
builder.Services.AddSingleton<ReadOnlyIntegration>(sp=>new(dataRoot,sp.GetRequiredService<IDataProtectionProvider>(),sp.GetRequiredService<IEventWriter>(),sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<TankConfigurationTransfer>();
builder.Services.AddSingleton<RuntimeOperationGate>();builder.Services.AddSingleton<ConfigurationCoordinator>();
builder.Services.AddSingleton(TimeProvider.System);
var authorizationRoot=Path.Combine(Path.GetDirectoryName(dataRoot)!,Path.GetFileName(dataRoot)+".authorization");
builder.Services.AddSingleton<ProductAuthorization>(sp=>new(authorizationRoot,sp.GetRequiredService<TimeProvider>(),sp.GetRequiredService<ILogger<ProductAuthorization>>()));builder.Services.AddHostedService(sp=>sp.GetRequiredService<ProductAuthorization>());
builder.Services.AddSingleton<SnapshotScheduler>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<SnapshotScheduler>());
builder.Services.AddSingleton<HistoryMaintenance>(sp=>new(sp.GetRequiredService<IConfigurationStore>(),sp.GetRequiredService<IHistoryStore>(),sp.GetRequiredService<IEventWriter>(),sp.GetRequiredService<TimeProvider>(),sp.GetRequiredService<ProductAuthorization>(),Path.Combine(dataRoot,"level-manager.db"),sp.GetRequiredService<ILogger<HistoryMaintenance>>()));builder.Services.AddHostedService(sp=>sp.GetRequiredService<HistoryMaintenance>());
builder.Services.AddSingleton<ChannelAccess>();
builder.Services.AddSingleton<RealtimeTrends>();
builder.Services.AddSingleton<AcquisitionService>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<AcquisitionService>());
builder.Services.AddSingleton<ControlService>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<ControlService>());
builder.Services.AddSingleton<WirelessTaskService>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<WirelessTaskService>());
builder.Services.AddHostedService<ReadOnlyStartup>();
builder.Services.AddSingleton<ConfigurationService>();builder.Services.AddSingleton<IPasswordHasher<UserAccount>,PasswordHasher<UserAccount>>();
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues:false));o.SerializerOptions.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow;});
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="LevelManager.Csrf";o.Cookie.SameSite=SameSiteMode.Strict;});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>{
 o.Cookie.Name="LevelManager.Session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.ExpireTimeSpan=TimeSpan.FromHours(8);o.SlidingExpiration=true;
 o.Events.OnValidatePrincipal=async ctx=>{var id=ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);var user=id is null?null:await ctx.HttpContext.RequestServices.GetRequiredService<IUserStore>().FindByIdAsync(id,ctx.HttpContext.RequestAborted);if(user is null||!user.Enabled||user.Role.ToString()!=ctx.Principal?.FindFirstValue(ClaimTypes.Role)||user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)!=ctx.Principal?.FindFirstValue("securityVersion")){ctx.RejectPrincipal();await ctx.HttpContext.SignOutAsync();}
 else if(ctx.Principal!.Identity is ClaimsIdentity identity){foreach(var claim in identity.FindAll(PermissionEndpoints.ClaimType).ToArray())identity.RemoveClaim(claim);var actor=await ctx.HttpContext.RequestServices.GetRequiredService<IRolePermissionStore>().ResolveAsync(user,ctx.HttpContext.RequestAborted);identity.AddClaims(actor.Permissions!.Select(p=>new Claim(PermissionEndpoints.ClaimType,p.ToString())));}};
 o.Events.OnRedirectToLogin=async ctx=>{ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="登录已失效，请重新登录"});};o.Events.OnRedirectToAccessDenied=async ctx=>{ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="当前账号没有此项操作权限"});};});
builder.Services.AddAuthorization(o=>{o.AddPolicy("administrator",p=>p.RequireRole(AccountRole.Administrator.ToString()));o.AddPolicy("operator",p=>p.RequireRole(AccountRole.Administrator.ToString(),AccountRole.Operator.ToString()));foreach(var permission in Enum.GetValues<AppPermission>()){var key=permission.ToString();o.AddPolicy(key,p=>p.RequireAuthenticatedUser().RequireClaim(PermissionEndpoints.ClaimType,key));}});
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddFixedWindowLimiter("external-readonly",l=>{l.PermitLimit=120;l.Window=TimeSpan.FromMinutes(1);l.QueueLimit=0;l.AutoReplenishment=true;});o.AddFixedWindowLimiter("authentication",l=>{l.PermitLimit=10;l.Window=TimeSpan.FromMinutes(1);l.QueueLimit=0;l.AutoReplenishment=true;});});
builder.Services.AddSignalR();builder.Services.AddWindowsService(o=>o.ServiceName=builder.Configuration["ServiceName"]??"Naide.LevelManager");
var app=builder.Build();var store=app.Services.GetRequiredService<SqliteConfigurationStore>();await store.InitializeAsync();
app.Use(async(ctx,next)=>{
 try{
  if(ctx.Request.Path.StartsWithSegments("/api"))ctx.Response.Headers.CacheControl="no-store";
  else if(ctx.Request.Path=="/"||ctx.Request.Path.Value?.EndsWith(".html",StringComparison.OrdinalIgnoreCase)==true)ctx.Response.Headers.CacheControl="no-cache";
  ctx.Response.Headers.XContentTypeOptions="nosniff";ctx.Response.Headers.XFrameOptions="DENY";ctx.Response.Headers["Referrer-Policy"]="same-origin";
  if((ctx.Request.Path.StartsWithSegments("/api/capacity-tables")||ctx.Request.Path.StartsWithSegments("/api/authorization/import")||ctx.Request.Path.StartsWithSegments("/api/tank-configuration"))&&ctx.Request.Method=="POST"){
   var feature=ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();if(feature is not null&&!feature.IsReadOnly)feature.MaxRequestBodySize=20L*1024*1024;
  }
  var host=ctx.Request.Host.Host;
  if(host!="localhost"&&!IPAddress.TryParse(host,out _)){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="访问主机名未配置"});return;}
  await next();
 }catch(AuthorizationRestrictedException e){ctx.Response.StatusCode=423;await ctx.Response.WriteAsJsonAsync(new{error=e.Message,code="authorization.restricted"});}
 catch(LicenseValidationException e){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
 catch(ConfigurationException e){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message,issues=e.Issues});}
 catch(RevisionConflictException e){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
 catch(UnauthorizedAccessException e){ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
 catch(InvalidDataException e){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message,code="document.invalid"});}
 catch(ArgumentException e){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
 catch(SqliteException e){app.Logger.LogError(e,"数据库操作失败");ctx.Response.StatusCode=503;await ctx.Response.WriteAsJsonAsync(new{error="数据库暂时无法保存或读取，操作未确认成功"});}
 catch(InvalidOperationException e){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}
});
app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
app.Use(async(ctx,next)=>{
 if(ctx.User.Identity?.IsAuthenticated==true)
 {
  var path=ctx.Request.Path.Value??"";var auth=ctx.RequestServices.GetRequiredService<ProductAuthorization>();
  if((path.StartsWith("/api/wireless-",StringComparison.Ordinal)||path.EndsWith("/wireless-tasks",StringComparison.Ordinal))&&!auth.HasAverageModule){ctx.Response.StatusCode=404;await ctx.Response.WriteAsJsonAsync(new{error="接口尚未开放"});return;}
  var history=path.StartsWith("/api/history",StringComparison.Ordinal)||path=="/api/trends/history"||path=="/api/events"||path.StartsWith("/api/snapshots",StringComparison.Ordinal)||(path.StartsWith("/api/data-jobs",StringComparison.Ordinal)&&ctx.Request.Method=="GET")||path.StartsWith("/api/capacity-tables",StringComparison.Ordinal)&&path.EndsWith("/export",StringComparison.Ordinal)||path=="/api/alarms"&&ctx.Request.Method=="GET"&&(ctx.Request.Query["state"] is var state&&(state=="history"||state=="all"));
  if(history)auth.RequireLicensed("历史查询、数据任务或导出");
 }
 await PermissionRouting.CheckAsync(ctx);
 if(ctx.Request.Path.StartsWithSegments("/api")&&ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS")){
  try{await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);}catch(AntiforgeryValidationException){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="页面校验已失效，请刷新后再操作"});return;}
 }
 await next();
});
app.MapGet("/api/health",()=>Results.Ok(new{product="液位管理系统",version="0.3.0",revision="20261118-r24",build="20261118-r24",customerFeedback="20261118",faultCommandPolicy="allow-reported-faults",gatewayProtocol="V3.2-D2-20261117",independentChannelAdd=true,gatewayReviewFlow="20261111",controlConfirmation="20261113",stage="正式发布",maximumTanks=128,baselineHistorySeconds=60,baselineRetentionMonths=12,baselineAnnualRows=ConfigurationRules.BaselineAnnualRows,acquisitionImplemented=true,controlImplemented=true,historyImplemented=true}));
app.MapGet("/api/session",async(HttpContext ctx,IAntiforgery csrf,IUserStore users,CancellationToken ct)=>{
 var token=csrf.GetAndStoreTokens(ctx);return Results.Ok(new{needsSetup=await users.NeedsSetupAsync(ct),csrfToken=token.RequestToken,user=ctx.User.Identity?.IsAuthenticated==true?new{accountId=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier),displayName=ctx.User.FindFirstValue(ClaimTypes.Name),role=ctx.User.FindFirstValue(ClaimTypes.Role),permissions=ctx.User.FindAll(PermissionEndpoints.ClaimType).Select(c=>c.Value).ToArray()}:null});});
app.MapPost("/api/auth/setup",async(SetupRequest input,HttpContext ctx,IUserStore users,IPasswordHasher<UserAccount> hasher,CancellationToken ct)=>{
 if(ctx.Connection.RemoteIpAddress is not {} ip||!IPAddress.IsLoopback(ip))return Results.Json(new{error="首次管理员设置请在采集主机本机进行"},statusCode:403);
 if(input.Login is null||!Regex.IsMatch(input.Login,"^[A-Za-z0-9_-]{3,60}$")||input.Password is null||input.Password.Length is <8 or >128||input.DisplayName is null||input.DisplayName.Length is <1 or >80)return Results.BadRequest(new{error="账号应为3～60位字母、数字、下划线或横线；密码8～128字符；请填写姓名"});
 var user=new UserAccount(Guid.NewGuid().ToString(),input.Login,input.DisplayName,AccountRole.Administrator,"",true);user=user with{PasswordHash=hasher.HashPassword(user,input.Password)};await users.CreateInitialAdministratorAsync(user,ct);return Results.Ok(new{message="管理员已建立，请登录"});}).RequireRateLimiting("authentication");
app.MapPost("/api/auth/login",async(LoginRequest input,HttpContext ctx,IUserStore users,ISecurityAudit audit,IPasswordHasher<UserAccount> hasher,CancellationToken ct)=>{
 if(input.Login is null||input.Password is null||input.Login.Length>60||input.Password.Length>128)return Results.BadRequest(new{error="账号或密码输入无效"});
 var user=await users.FindAsync(input.Login,ct);
 if(user is null||!user.Enabled||hasher.VerifyHashedPassword(user,user.PasswordHash,input.Password)==PasswordVerificationResult.Failed){await audit.RecordAsync("",input.Login,"账号登录","失败",ct);return Results.Json(new{error="账号或密码错误"},statusCode:401);}
 await audit.RecordAsync(user.Id,user.DisplayName,"账号登录","成功",ct);
 var claims=new[]{new Claim(ClaimTypes.NameIdentifier,user.Id),new Claim(ClaimTypes.Name,user.DisplayName),new Claim(ClaimTypes.Role,user.Role.ToString()),new Claim("securityVersion",user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))};await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));return Results.Ok(new{message="登录成功"});}).RequireRateLimiting("authentication");
app.MapPost("/api/auth/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync();return Results.Ok(new{message="已退出"});}).RequireAuthorization();
app.MapUserEndpoints();app.MapPermissionEndpoints();app.MapTankConfigurationTransfer();
app.MapGet("/api/presentation",(ProductAuthorization authorization)=>Results.Ok(new{branding=authorization.Branding,enabledModules=authorization.EnabledModules}));
app.MapGet("/api/integration/access",(ReadOnlyIntegration integration)=>Results.Ok(integration.Status())).RequireAuthorization("IntegrationManage");
app.MapPost("/api/integration/access",async(ReadOnlyAccessRequest input,HttpContext ctx,ReadOnlyIntegration integration,IConfigurationStore configs,CancellationToken ct)=>Results.Ok(await integration.ChangeAsync(input,await configs.GetAsync(ct),Actor(ctx),ct))).RequireAuthorization("IntegrationManage");
app.MapGet("/api/external/v1/current",async(HttpContext ctx,ReadOnlyIntegration integration,AcquisitionService acquisition,IConfigurationStore configs,IAlarmStore alarms,CancellationToken ct)=>{var allowed=integration.Authorize(ctx.Request.Headers.Authorization);if(allowed is null){ctx.Response.Headers.WWWAuthenticate="Bearer";return Results.Json(new{error="对外只读令牌无效或接口未启用"},statusCode:401);}return Results.Ok(await integration.SnapshotAsync(allowed,acquisition,configs,alarms,ct));}).RequireRateLimiting("external-readonly");
app.MapGet("/api/authorization/status",(ProductAuthorization authorization)=>Results.Ok(authorization.Status())).RequireAuthorization();
app.MapGet("/api/authorization/request",(ProductAuthorization authorization)=>Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(authorization.InstallationRequest()),"application/json","液位管理系统-安装授权请求.json")).RequireAuthorization("administrator");
app.MapPost("/api/authorization/import",async(AuthorizationImportRequest input,HttpContext ctx,ProductAuthorization authorization,IEventWriter audit,IWirelessTaskStore tasks,CancellationToken ct)=>{var candidate=LicenseVerifier.Verify(input.EnvelopeJson,authorization.InstallationId,ProductTrust.PublicKeyPem);if(authorization.HasAverageModule&&!candidate.License.EnabledModules.Contains(ProductTrust.AverageModule)&&(await tasks.ListAsync(activeOnly:true,ct:ct)).Count>0)throw new InvalidOperationException("仍有执行中或待核查的测温任务，暂不能撤销该交付模块");await authorization.ImportAsync(input.EnvelopeJson,Actor(ctx),audit,ct);return Results.Ok(authorization.Status());}).RequireAuthorization("administrator");
app.MapGet("/api/system/serial-ports",()=>Results.Ok(System.IO.Ports.SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>int.TryParse(p.AsSpan(p.StartsWith("COM",StringComparison.OrdinalIgnoreCase)?3:0),out var n)?n:int.MaxValue).ThenBy(p=>p).ToArray())).RequireAuthorization("ConfigurationEdit");
app.MapGet("/api/record-basis",async(Guid tankId,long configurationRevision,long calculationRevision,IRecordBasisStore store,CancellationToken ct)=>Results.Ok(await store.GetRecordBasisAsync(tankId,configurationRevision,calculationRevision,ct))).RequireAuthorization();
app.MapGet("/api/config",async(ConfigurationService service,CancellationToken ct)=>Results.Ok(await service.GetAsync(ct))).RequireAuthorization();
app.MapPut("/api/config",async(SystemConfiguration config,HttpContext ctx,ConfigurationCoordinator coordinator,CancellationToken ct)=>{
 var actor=ctx.CurrentActor();var saved=await coordinator.SaveAsync(config,actor,ct);return Results.Ok(saved);}).RequireAuthorization("ConfigurationEdit");
app.MapGet("/api/storage/health",async(IConfigurationStore repository,CancellationToken ct)=>Results.Ok(await repository.GetHealthAsync(ct))).RequireAuthorization("administrator");
app.MapGet("/api/events",async(HttpRequest request,IConfigurationStore repository,CancellationToken ct)=>{
 var q=request.Query;long? Time(string key){if(string.IsNullOrWhiteSpace(q[key]))return null;if(!DateTimeOffset.TryParse(q[key],out var parsed))throw new ArgumentException("查询时间格式无效");return parsed.ToUnixTimeMilliseconds();}var limit=100;if(!string.IsNullOrWhiteSpace(q["limit"])&&!int.TryParse(q["limit"],out limit))throw new ArgumentException("查询数量无效");
 return Results.Ok(await repository.QueryEventsAsync(new(q["category"],q["tank"],q["actor"],q["keyword"],Time("start"),Time("end"),limit),ct));}).RequireAuthorization("HistoryRead");
static AuditActor Actor(HttpContext ctx)=>ctx.CurrentActor();
app.MapDelete("/api/capacity-tables/{id:guid}",async(Guid id,long revision,bool confirmed,HttpContext ctx,ICapacityTableStore tables,RuntimeOperationGate runtime,CancellationToken ct)=>{if(!confirmed)throw new ArgumentException("请确认删除罐容表");await runtime.Gate.WaitAsync(ct);try{await tables.DeleteTableAsync(id,revision,Actor(ctx),ct);return Results.Ok(new{deleted=true});}finally{runtime.Gate.Release();}}).RequireAuthorization("CapacityManage");
app.MapGet("/api/capacity-tables",async(ICapacityTableStore tables,CancellationToken ct)=>Results.Ok(await tables.ListTablesAsync(ct))).RequireAuthorization();
app.MapGet("/api/capacity-tables/{id:guid}",async(Guid id,long? revision,ICapacityTableStore tables,CancellationToken ct)=>{var table=await tables.GetTableAsync(id,revision,ct);return table is null?Results.NotFound(new{error="罐容表不存在"}):Results.Ok(table);}).RequireAuthorization();
app.MapPost("/api/capacity-tables",async(CapacityTable table,HttpContext ctx,ICapacityTableStore tables,CancellationToken ct)=>Results.Ok(await tables.SaveTableAsync(table,Actor(ctx),ct))).RequireAuthorization("CapacityManage");
app.MapPost("/api/capacity-tables/{id:guid}/associations",async(Guid id,CapacityAssociationRequest input,HttpContext ctx,ICapacityTableStore tables,ConfigurationService service,ConfigurationCoordinator coordinator,CancellationToken ct)=>{
 var table=await tables.GetTableAsync(id,ct:ct)??throw new ArgumentException("罐容表不存在");
 if(input.Action is not ("Associate" or "Remove")||input.TankIds is null||input.TankIds.Count is <1 or >128||input.TankIds.Distinct().Count()!=input.TankIds.Count)throw new ArgumentException("请选择储罐及有效的关联操作");
 if(input.Action=="Associate"&&!table.Enabled)throw new ArgumentException("请先启用罐容表");
 var config=await service.GetAsync(ct);if(config.Revision!=input.ConfigurationRevision)throw new RevisionConflictException();
 var selected=input.TankIds.ToHashSet();if(selected.Any(t=>!config.Tanks.Any(x=>x.Id==t)))throw new ArgumentException("所选储罐不存在");
 var changed=0;
 var tanks=config.Tanks.Select(t=>{if(!selected.Contains(t.Id))return t;var m=t.Metering;
 Guid? current=table.Kind switch{CapacityTableKind.Main=>m.MainTableId,CapacityTableKind.StaticPressure=>m.StaticPressureTableId,_=>m.DecimalTableId};
 if(input.Action=="Remove"&&current!=id)return t;
 if(input.Action=="Associate"&&current.HasValue&&current!=id&&!input.ReplaceExisting)throw new InvalidOperationException("部分储罐已关联同类其他表，请确认替换后再提交");
 Guid? target=input.Action=="Associate"?id:null;
 var next=table.Kind switch{CapacityTableKind.Main=>m with{MainTableId=target,Enabled=target.HasValue&&input.EnableVolume},CapacityTableKind.StaticPressure=>m with{StaticPressureTableId=target},_=>m with{DecimalTableId=target}};
 if(next==m)return t;changed++;return t with{Metering=next};}).ToArray();
 if(changed==0)return Results.Ok(config);
 var saved=await coordinator.SaveAsync(config with{Tanks=tanks},Actor(ctx),ct);return Results.Ok(saved);
}).RequireAuthorization("CapacityManage");
app.MapPost("/api/capacity-tables/import-preview",async(HttpRequest request,CancellationToken ct)=>{
 var q=request.Query;int Number(string key,int fallback)=>string.IsNullOrWhiteSpace(q[key])?fallback:int.TryParse(q[key],out var n)?n:throw new ArgumentException("行列选择无效");
 using var memory=new MemoryStream();var buffer=new byte[65536];int read;
 while((read=await request.Body.ReadAsync(buffer,ct))>0){if(memory.Length+read>20*1024*1024)throw new ArgumentException("文件不能超过20MiB");await memory.WriteAsync(buffer.AsMemory(0,read),ct);}
 return Results.Ok(q["kind"]=="Decimal" ? CapacityExcel.PreviewDecimal(memory.ToArray(),Number("sheet",0),Number("firstRow",2),ct) : CapacityExcel.Preview(memory.ToArray(),Number("sheet",0),Number("firstRow",2),Number("heightColumn",1),Number("volumeColumn",2),ct));
}).RequireAuthorization("CapacityManage");
app.MapGet("/api/capacity-tables/{id:guid}/export",async(Guid id,long? revision,ICapacityTableStore tables,CancellationToken ct)=>{
 var table=await tables.GetTableAsync(id,revision,ct)??throw new ArgumentException("罐容表不存在");return Results.File(CapacityExcel.Export(table),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",fileDownloadName:table.Number+"-v"+table.Revision+".xlsx");
}).RequireAuthorization("DataExport");
app.MapPost("/api/tanks/{id:guid}/display",async(Guid id,TankDisplayRequest input,HttpContext ctx,ConfigurationService service,ConfigurationCoordinator coordinator,CancellationToken ct)=>{
 var config=await service.GetAsync(ct);if(config.Revision!=input.ConfigurationRevision)throw new RevisionConflictException();
 if(input.Action is not ("Pin" or "Unpin" or "Hide" or "Show"))throw new ArgumentException("显示操作无效");
 if(!config.Tanks.Any(t=>t.Id==id))throw new ArgumentException("储罐不存在");
 var tanks=config.Tanks.Select(t=>t.Id!=id?t:input.Action switch{"Pin"=>t with{PinnedAtUtc=t.PinnedAtUtc??DateTimeOffset.UtcNow},"Unpin"=>t with{PinnedAtUtc=null},"Hide"=>t with{Hidden=true},_=>t with{Hidden=false}}).ToArray();
 var saved=await coordinator.SaveAsync(config with{Tanks=tanks},Actor(ctx),ct);return Results.Ok(saved);
}).RequireAuthorization("ConfigurationEdit");
app.MapPost("/api/storage/recovery-gateway-reset/{id:guid}",async(Guid id,RecoveryGatewayResetRequest input,HttpContext ctx,ControlService control,CancellationToken ct)=>Results.Ok(await control.ResetAfterRestoreAsync(id,input.ConfigurationRevision,input.Cycle,input.Reason,input.Confirmed,Actor(ctx),ct))).RequireAuthorization("administrator");
app.MapGet("/api/storage/recovery",async(MotionEvidenceGuard guard,CancellationToken ct)=>{await guard.Ready.WaitAsync(ct);return Results.Ok(new{requiresReview=guard.RequiresReview,message=guard.RequiresReview?"恢复后的业务库缺少独立写入证据，请核对现场接线、地址、浮子运动和仪表模式后再解除限制":"业务数据与独立写入证据一致"});}).RequireAuthorization();
app.MapPost("/api/storage/recovery-review",async(RecoveryReviewRequest input,HttpContext ctx,MotionEvidenceGuard guard,CancellationToken ct)=>{await guard.ReviewAsync(Actor(ctx),input.Reason,input.Confirmed,ct);return Results.Ok(new{message="核查结论已记录，未发送动作或自动续跑任务"});}).RequireAuthorization("administrator");
app.MapPost("/api/storage/retention-change",async(RetentionChangeRequest input,HttpContext ctx,ConfigurationCoordinator coordinator,CancellationToken ct)=>{if(!input.Confirmed)throw new ArgumentException("请确认缩短期限会清理超期历史，并已保存有效备份");return Results.Ok(await coordinator.SaveAsync(input.Configuration,Actor(ctx),ct,input.BackupId));}).RequireAuthorization("ConfigurationEdit");
app.MapPost("/api/storage/backup",async(HttpContext ctx,DataJobService jobs,CancellationToken ct)=>Results.Ok(new{id=await jobs.BackupAsync(Actor(ctx),ct)})).RequireAuthorization("BackupManage");
app.MapPost("/api/history/export",async(HistoryExportRequest input,HttpContext ctx,DataJobService jobs,CancellationToken ct)=>Results.Ok(new{id=await jobs.ExportAsync(input,Actor(ctx),ct)})).RequireAuthorization("DataExport");
app.MapPost("/api/snapshots",async(SnapshotRequest input,HttpContext ctx,DataJobService jobs,CancellationToken ct)=>Results.Ok(new{id=await jobs.SnapshotAsync(input,Actor(ctx),ct)})).RequireAuthorization("SnapshotCreate");
app.MapGet("/api/snapshots",async(IDataJobStore jobs,CancellationToken ct)=>Results.Ok(await jobs.SnapshotsAsync(ct))).RequireAuthorization("HistoryRead");
app.MapGet("/api/snapshots/{id:guid}",async(Guid id,IDataJobStore jobs,CancellationToken ct)=>await jobs.SnapshotAsync(id,ct) is {} snapshot?Results.Ok(snapshot):Results.NotFound()).RequireAuthorization();
app.MapGet("/api/storage/maintenance",(HistoryMaintenance maintenance)=>Results.Ok(maintenance.Status)).RequireAuthorization("administrator");
app.MapGet("/api/data-jobs",async(HttpContext ctx,IDataJobStore jobs,CancellationToken ct)=>Results.Ok((await jobs.ListAsync(ct)).Where(j=>j.Kind==DataJobKind.DatabaseBackup?Actor(ctx).Allows(AppPermission.BackupManage):Actor(ctx).Allows(AppPermission.HistoryRead)))).RequireAuthorization();
app.MapPost("/api/data-jobs/{id:guid}/cancel",async(Guid id,HttpContext ctx,DataJobService service,CancellationToken ct)=>{await service.CancelAsync(id,Actor(ctx),ct);return Results.Ok(new{message="取消请求已处理"});}).RequireAuthorization();
app.MapGet("/api/data-jobs/{id:guid}/download",async(Guid id,IDataJobStore jobs,DataJobService service,CancellationToken ct)=>{
 var job=await jobs.GetAsync(id,ct);if(job is null||job.State!=DataJobState.Completed||job.FileName is null)return Results.NotFound();
 var directory=Path.GetFullPath(service.DirectoryFor(id))+Path.DirectorySeparatorChar;var file=Path.GetFullPath(Path.Combine(directory,job.FileName));if(!file.StartsWith(directory,StringComparison.OrdinalIgnoreCase)||!File.Exists(file))return Results.NotFound();
 return Results.File(file,job.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)?"application/zip":"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",job.FileName,enableRangeProcessing:true);
}).RequireAuthorization();
app.MapPost("/api/trends/history",async(TrendQuery query,IHistoryStore history,IConfigurationStore configs,TimeProvider clock,CancellationToken ct)=>{TrendValues.Validate(query);var config=await configs.GetAsync(ct);if(query.Series.Any(s=>!config.Tanks.Any(t=>t.Id==s.TankId)))throw new ArgumentException("曲线包含不存在的储罐");return Results.Ok(new TrendResult(query.StartUtcMs,query.EndUtcMs,clock.GetUtcNow().ToUnixTimeMilliseconds(),await history.QueryTrendAsync(query,ct),"历史曲线使用归档时间及当时结果；减少显示点数时保留实际首尾和极值，缺测保持断线，不估算。"));}).RequireAuthorization("HistoryRead");
app.MapPost("/api/trends/realtime",async(TrendQuery query,RealtimeTrends realtime,IConfigurationStore configs,TimeProvider clock,CancellationToken ct)=>{TrendValues.Validate(query);var config=await configs.GetAsync(ct);if(query.Series.Any(s=>!config.Tanks.Any(t=>t.Id==s.TankId)))throw new ArgumentException("曲线包含不存在的储罐");var now=clock.GetUtcNow().ToUnixTimeMilliseconds();return Results.Ok(realtime.Query(query with{StartUtcMs=now-1800000,EndUtcMs=now+1},config.ReadingExpirySeconds));}).RequireAuthorization();
app.MapGet("/api/alarms/summary",async(IAlarmStore alarms,AlarmService service,CancellationToken ct)=>Results.Ok(new{summary=await alarms.SummaryAsync(ct),error=service.Error})).RequireAuthorization();
app.MapGet("/api/alarms",async(string? state,Guid? tank,DateTimeOffset? start,DateTimeOffset? end,IAlarmStore alarms,AlarmService service,CancellationToken ct)=>Results.Ok(new{records=await alarms.QueryAsync(new(state??"attention",tank,start,end),ct),error=service.Error})).RequireAuthorization();
app.MapPost("/api/alarms/acknowledge",async(AlarmAcknowledgeRequest input,HttpContext ctx,IAlarmStore alarms,TimeProvider clock,CancellationToken ct)=>{if(input.Ids is null)throw new ArgumentException("请选择报警");return Results.Ok(new{acknowledged=await alarms.AcknowledgeAsync(input.Ids,Actor(ctx),clock.GetUtcNow(),ct)});}).RequireAuthorization("AlarmAcknowledge");
app.MapGet("/api/wireless-tasks",async(Guid? tank,HttpContext ctx,IWirelessTaskStore tasks,WirelessTaskService service,ProductAuthorization authorization,CancellationToken ct)=>{var rows=await tasks.ListAsync(tank,ct:ct);return Results.Ok(authorization.Status().AllowsLicensedActions&&Actor(ctx).Allows(AppPermission.HistoryRead)?rows:rows.Where(r=>r.HoldsTank||service.IsCurrent(r.Id)));}).RequireAuthorization();
app.MapGet("/api/wireless-support",(string protocol)=>{if(!Enum.TryParse<AcquisitionProtocol>(protocol,out var parsed)||!Enum.IsDefined(parsed))throw new ArgumentException("协议无效");var why=WirelessDevice.SupportReason(parsed);return Results.Ok(new{supported=why is null,reason=why});}).RequireAuthorization();
app.MapPost("/api/tanks/{id:guid}/wireless-tasks",async(Guid id,WirelessStartRequest input,HttpContext ctx,WirelessTaskService tasks,CancellationToken ct)=>Results.Ok(await tasks.RequestAsync(id,input.ConfigurationRevision,input.Points,input.Confirmed,Actor(ctx),ct))).RequireAuthorization("AverageTemperatureStart");
app.MapPost("/api/wireless-tasks/{id:guid}/cancel",async(Guid id,WirelessReviewRequest input,HttpContext ctx,WirelessTaskService tasks,CancellationToken ct)=>{await tasks.CancelAndStopAsync(id,input.Confirmed,Actor(ctx),ct);return Results.Ok(new{message="后续任务已取消并已请求停止；请核对实际位置与运动状态，完成核查"});}).RequireAuthorization("InstrumentStop");
app.MapPost("/api/wireless-tasks/{id:guid}/review",async(Guid id,WirelessReviewRequest input,HttpContext ctx,WirelessTaskService tasks,CancellationToken ct)=>{await tasks.ReviewAsync(id,input.Confirmed,input.Reason,Actor(ctx),ct);return Results.Ok(new{message="核查结论已保存，未重放任务"});}).RequireAuthorization("ControlReview");
app.MapGet("/api/controls",async(Guid? tank,bool? unresolved,HttpContext ctx,IControlStore controls,ControlService service,WirelessTaskService tasks,ProductAuthorization authorization,CancellationToken ct)=>{var rows=await controls.ListAsync(tank,unresolved??false,ct);return Results.Ok(authorization.Status().AllowsLicensedActions&&Actor(ctx).Allows(AppPermission.HistoryRead)?rows:rows.Where(r=>service.IsCurrent(r)||r.MeasurementTaskId.HasValue&&tasks.IsCurrent(r.MeasurementTaskId.Value)));}).RequireAuthorization();
app.MapGet("/api/tanks/{id:guid}/gateway-controls",async(Guid id,ControlService control,CancellationToken ct)=>Results.Ok(await control.GatewayStatusAsync(id,ct))).RequireAuthorization();
app.MapPost("/api/tanks/{id:guid}/gateway-controls/reset",async(Guid id,GatewayLiveResetRequest input,HttpContext ctx,ControlService control,CancellationToken ct)=>Results.Ok(await control.ResetLiveGatewayAsync(id,input.Expected,input.Cycle,Actor(ctx),input.Reason,input.Confirmed,ct,input.ReviewedControlIds))).RequireAuthorization("ControlReview");
app.MapPost("/api/tanks/{id:guid}/controls",async(Guid id,ControlRequest input,HttpContext ctx,ControlService control,CancellationToken ct)=>Results.Ok(await control.RequestAsync(id,input.ConfigurationRevision,input.Command,input.Confirmed,Actor(ctx),ct,input.TargetLevelMm,input.Reason))).RequireAuthorization();
app.MapPost("/api/controls/{id:guid}/reset",async(Guid id,ControlReviewRequest input,HttpContext ctx,ControlService control,CancellationToken ct)=>Results.Ok(await control.ResetAsync(id,Actor(ctx),input.Reason,input.Confirmed,ct))).RequireAuthorization("ControlReview");
app.MapPost("/api/controls/{id:guid}/review",async(Guid id,ControlReviewRequest input,HttpContext ctx,ControlService control,CancellationToken ct)=>{await control.ReviewAsync(id,Actor(ctx),input.Reason,input.Confirmed,ct);return Results.Ok(new{message="人工核查已记录，未停止、复位或重发动作"});}).RequireAuthorization("ControlReview");
app.MapGet("/api/acquisition",async(AcquisitionService service,CancellationToken ct)=>Results.Ok(await service.CurrentAsync(ct))).RequireAuthorization();
app.MapPost("/api/acquisition/start",async(HttpContext ctx,AcquisitionService service,CancellationToken ct)=>{await service.BeginAsync(Actor(ctx),ct);return Results.Ok(await service.CurrentAsync(ct));}).RequireAuthorization("AcquisitionManage");
app.MapPost("/api/acquisition/stop",async(HttpContext ctx,AcquisitionService service,ControlService control,CancellationToken ct)=>{await control.HaltAcquisitionAsync(Actor(ctx),ct);return Results.Ok(await service.CurrentAsync(ct));}).RequireAuthorization("AcquisitionManage");
app.MapGet("/api/history",async(HttpRequest request,IHistoryStore repository,CancellationToken ct)=>{
 var q=request.Query;
 long Time(string name){if(!DateTimeOffset.TryParse(q[name],out var value))throw new ArgumentException("请输入有效历史起止时间");return value.ToUnixTimeMilliseconds();}
 Guid? tank=null;if(!string.IsNullOrWhiteSpace(q["tank"])&&q["tank"]!="all"){if(!Guid.TryParse(q["tank"],out var id)||id==Guid.Empty)throw new ArgumentException("历史罐号无效");tank=id;}
 var limit=500;if(!string.IsNullOrWhiteSpace(q["limit"])&&!int.TryParse(q["limit"],out limit))throw new ArgumentException("历史查询数量无效");
 HistoryCursor? after=null;if(!string.IsNullOrWhiteSpace(q["afterAt"])||!string.IsNullOrWhiteSpace(q["afterTank"])){
 if(!long.TryParse(q["afterAt"],out var at)||!long.TryParse(q["afterTank"],out var key))throw new ArgumentException("历史分页位置无效");after=new(at,key);}
 var quantity=TrendQuantity.Level;var quality=HistoryQualityFilter.All;
 if(!string.IsNullOrWhiteSpace(q["quantity"])&&(!Enum.TryParse(q["quantity"],out quantity)||!Enum.IsDefined(quantity)))throw new ArgumentException("历史数据项无效");
 if(!string.IsNullOrWhiteSpace(q["quality"])&&(!Enum.TryParse(q["quality"],out quality)||!Enum.IsDefined(quality)))throw new ArgumentException("历史数据状态无效");
 return Results.Ok(await repository.QueryAsync(new(Time("start"),Time("end"),tank,limit,after,quantity,quality),ct));}).RequireAuthorization("HistoryRead");
app.Use(async(context,next)=>{
 var name=context.Request.Path.Value;
 if(name is "/templates/capacity-main.xlsx" or "/templates/capacity-static-pressure.xlsx" or "/templates/capacity-decimal.xlsx"){
  var file=Path.Combine(app.Environment.WebRootPath??Path.Combine(app.Environment.ContentRootPath,"wwwroot"),"templates",Path.GetFileName(name));
  var bytes=WorkbookBorders.AddToTemplate(await File.ReadAllBytesAsync(file,context.RequestAborted));context.Response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";context.Response.Headers.ContentDisposition="attachment; filename=\""+Path.GetFileName(name)+"\"";context.Response.Headers.CacheControl="no-cache";await context.Response.Body.WriteAsync(bytes,context.RequestAborted);return;
 }
 await next(context);
});
app.UseDefaultFiles();app.UseStaticFiles(new StaticFileOptions{OnPrepareResponse=ctx=>{if(ctx.File.Name.EndsWith(".html",StringComparison.OrdinalIgnoreCase))ctx.Context.Response.Headers.CacheControl="no-cache";}});
app.Map("/api/{**path}",()=>Results.NotFound(new{error="接口尚未开放"}));
if(Directory.Exists(app.Environment.WebRootPath))app.MapFallbackToFile("index.html");
await app.RunAsync();
internal sealed record SetupRequest(string Login,string DisplayName,string Password);
internal sealed record LoginRequest(string Login,string Password);

internal sealed record CapacityAssociationRequest(long ConfigurationRevision,IReadOnlyList<Guid> TankIds,string Action,bool ReplaceExisting=false,bool EnableVolume=true);

internal sealed record TankDisplayRequest(long ConfigurationRevision,string Action);

internal sealed record AlarmAcknowledgeRequest(IReadOnlyList<Guid> Ids);

internal sealed record ControlRequest(long ConfigurationRevision,InstrumentCommand Command,bool Confirmed,decimal? TargetLevelMm=null,string? Reason=null);
internal sealed record ControlReviewRequest(string Reason,bool Confirmed);
internal sealed record GatewayLiveResetRequest(GatewayControlSnapshot Expected,ControlCycle Cycle,string Reason,bool Confirmed,IReadOnlyList<Guid>? ReviewedControlIds=null);

public sealed record WirelessStartRequest(long ConfigurationRevision,int Points,bool Confirmed);
public sealed record WirelessReviewRequest(bool Confirmed,string Reason);

internal sealed record AuthorizationImportRequest(string EnvelopeJson);

internal sealed record RecoveryReviewRequest(string Reason,bool Confirmed);

internal sealed record RecoveryGatewayResetRequest(long ConfigurationRevision,ControlCycle Cycle,string Reason,bool Confirmed);

internal sealed record RetentionChangeRequest(SystemConfiguration Configuration,Guid BackupId,bool Confirmed);

