using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Naide.LevelManager.Desktop;
internal static class Program
{
 [STAThread]static void Main(string[] args)
 {
  Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  string? Option(string name){var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null;}
  var config=Path.Combine(AppContext.BaseDirectory,"client.json");var url=Option("--server-url");if(url is null&&File.Exists(config))try{url=JsonDocument.Parse(File.ReadAllText(config)).RootElement.GetProperty("serverUrl").GetString();}catch(Exception e)when(e is IOException or JsonException or KeyNotFoundException){MessageBox.Show("客户端设置无法读取，请联系管理员。","液位管理系统");return;}
  if(!Uri.TryCreate(url??"http://127.0.0.1:8780/",UriKind.Absolute,out var origin)||origin.Scheme is not("http" or "https")||origin.AbsolutePath!="/"||!string.IsNullOrEmpty(origin.UserInfo)||!string.IsNullOrEmpty(origin.Query)||!string.IsNullOrEmpty(origin.Fragment)){MessageBox.Show("客户端服务器地址无效。","液位管理系统");return;}
  var profile=Option("--profile-root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Naide","LevelManager","client");Application.Run(new MainWindow(origin,Path.GetFullPath(profile),Option("--verify-startup")));
 }
}
internal sealed class MainWindow:Form
{
 private readonly Uri origin;private readonly string profile;private readonly string? verification;private readonly WebView2 web=new(){Dock=DockStyle.Fill,Visible=false};private readonly Label status=new(){AutoSize=false,Dock=DockStyle.Top,Height=130,Padding=new(24),Text="正在连接液位管理后台…"};private readonly Button retry=new(){Text="重新连接",Dock=DockStyle.Top,Height=42};private readonly Panel fallback=new(){Dock=DockStyle.Fill};private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(4),MaxResponseContentBufferSize=6*1024*1024};private readonly CancellationTokenSource closing=new();private readonly System.Windows.Forms.Timer brandingTimer=new(){Interval=15000},checkTimer=new(){Interval=30000};private bool connecting,checkedStartup;private Icon? ownedIcon;private readonly Icon? defaultIcon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);private string? currentIconDataUrl;
 public MainWindow(Uri endpoint,string browserProfile,string? check)
 {
  origin=endpoint;profile=browserProfile;verification=check;Text="液位管理系统";Width=1440;Height=940;MinimumSize=new(800,480);AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);StartPosition=FormStartPosition.CenterScreen;Font=new("Microsoft YaHei UI",10);Icon=defaultIcon;fallback.Controls.Add(retry);fallback.Controls.Add(status);Controls.Add(web);Controls.Add(fallback);retry.Click+=async(_,_)=>await ConnectAsync();Shown+=async(_,_)=>await ConnectAsync();brandingTimer.Tick+=async(_,_)=>await RefreshBrandingAsync();checkTimer.Tick+=(_,_)=>FinishCheck(false,"启动或页面加载未在30秒内完成");FormClosed+=(_,_)=>{downloads?.Dispose();closing.Cancel();brandingTimer.Dispose();checkTimer.Dispose();http.Dispose();ownedIcon?.Dispose();defaultIcon?.Dispose();closing.Dispose();};
  if(verification is not null){ShowInTaskbar=false;Opacity=0;checkTimer.Start();}else WindowState=FormWindowState.Maximized;
 }
 private DownloadsWindow? downloads;
 private string? pageRevision;
 private bool documentLoaded;private readonly HashSet<ulong> attachmentNavigations=[];
 private async Task ConnectAsync()
 {
  if(connecting)return;connecting=true;retry.Enabled=false;try
  {
   using var response=await http.GetAsync(new Uri(origin,"api/health"),closing.Token);response.EnsureSuccessStatusCode();var health=await response.Content.ReadFromJsonAsync<JsonElement>(closing.Token);pageRevision=health.TryGetProperty("revision",out var revision)?revision.GetString():null;await RefreshBrandingAsync();
   if(web.CoreWebView2 is null){Directory.CreateDirectory(profile);var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:profile);await web.EnsureCoreWebView2Async(environment);var core=web.CoreWebView2!;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=true;
    downloads=new DownloadsWindow(this,origin,profile);core.DownloadStarting+=downloads.DownloadStarting;
    core.WebMessageReceived+=(_,e)=>{if(!Trusted(e.Source))return;try{if(e.TryGetWebMessageAsString()=="open-downloads")downloads.ShowDownloads();}catch(ArgumentException){}};
    core.IsDefaultDownloadDialogOpenChanged+=(_,_)=>{if(core.IsDefaultDownloadDialogOpen){core.CloseDefaultDownloadDialog();BeginInvoke(()=>downloads.ShowDownloads());}};
    core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;core.NavigationStarting+=(_,e)=>{if(!Trusted(e.Uri))e.Cancel=true;else if(DownloadFilePolicy.IsAttachmentNavigation(origin,e.Uri))attachmentNavigations.Add(e.NavigationId);};core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Trusted(e.Uri))core.Navigate(e.Uri);};core.NavigationCompleted+=(_,e)=>{var attachment=attachmentNavigations.Remove(e.NavigationId);if(e.IsSuccess){documentLoaded=true;fallback.Visible=false;web.Visible=true;FinishCheck(true,"本机页面已加载");}else if(DownloadFilePolicy.KeepDocumentAfterCancellation(documentLoaded,e.WebErrorStatus==CoreWebView2WebErrorStatus.OperationCanceled,e.WebErrorStatus==CoreWebView2WebErrorStatus.ConnectionAborted,attachment)){fallback.Visible=false;web.Visible=true;}else{fallback.Visible=true;web.Visible=false;status.Text="画面加载失败。请确认后台服务与服务器地址，再重新连接。";FinishCheck(false,e.WebErrorStatus.ToString());}};
   }
   web.CoreWebView2!.Navigate(string.IsNullOrWhiteSpace(pageRevision)?origin.AbsoluteUri:new UriBuilder(origin){Query="revision="+Uri.EscapeDataString(pageRevision)}.Uri.AbsoluteUri);brandingTimer.Start();
  }catch(Exception e)when(e is HttpRequestException or TaskCanceledException or WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or JsonException)
  {if(!closing.IsCancellationRequested){status.Text=e is WebView2RuntimeNotFoundException?"缺少Microsoft Edge WebView2运行环境。请运行安装目录中的离线运行环境安装程序，然后重新打开画面。":"后台尚未连接。请确认已安装并启动液位管理服务，再重新连接。关闭本画面不会停止后台服务。";fallback.Visible=true;web.Visible=false;FinishCheck(false,e.GetType().Name);}}
  finally{connecting=false;if(!IsDisposed)retry.Enabled=true;}
 }
 private bool Trusted(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var target)&&target.Scheme==origin.Scheme&&target.Authority==origin.Authority;
 private async Task RefreshBrandingAsync()
 {
  try{var data=await http.GetFromJsonAsync<JsonElement>(new Uri(origin,"api/presentation"),closing.Token);var branding=data.GetProperty("branding");Text=branding.GetProperty("softwareName").GetString()??"液位管理系统";var icon=branding.GetProperty("iconDataUrl");var value=icon.ValueKind==JsonValueKind.String?icon.GetString():null;if(string.IsNullOrEmpty(value)){if(ownedIcon is{} previous){Icon=defaultIcon;ownedIcon=null;previous.Dispose();}currentIconDataUrl=null;}else if(value!=currentIconDataUrl&&value.Length<1500000){var bytes=Convert.FromBase64String(value[(value.IndexOf(',')+1)..]);using var stream=new MemoryStream(bytes);Icon replacement;if(value.StartsWith("data:image/x-icon;",StringComparison.Ordinal)){using var image=new Icon(stream);replacement=(Icon)image.Clone();}else{using var image=new Bitmap(stream);using var resized=new Bitmap(image,new Size(64,64));var handle=resized.GetHicon();try{using var borrowed=Icon.FromHandle(handle);replacement=(Icon)borrowed.Clone();}finally{DestroyIcon(handle);}}var old=ownedIcon;ownedIcon=replacement;Icon=replacement;currentIconDataUrl=value;old?.Dispose();}}
  catch(Exception e)when(e is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException or KeyNotFoundException){}
 }
 private void FinishCheck(bool passed,string message)
 {
  if(verification is null||checkedStartup)return;checkedStartup=true;checkTimer.Stop();File.WriteAllText(verification,JsonSerializer.Serialize(new{passed,message,title=Text,pageUrl=web.Source?.AbsoluteUri,revision=pageRevision,runtime=web.CoreWebView2?.Environment.BrowserVersionString,realDeviceAccess=false}));BeginInvoke(()=>Close());
 }
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool DestroyIcon(IntPtr handle);
}
