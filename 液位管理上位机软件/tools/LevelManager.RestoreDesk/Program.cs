using System.Diagnostics;
using System.Text.Json;
using Naide.LevelManager.Infrastructure;

namespace Naide.LevelManager.RestoreDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); System.Windows.Forms.Application.EnableVisualStyles(); System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        var backupIndex = Array.IndexOf(args, "--backup");
        var selectedBackup = backupIndex >= 0 && backupIndex + 1 < args.Length ? args[backupIndex + 1] : null;
        System.Windows.Forms.Application.Run(new RestoreWindow(RestoreDefaults.FindDataRoot(args), selectedBackup));
    }
}

internal sealed class RestoreWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(31, 61, 73), Teal = Color.FromArgb(23, 135, 124), Muted = Color.FromArgb(101, 126, 136);
    private readonly TextBox backup = Field("backup", true), target = Field("target", true), confirmation = Field("confirmation", false);
    private readonly Label backupSummary = Summary("尚未选择备份。选择后会检查完整性并显示备份时间、储罐和历史记录数量。"), targetSummary = Summary("请选择需要恢复的原业务数据文件夹。"), status = Summary("请先选择备份，再检查恢复目标。"), gate = Summary("");
    private readonly CheckBox stopped = new() { Name = "stopped", Text = "我已确认现场没有正在执行的仪表操作，并已停止后台。", AutoSize = true }, replace = new() { Name = "replace", Text = "我理解当前业务数据将被替换，备份之后新增的记录不会自动合并。", AutoSize = true };
    private readonly Button select = Button("selectBackup", "选择备份文件…"), directory = Button("selectTarget", "更换数据目录…"), inspect = Button("inspect", "重新检查"), restore = Button("restore", "确认并恢复…"), openSafety = Button("openSafety", "打开恢复前副本"), close = Button("close", "关闭"), cancel = Button("cancelInspection", "取消检查");
    private readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 5, Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private BusinessBackupInspection? inspection;
    private BusinessDatabaseOverview? current;
    private CancellationTokenSource? cancellation;
    private bool busy, restoring, restored;
    private string? safety;
    private readonly List<Control> selections = [];

    internal RestoreWindow(string? initialDataRoot, string? initialBackup)
    {
        Text = "液位管理系统 · 业务数据恢复"; Name = "RestoreDesk"; ClientSize = new(1050, 720); MinimumSize = new(920, 700); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96); Font = new("Microsoft YaHei UI", 10); ForeColor = Ink; BackColor = Color.FromArgb(244, 248, 248);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Ink, Padding = new(24, 12, 24, 10) };
        header.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 26, Text = "选择备份 → 核对内容与目标 → 确认恢复   ·   恢复前自动保留原数据副本", ForeColor = Color.FromArgb(190, 219, 217) });
        header.Controls.Add(new Label { Dock = DockStyle.Top, Height = 30, Text = "业务数据恢复", ForeColor = Color.White, Font = new(Font.FontFamily, 17, FontStyle.Bold) });
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 94, Padding = new(22, 13, 22, 12), ColumnCount = 4 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); foreach (var width in new[] { 165, 175, 92 }) footer.ColumnStyles.Add(new(SizeType.Absolute, width));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(openSafety, 1, 0); footer.Controls.Add(restore, 2, 0); footer.Controls.Add(close, 3, 0);
        restore.BackColor = Teal; restore.ForeColor = Color.White; openSafety.Visible = false;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(22, 14, 22, 6) };
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(0) }; body.ColumnStyles.Add(new(SizeType.Percent, 100));
        body.Controls.Add(Section("01  选择业务备份", "使用系统“创建业务备份”下载的完整ZIP，无需解压。"));
        body.Controls.Add(PathRow(backup, select)); body.Controls.Add(backupSummary);
        body.Controls.Add(Section("02  核对恢复目标", "这是被恢复的数据目录。请确认属于当前这套软件，不能选择备份文件夹或程序文件夹。"));
        body.Controls.Add(PathRow(target, directory)); body.Controls.Add(targetSummary);
        var gateRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = new(0, 4, 0, 9) }; gateRow.ColumnStyles.Add(new(SizeType.Percent, 100)); gateRow.ColumnStyles.Add(new(SizeType.Absolute, 120)); gateRow.ColumnStyles.Add(new(SizeType.Absolute, 120));
        gateRow.Controls.Add(gate, 0, 0); gateRow.Controls.Add(inspect, 1, 0); gateRow.Controls.Add(cancel, 2, 0); body.Controls.Add(gateRow); cancel.Visible = false;
        body.Controls.Add(Section("03  确认替换业务数据", "恢复包括配置、账号、历史、报警/事件和测温记录；授权期限不改变，恢复后不会自动采集或重跑仪表动作。"));
        body.Controls.Add(stopped); body.Controls.Add(replace);
        var confirmRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new(0, 11, 0, 8) }; confirmRow.ColumnStyles.Add(new(SizeType.Absolute, 240)); confirmRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        confirmRow.Controls.Add(new Label { AutoSize = true, Text = "请在右侧输入“恢复”：", Padding = new(0, 8, 0, 0) }, 0, 0); confirmRow.Controls.Add(confirmation, 1, 0); body.Controls.Add(confirmRow);
        scroll.Controls.Add(body); Controls.Add(scroll); Controls.Add(footer); Controls.Add(progress); Controls.Add(header);
        selections.AddRange([select, directory, inspect, stopped, replace, confirmation]);
        if (initialDataRoot is not null) target.Text = initialDataRoot;
        if (initialBackup is not null) backup.Text = initialBackup;
        backup.AccessibleName = "已选择的业务备份文件"; target.AccessibleName = "恢复目标数据目录"; confirmation.AccessibleName = "输入恢复以确认";
        select.Click += async (_, _) => { using var dialog = new OpenFileDialog { Title = "选择业务数据备份ZIP", Filter = "业务备份ZIP|*.zip", CheckFileExists = true, Multiselect = false }; if (dialog.ShowDialog(this) != DialogResult.OK) return; backup.Text = dialog.FileName; await InspectAsync(); };
        directory.Click += async (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择原业务数据目录：其中应包含level-manager.db", UseDescriptionForTitle = true, ShowNewFolderButton = false }; if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text; if (dialog.ShowDialog(this) != DialogResult.OK) return; target.Text = dialog.SelectedPath; await InspectAsync(); };
        inspect.Click += async (_, _) => await InspectAsync(); cancel.Click += (_, _) => cancellation?.Cancel(); close.Click += (_, _) => Close();
        stopped.CheckedChanged += (_, _) => RefreshGate(); replace.CheckedChanged += (_, _) => RefreshGate(); confirmation.TextChanged += (_, _) => RefreshGate();
        restore.Click += async (_, _) => await RestoreAsync(); openSafety.Click += (_, _) => { if (safety is not null && Directory.Exists(safety)) Process.Start(new ProcessStartInfo(safety) { UseShellExecute = true }); };
        refresh.Tick += (_, _) => RefreshGate(); refresh.Start(); Shown += async (_, _) => await InspectAsync();
        FormClosing += (_, e) => { if (restoring) { e.Cancel = true; MessageBox.Show(this, "恢复正在进行，请等待完成。不要强行关闭工具或断电。", "正在恢复", MessageBoxButtons.OK, MessageBoxIcon.Information); } else if (busy) { cancellation?.Cancel(); e.Cancel = true; status.Text = "正在取消检查，请稍候再关闭。"; } };
        FormClosed += (_, _) => { refresh.Dispose(); inspection?.Dispose(); cancellation?.Dispose(); };
        RefreshGate();
    }

    private async Task InspectAsync()
    {
        if (busy || restored) return;
        inspection?.Dispose(); inspection = null; current = null; ResetConfirmation();
        SetBusy(true, false); cancellation = new CancellationTokenSource();
        var reporter = new Progress<string>(text => status.Text = text);
        var selectedBackup = backup.Text; var selectedTarget = target.Text;
        try
        {
            if (!string.IsNullOrWhiteSpace(selectedTarget))
            {
                status.Text = "正在读取恢复目标的数据摘要…";
                current = await Task.Run(() => BusinessBackupInspection.InspectTargetAsync(selectedTarget, cancellation.Token));
                targetSummary.Text = current.SummaryAvailable
                    ? $"当前数据：{current.Tanks:N0} 个储罐  ·  {current.Accounts:N0} 个账号  ·  {current.HistoryRows:N0} 条历史记录\n以上当前业务数据将按上方选定的备份进行替换。"
                    : "原业务数据库无法正常读取，暂时无法统计。请再次核对目标目录；恢复前仍会保留原文件副本。";
                targetSummary.ForeColor = current.SummaryAvailable ? Muted : Color.FromArgb(174, 87, 48);
            }
            else targetSummary.Text = "未检测到原业务数据。请点“更换数据目录”选择已有数据文件夹。";
            if (!string.IsNullOrWhiteSpace(selectedBackup))
            {
                inspection = await Task.Run(() => BusinessBackupInspection.OpenAsync(selectedBackup, reporter, cancellation.Token));
                var v = inspection.Overview;
                backupSummary.Text = $"检查通过  ·  备份时间：{inspection.Manifest.SnapshotAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{v.Tanks:N0} 个储罐  ·  {v.Accounts:N0} 个账号  ·  {v.HistoryRows:N0} 条历史记录  ·  数据大小 {inspection.Manifest.Bytes / 1024d / 1024d:N1} MB";
                status.Text = "备份检查通过。请核对目标目录和替换影响，再勾选确认。";
            }
            else status.Text = "请选择从管理系统下载的业务备份ZIP。";
        }
        catch (OperationCanceledException) { inspection?.Dispose(); inspection = null; status.Text = "已取消检查，当前数据未更改。"; }
        catch (Exception e) { inspection?.Dispose(); inspection = null; status.Text = "检查未通过，当前数据未更改。"; backupSummary.Text = "尚未完成有效检查，不能恢复。"; MessageBox.Show(this, Friendly(e), "无法恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false, false); }
    }

    private async Task RestoreAsync()
    {
        RefreshGate(); if (!restore.Enabled || inspection is null) return;
        var checkedBackup = inspection; var destination = target.Text;
        var answer = MessageBox.Show(this, $"请最后核对：\n\n备份：{Path.GetFileName(checkedBackup.BackupPath)}\n备份时间：{checkedBackup.Manifest.SnapshotAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n恢复目标：{destination}\n\n当前配置、账号、历史等将替换为备份时的数据；之后新增记录不会合并。恢复前会保留原数据副本。\n\n确认恢复到上述目录吗？", "最后确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        SetBusy(true, true); status.Text = "正在复核备份、保留原数据并执行恢复，请勿关闭或断电…";
        try
        {
            var result = await Task.Run(() => checkedBackup.RestoreAsync(destination, true));
            restored = true; safety = result.SafetyDirectory; openSafety.Visible = true; ResetConfirmation();
            status.Text = "恢复完成。请重新启动后台、重新登录，并完成现场核查。";
            gate.Text = "恢复成功；本工具不会自动启动采集或仪表动作。"; gate.ForeColor = Teal;
            WriteRecord(destination, checkedBackup, result);
            MessageBox.Show(this, $"业务数据恢复完成。\n\n原数据副本保存在：\n{safety}\n\n下一步：\n1. 重新启动后台和客户端；使用备份时的账号、密码登录。\n2. 在“数据库维护”完成恢复后的现场核查。\n3. 核对实际设备后，手动开启采集。\n\n软件授权期限不变，原仪表动作与测温任务不会自动重跑。", "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) { status.Text = "恢复未完成。请查看提示，保留原数据及恢复记录。"; ResetConfirmation(); MessageBox.Show(this, Friendly(e) + "\n\n请勿手工删除数据库或授权文件夹。", "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { restoring = false; inspection?.Dispose(); inspection = null; SetBusy(false, false); }
    }

    private static void WriteRecord(string destination, BusinessBackupInspection backup, DatabaseRestoreResult result)
    {
        // Supplemental record only; failure must never misreport a successful database restore as failed.
        try { File.WriteAllText(Path.Combine(result.SafetyDirectory, "desktop-restore.json"), JsonSerializer.Serialize(new { restoredAtUtc = DateTimeOffset.UtcNow, destination, backupFile = Path.GetFileName(backup.BackupPath), backup.ArchiveSha256, backupTime = backup.Manifest.SnapshotAtUtc, result.RestoreId, source = "RestoreDesk" }, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private void RefreshGate()
    {
        if (busy || restored) { restore.Enabled = false; return; }
        var block = BusinessBackupInspection.TargetBlockReason(target.Text);
        gate.Text = block ?? (current is not null ? "后台占用检查通过。确认后还会再次检查，避免运行中覆盖数据。" : "请先检查恢复目标。");
        gate.ForeColor = block is null ? Teal : Color.FromArgb(174, 87, 48);
        restore.Enabled = inspection is not null && current is not null && block is null && stopped.Checked && replace.Checked && confirmation.Text.Trim() == "恢复";
        restore.BackColor = restore.Enabled ? Teal : Color.FromArgb(222, 231, 232);
        restore.ForeColor = restore.Enabled ? Color.White : Muted;
    }
    private void SetBusy(bool value, bool isRestoring)
    {
        busy = value; restoring = isRestoring; progress.Visible = value; foreach (var control in selections) control.Enabled = !value && !restored;
        cancel.Visible = value && !isRestoring; close.Enabled = !isRestoring; restore.Enabled = false; if (!value) RefreshGate();
    }
    private void ResetConfirmation() { stopped.Checked = false; replace.Checked = false; confirmation.Clear(); }
    private static string Friendly(Exception e) => e switch { UnauthorizedAccessException => "没有文件访问权限。请确认文件夹权限，必要时右键“以管理员身份运行”本工具。", JsonException => "备份说明或配置无法识别，请选择本系统生成的原始业务备份ZIP。", _ => e.Message };
    private static TextBox Field(string name, bool readOnly) => new() { Name = name, ReadOnly = readOnly, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new(0, 6, 8, 6) };
    private static Label Summary(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, MaximumSize = new(950, 0), ForeColor = Muted, Padding = new(0, 6, 0, 6), Margin = new(0, 0, 0, 3) };
    private static Button Button(string name, string text) => new() { Name = name, Text = text, Dock = DockStyle.Fill, MinimumSize = new(80, 38), FlatStyle = FlatStyle.Flat, Margin = new(4, 3, 4, 3) };
    private static Control Section(string title, string note)
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new(0, 5, 0, 4) };
        box.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Ink, Font = new("Microsoft YaHei UI", 11, FontStyle.Bold), Padding = new(0, 6, 0, 1) }); box.Controls.Add(Summary(note)); return box;
    }
    private static Control PathRow(Control field, Control button)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, MinimumSize = new(0, 48) }; row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 170)); row.Controls.Add(field, 0, 0); row.Controls.Add(button, 1, 0); return row;
    }
}
