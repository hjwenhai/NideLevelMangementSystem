using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.LicenseDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var verification = args.Length == 2 && args[0] == "--verify-ui" ? Path.GetFullPath(args[1]) : null;
        Application.Run(new LicenseWindow(verification));
    }
}

internal sealed class LicenseWindow : Form
{
    private readonly Color ink = Color.FromArgb(33, 61, 73), teal = Color.FromArgb(23, 135, 124);
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new(20, 9) };
    private readonly TextBox requestFile = TextField("requestFile", true), installation = TextField("installation", true), customer = TextField("customer"), privateKey = TextField("privateKey", true);
    private readonly NumericUpDown sequence = new() { Name = "sequence", Minimum = 1, Maximum = long.MaxValue, Value = 1, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private readonly ComboBox term = new() { Name = "term", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker expiry = new() { Name = "expiry", Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Enabled = false };
    private readonly CheckBox average = new() { Name = "average", Text = "开通无线浮子平均温度测量", AutoSize = true, Padding = new(0, 5, 0, 5) };
    private readonly Dictionary<InstrumentCommand,CheckBox> operations=Issuance.OperationNames.ToDictionary(p=>p.Key,p=>new CheckBox{Text=p.Value,AutoSize=true,Padding=new(0,5,12,5)});
    private readonly TextBox software = TextField("software"), manufacturer = TextField("manufacturer"), information = TextField("information"), logo = TextField("logo", true), iconFile = TextField("iconFile", true);
    private readonly TextBox summary = TextField("summary", true);
    private readonly ListView history = new() { Name = "history", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "先选择客户安装请求。授权期限和开通模块由你方明确选择。", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button issue = new() { Name = "issue", Text = "核对并签发授权…", Width = 185, Height = 44, FlatStyle = FlatStyle.Flat };
    private readonly string records = Path.Combine(AppContext.BaseDirectory, "签发记录");
    private readonly string? verification;
    private InstallationRequest? request;
    private long previousSequence;
    private string? logoData, iconData;
    private bool dirty, suppressChanges;

    internal LicenseWindow(string? verification)
    {
        this.verification = verification;
        Text = "液位管理系统 · 供方授权签发工具"; Name = "LicenseDesk";
        Width = 1080; Height = 860; MinimumSize = new(900, 720); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); Font = new("Microsoft YaHei UI", 10); ForeColor = ink; BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var header = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = ink, Padding = new(24, 14, 24, 12) };
        header.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(190, 216, 219), Text = "供方内部使用  ·  离线签发  ·  私钥不会写入授权文件", TextAlign = ContentAlignment.BottomLeft });
        header.Controls.Add(new Label { Dock = DockStyle.Top, Height = 33, ForeColor = Color.White, Font = new(Font, FontStyle.Bold), Text = "液位管理系统  /  授权签发" });
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 76, Padding = new(22, 14, 22, 14), ColumnCount = 2 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 195));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(issue, 1, 0); issue.BackColor = teal; issue.ForeColor = Color.White; issue.FlatAppearance.BorderSize = 0;
        Controls.Add(tabs); Controls.Add(footer); Controls.Add(header);
        var signing = Page("1  签发授权"); var brand = Page("2  品牌资料"); var recordTab = Page("3  签发记录");
        var form = Grid(signing);
        Row(form, "安装请求", requestFile, Button("选择请求…", SelectRequest));
        Row(form, "安装编号", installation);
        Row(form, "续期 / 换授权", new Label { AutoSize = true, Text = "续期请载入客户当前最新授权，保留原模块和品牌，并递增序号。", Padding = new(0, 6, 0, 0) }, Button("载入旧授权…", SelectPrevious));
        Row(form, "客户名称 *", customer);
        Row(form, "本次授权序号 *", sequence);
        term.Items.AddRange(["请选择授权期限…", "按日期到期", "永久授权"]); term.SelectedIndex = 0;
        Row(form, "授权期限 *", term);
        expiry.Value = DateTime.SpecifyKind(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date.AddDays(1).AddSeconds(-1), DateTimeKind.Unspecified);
        Row(form, "到期时刻（北京时间）", expiry);
        Row(form, "功能模块", average);
        var operationPanel=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};foreach(var option in operations.Values){operationPanel.Controls.Add(option);option.CheckedChanged+=(_,_)=>MarkChanged();}
        Row(form,"选配仪表操作",operationPanel);
        Note(form,"测量Ⅰ、停止、上升、重复性检查、液体震荡回避为基本操作（受设备协议能力限制）。上述选配操作仅在客户导入本次签名授权后显示。r22不能使用包含新增选配操作模块的授权，请先升级r23。");
        Row(form, "供方签名私钥", privateKey, Button("选择私钥…", SelectKey));
        Note(form, "期限按北京时间 UTC+08:00 签发，精确到秒。初次签发序号为1；续期须高于客户已导入的序号。\r\n本地记录只能检查本工具留存的历史；其他电脑签发过的授权，请先载入最新授权。ATS实时平均不受无线浮子模块开关影响。");
        summary.Multiline = true; summary.ScrollBars = ScrollBars.Vertical; summary.Height = 160;
        Row(form, "签发摘要", summary, Button("更新摘要", () => Guard(RefreshSummary)));
        var branding = Grid(brand); software.Text = "液位管理系统";
        Row(branding, "软件名称 *", software); Row(branding, "厂家名称", manufacturer);
        information.Multiline = true; information.Height = 95; information.ScrollBars = ScrollBars.Vertical;
        Row(branding, "厂家简介 / 联系方式", information);
        Row(branding, "厂家Logo", logo, Button("选择图像…", () => SelectImage(false)));
        Row(branding, "", Button("移除厂家Logo", () => { logoData = null; logo.Text = "未设置"; MarkChanged(); }));
        Row(branding, "程序图标", iconFile, Button("选择图标…", () => SelectImage(true)));
        Row(branding, "", Button("恢复默认图标", () => { iconData = null; iconFile.Text = "默认图标"; MarkChanged(); }));
        Note(branding, "Logo支持PNG/JPEG（最大1MB、4096×4096）；程序图标另支持ICO（最大1MB、1024×1024）。\r\n品牌资料写入签名授权，客户无法在软件中自行修改。续期载入旧授权后会保留原图像；更改品牌也需重新签发。\r\n导入后客户端窗口与任务栏图标随品牌生效；已安装的桌面快捷方式可由供方运行随附图标同步脚本更新。");
        logo.Text = "未设置"; iconFile.Text = "默认图标";
        var recordButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 53, Padding = new(4) };
        recordButtons.Controls.Add(Button("刷新记录", () => Guard(RefreshHistory))); recordButtons.Controls.Add(Button("打开记录文件夹", () => Guard(() => OpenDirectory(records))));
        recordButtons.Controls.Add(Button("载入所选记录续期", LoadSelected));
        history.Columns.Add("签发时间（北京时间）", 180); history.Columns.Add("客户", 170); history.Columns.Add("序号", 65); history.Columns.Add("期限", 180); history.Columns.Add("无线平均测温", 115);
        history.DoubleClick += (_, _) => LoadSelected();
        var recordNote = new Label { Dock = DockStyle.Bottom, Height = 75, Text = "记录为已签名授权文件，可复制给对应客户。不要编辑或删除原记录。\r\n记录不含私钥；私钥需在你方另行安全备份。本工具及其整个目录不要交客户。", Padding = new(8), AutoEllipsis = true };
        recordTab.Controls.Add(history); recordTab.Controls.Add(recordButtons); recordTab.Controls.Add(recordNote);
        issue.Click += (_, _) => Guard(Issue);
        term.SelectedIndexChanged += (_, _) => { expiry.Enabled = term.SelectedIndex == 1; MarkChanged(); };
        foreach (var input in new Control[] { customer, software, manufacturer, information }) input.TextChanged += (_, _) => MarkChanged();
        average.CheckedChanged += (_, _) => MarkChanged(); sequence.ValueChanged += (_, _) => MarkChanged(); expiry.ValueChanged += (_, _) => MarkChanged();
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 2) Guard(RefreshHistory); };
        privateKey.Text = FindKey() ?? "请选择本项目现有私钥（不要重新生成密钥）";
        FormClosing += (_, e) => { if (verification is null && dirty && MessageBox.Show(this, "尚有未签发的设置，确定关闭？", "关闭工具", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true; };
        Shown += (_, _) => { if (verification is not null) BeginInvoke(() => VerifyUi(verification)); };
    }

    private TabPage Page(string title) { var page = new TabPage(title) { Padding = new(18), BackColor = Color.White, AutoScroll = true }; tabs.TabPages.Add(page); return page; }
    private static TextBox TextField(string name, bool readOnly = false) => new() { Name = name, Dock = DockStyle.Fill, ReadOnly = readOnly, BorderStyle = BorderStyle.FixedSingle, Margin = new(6), BackColor = readOnly ? Color.FromArgb(243, 247, 247) : Color.White };
    private static TableLayoutPanel Grid(Control parent)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new(0, 5, 0, 8) };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 168)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 140)); parent.Controls.Add(grid); return grid;
    }
    private static void Row(TableLayoutPanel grid, string caption, Control control, Control? action = null)
    {
        var row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new(3, 13, 3, 9) }, 0, row);
        control.Margin = new(6, 8, 6, 8); grid.Controls.Add(control, 1, row);
        if (action is null) grid.SetColumnSpan(control, 2); else { action.Margin = new(6, 7, 0, 7); action.Dock = DockStyle.Fill; grid.Controls.Add(action, 2, row); }
    }
    private static void Note(TableLayoutPanel grid, string text) { var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new(3, 10, 3, 16), ForeColor = Color.FromArgb(100, 124, 132), MaximumSize = new(850, 0) }; var row = grid.RowCount++; grid.Controls.Add(label, 0, row); grid.SetColumnSpan(label, 3); }
    private static Button Button(string caption, Action action) { var button = new Button { Text = caption, AutoSize = true, MinimumSize = new(122, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.White }; button.FlatAppearance.BorderColor = Color.FromArgb(204, 220, 224); button.Click += (_, _) => action(); return button; }
    private void MarkChanged() { if (suppressChanges) return; dirty = true; summary.Clear(); status.Text = "设置已改变，请核对摘要后签发。尚未生成客户授权。"; }
    private void Guard(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or LicenseValidationException or System.Security.Cryptography.CryptographicException or OverflowException or FormatException) { MessageBox.Show(this, e.Message, "未完成，请检查", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    private string? Pick(string title, string filter) { using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true }; return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null; }
    private void SelectRequest() { var path = Pick("选择客户安装授权请求", "安装请求 (*.json)|*.json"); if (path is not null) Guard(() => LoadRequest(path)); }
    private void LoadRequest(string path)
    {
        var next = Issuance.ReadRequest(path);
        var known = Issuance.HighestSequence(records, next.InstallationId);
        suppressChanges = true;
        // A new target starts clean, so another customer's brand or modules cannot leak into the next license.
        request = next; requestFile.Text = path; installation.Text = next.InstallationId; previousSequence = known;
        customer.Clear(); term.SelectedIndex = 0; expiry.Enabled = false; average.Checked = false;foreach(var option in operations.Values)option.Checked=false;
        software.Text = "液位管理系统"; manufacturer.Clear(); information.Clear(); logoData = iconData = null; logo.Text = "未设置"; iconFile.Text = "默认图标";
        sequence.Value = checked(known + 1); suppressChanges = false; MarkChanged();
        status.Text = "已读取安装请求。" + (known > 0 ? "本地发现历史授权，请载入最新记录保留品牌及模块。" : "请填写客户、期限及模块；如曾在别处签发，请载入最新授权。");
    }
    private void SelectPrevious() { if (request is null) { Guard(() => throw new ArgumentException("请先选择客户安装请求。")); return; } var path = Pick("选择该电脑最新的签名授权", "签名授权 (*.json)|*.json"); if (path is not null) Guard(() => LoadPrevious(path)); }
    private void LoadPrevious(string path)
    {
        if (request is null) throw new ArgumentException("请先选择对应客户安装请求，再载入旧授权。");
        var old = Issuance.ReadLicense(path, request.InstallationId);
        var prior = Math.Max(previousSequence, old.Sequence); var next = checked(prior + 1);
        suppressChanges = true; previousSequence = prior; sequence.Value = next; customer.Text = old.CustomerName;
        term.SelectedIndex = old.ExpiresAtUtc.HasValue ? 1 : 2;
        if (old.ExpiresAtUtc is {} end) expiry.Value = end.ToOffset(TimeSpan.FromHours(8)).DateTime;
        average.Checked = old.EnabledModules.Contains(ProductTrust.AverageModule);foreach(var option in operations)option.Value.Checked=old.EnabledModules.Contains(ProductTrust.OptionalOperations[option.Key]);
        software.Text = old.Branding.SoftwareName; manufacturer.Text = old.Branding.ManufacturerName; information.Text = old.Branding.ManufacturerInformation;
        logoData = old.Branding.LogoDataUrl; iconData = old.Branding.IconDataUrl; logo.Text = logoData is null ? "未设置" : "保留原授权Logo"; iconFile.Text = iconData is null ? "默认图标" : "保留原授权图标";
        suppressChanges = false; MarkChanged(); status.Text = "已保留原授权品牌和模块。请调整期限，并确认本次完整授权。";
    }
    private void SelectKey() { var path = Pick("选择本项目既有的供方私钥（不生成新密钥）", "PEM私钥 (*.pem)|*.pem"); if (path is not null) { privateKey.Text = path; MarkChanged(); } }
    private void SelectImage(bool isIcon) { var path = Pick(isIcon ? "选择程序图标" : "选择厂家Logo", isIcon ? "图像 (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico" : "图像 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"); if (path is null) return; Guard(() => { var data = Issuance.ReadImage(path, isIcon); if (isIcon) { iconData = data; iconFile.Text = path; } else { logoData = data; logo.Text = path; } MarkChanged(); }); }
    private ProductLicense Candidate() => Issuance.Build(request ?? throw new ArgumentException("请先选择安装请求。"), customer.Text, decimal.ToInt64(sequence.Value), term.SelectedIndex switch { 1 => false, 2 => true, _ => (bool?)null }, expiry.Value, average.Checked,
        new() { SoftwareName = software.Text.Trim(), ManufacturerName = manufacturer.Text.Trim(), ManufacturerInformation = information.Text.Trim(), LogoDataUrl = logoData, IconDataUrl = iconData }, DateTimeOffset.UtcNow, previousSequence,operations.Where(p=>p.Value.Checked).Select(p=>p.Key).ToArray());
    private void RefreshSummary() { summary.Text = Issuance.Summary(Candidate()); status.Text = "摘要已更新。核对客户、安装编号、期限和模块，再签发。"; }
    private void Issue()
    {
        var candidate = Candidate(); summary.Text = Issuance.Summary(candidate);
        if (!File.Exists(privateKey.Text)) throw new ArgumentException("请先选择本项目现有供方私钥。");
        using var dialog = new SaveFileDialog { Title = "选择发给客户的授权文件保存位置", Filter = "签名授权 (*.json)|*.json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true, FileName = "液位管理系统-授权_序号" + candidate.Sequence + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) throw new IOException("为保留历史，不覆盖已有授权文件，请另选文件名。");
        if (MessageBox.Show(this, summary.Text + "\r\n\r\n签发并保存到：" + dialog.FileName + "\r\n\r\n确定签发这份完整授权？", "签发前核对", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        issue.Enabled = false;
        try
        {
            // Rebuild at confirmation time so an expiry that passed while the dialog was open is rejected.
            candidate = Candidate(); var result = Issuance.Issue(candidate, privateKey.Text, dialog.FileName, records);
            previousSequence = candidate.Sequence; sequence.Value = checked(candidate.Sequence + 1); dirty = false;
            status.Text = result.ExportError is null ? "授权已签发并留存记录。只将生成的客户授权JSON文件交给客户。" : "授权已签发留存；客户文件导出失败，请到记录文件夹复制。";
            MessageBox.Show(this, (result.ExportError ?? "客户授权文件：\r\n" + result.ExportPath) + "\r\n\r\n供方留存：\r\n" + result.RecordPath + "\r\n\r\n客户进入“系统管理 → 软件授权”导入。不要发送私钥或工具目录。", "签发结果", MessageBoxButtons.OK, result.ExportError is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally { issue.Enabled = true; }
    }
    private void RefreshHistory()
    {
        history.Items.Clear(); if (!Directory.Exists(records)) return;
        foreach (var path in Directory.EnumerateFiles(records, "*.license.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileName(path); var id = name.Split('_')[0];
            try
            {
                var old = Issuance.ReadLicense(path, id); var item = new ListViewItem(old.IssuedAtUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) { Tag = path };
                item.SubItems.AddRange([old.CustomerName, old.Sequence.ToString(CultureInfo.InvariantCulture), old.ExpiresAtUtc?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "永久", old.EnabledModules.Contains(ProductTrust.AverageModule) ? "开通" : "不开通"]); history.Items.Add(item);
            }
            catch (Exception e) when (e is LicenseValidationException or JsonException or IOException) { history.Items.Add(new ListViewItem("记录损坏，请检查：" + name) { ForeColor = Color.Firebrick }); }
        }
    }
    private void LoadSelected() { if (history.SelectedItems.Count != 1 || history.SelectedItems[0].Tag is not string path) return; Guard(() => { LoadPrevious(path); tabs.SelectedIndex = 0; }); }
    private static void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
    private static string? FindKey()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { var path = Path.Combine(directory.FullName, ".build-tools", "vendor-keys", "level-manager-signing.pem"); if (File.Exists(path)) return path; }
        return null;
    }
    private void VerifyUi(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            var sample = Path.Combine(directory, "ui-only-request.json");
            File.WriteAllText(sample, JsonSerializer.Serialize(new { productId = ProductTrust.ProductId, installationId = new string('F', 64), softwareVersion = "r12 UI验看（不签发）" }));
            LoadRequest(sample); customer.Text = "画面验看示例（未签发）"; term.SelectedIndex = 1; expiry.Value = DateTime.UtcNow.AddYears(1); average.Checked = true;
            manufacturer.Text = "耐德东京计装"; RefreshSummary();
            var captures = new List<string>();
            for (var i = 0; i < tabs.TabCount; i++)
            { tabs.SelectedIndex = i; PerformLayout(); tabs.SelectedTab!.PerformLayout(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new(0, 0, Width, Height)); var path = Path.Combine(directory, "page-" + i + ".png"); bitmap.Save(path); captures.Add(path); }
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = true, title = Text, size = new { Width, Height }, dpi = DeviceDpi, captures, signingPerformed = false, privateKeyRead = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = false, error = e.Message })); }
        finally { dirty = false; Close(); }
    }
}
