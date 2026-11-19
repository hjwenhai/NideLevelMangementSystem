using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Naide.LevelManager.Desktop;

internal sealed class DownloadsWindow : Form
{
    private sealed class SavedDownload
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = "";
        public string State { get; set; } = "InProgress";
        public long Bytes { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    }

    private sealed class TrackedDownload(SavedDownload saved)
    {
        public SavedDownload Saved { get; } = saved;
        public CoreWebView2DownloadOperation? Operation { get; set; }
        public EventHandler<object>? Changed { get; set; }
    }

    private readonly Form owner;
    private readonly Uri origin;
    private readonly string historyPath;
    private readonly List<TrackedDownload> downloads = [];
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, ShowItemToolTips = true, AccessibleName = "下载文件列表" };
    private readonly TextBox location = new() { Dock = DockStyle.Top, Height = 30, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, AccessibleName = "文件保存位置" };
    private readonly Label result = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Color.FromArgb(70, 117, 111), AutoEllipsis = true };
    private readonly Button open = new() { Text = "打开文件", AutoSize = true, Enabled = false, Padding = new(14, 7, 14, 7) };
    private readonly Button folder = new() { Text = "打开文件夹", AutoSize = true, Enabled = false, Padding = new(14, 7, 14, 7) };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 300 };
    private bool dirty;

    public DownloadsWindow(Form owner, Uri origin, string profile)
    {
        this.owner = owner;
        this.origin = origin;
        historyPath = Path.Combine(profile, "download-records.json");
        Text = "下载文件";
        Width = 850; Height = 430; MinimumSize = new(620, 330);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        Font = new("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96);
        BackColor = Color.White;
        list.Columns.Add("文件名称", 350);
        list.Columns.Add("状态", 100);
        list.Columns.Add("大小", 95);
        list.Columns.Add("下载时间", 165);
        var title = new Label { Text = "下载文件", Font = new(Font.FontFamily, 14, FontStyle.Bold), ForeColor = Color.FromArgb(32, 60, 73), Dock = DockStyle.Top, Height = 56, Padding = new(16, 12, 0, 0) };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 126, Padding = new(16, 8, 16, 10) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(open); actions.Controls.Add(folder);
        footer.Controls.Add(result); footer.Controls.Add(location); footer.Controls.Add(actions);
        Controls.Add(list); Controls.Add(title); Controls.Add(footer);
        open.Click += (_, _) => OpenSelected(false);
        folder.Click += (_, _) => OpenSelected(true);
        list.SelectedIndexChanged += (_, _) => SelectionChanged();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        VisibleChanged += (_, _) => { if (Visible) { Rebuild(); SelectionChanged(); } };
        refresh.Tick += (_, _) => { if (!dirty) return; dirty = false; Rebuild(); };
        LoadHistory(); refresh.Start();
    }

    public void ShowDownloads()
    {
        if (!Visible) Show(owner);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront(); Activate();
    }

    public void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!DownloadFilePolicy.TrustedDownload(origin, e.DownloadOperation.Uri)) { e.Cancel = true; return; }
        e.Handled = true;
        var item = new TrackedDownload(new SavedDownload { FilePath = e.ResultFilePath }) { Operation = e.DownloadOperation };
        item.Changed = (_, _) => Update(item);
        downloads.Insert(0, item);
        item.Operation.StateChanged += item.Changed;
        item.Operation.BytesReceivedChanged += item.Changed;
        Update(item); Rebuild(item.Saved.Id);
        // Return from DownloadStarting before showing another native window.
        owner.BeginInvoke(() => { if (!IsDisposed && !owner.IsDisposed) ShowDownloads(); });
    }

    private void Update(TrackedDownload item)
    {
        if (IsDisposed || item.Operation is not { } operation) return;
        item.Saved.FilePath = operation.ResultFilePath;
        item.Saved.Bytes = operation.BytesReceived;
        item.Saved.State = operation.State.ToString();
        dirty = true;
        if (operation.State != CoreWebView2DownloadState.InProgress)
        {
            operation.StateChanged -= item.Changed;
            operation.BytesReceivedChanged -= item.Changed;
            item.Operation = null;
            SaveHistory();
        }
    }

    private void Rebuild(string? select = null)
    {
        select ??= list.SelectedItems.Count > 0 ? list.SelectedItems[0].Name : null;
        list.BeginUpdate(); list.Items.Clear();
        foreach (var item in downloads)
        {
            var s = item.Saved;
            var state = s.State switch { "Completed" => File.Exists(s.FilePath) ? "已完成" : "文件已移走", "InProgress" => "下载中", _ => "未完成" };
            var row = new ListViewItem(Path.GetFileName(s.FilePath)) { Name = s.Id, Tag = item, ToolTipText = s.FilePath };
            row.SubItems.Add(state);
            row.SubItems.Add(s.Bytes >= 1024 * 1024 ? $"{s.Bytes / 1048576d:0.0} MB" : $"{s.Bytes / 1024d:0.0} KB");
            row.SubItems.Add(s.StartedAt.LocalDateTime.ToString("MM-dd HH:mm:ss"));
            list.Items.Add(row);
            if (s.Id == select) row.Selected = true;
        }
        list.EndUpdate();
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
        SelectionChanged();
    }

    private SavedDownload? Selected => list.SelectedItems.Count == 1 ? ((TrackedDownload)list.SelectedItems[0].Tag!).Saved : null;

    private void SelectionChanged()
    {
        var selected = Selected;
        location.Text = selected?.FilePath ?? "导出文件后，可在这里打开文件或定位文件夹。";
        var ready = selected?.State == "Completed" && DownloadFilePolicy.TryExistingFile(selected.FilePath, out _);
        folder.Enabled = ready;
        open.Enabled = ready && DownloadFilePolicy.CanOpenDocument(selected!.FilePath);
    }

    private void OpenSelected(bool reveal)
    {
        result.Text = "";
        var selected = Selected;
        if (selected?.State != "Completed" || !DownloadFilePolicy.TryExistingFile(selected.FilePath, out var file))
        { result.Text = "文件尚未下载完成，或已被移动、删除。请重新下载。"; SelectionChanged(); return; }
        if (!reveal && !DownloadFilePolicy.CanOpenDocument(file))
        { result.Text = "请通过文件夹查看此类型的文件。"; return; }
        try
        {
            var info = reveal
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true, Arguments = $"/select,\"{file}\"" }
                : new ProcessStartInfo(file) { UseShellExecute = true };
            Process.Start(info)?.Dispose();
            result.Text = reveal ? "已打开文件所在文件夹。" : "已请求系统默认程序打开文件。";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { result.Text = "无法打开。请确认文件仍存在，并已安装相应的文件查看软件。"; }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(historyPath) || new FileInfo(historyPath).Length > 1024 * 1024) return;
            var records = JsonSerializer.Deserialize<List<SavedDownload>>(File.ReadAllText(historyPath)) ?? [];
            foreach (var saved in records.Take(100))
            {
                if (string.IsNullOrWhiteSpace(saved.Id) || string.IsNullOrWhiteSpace(saved.FilePath) || !Path.IsPathFullyQualified(saved.FilePath)) continue;
                if (saved.State != "Completed") saved.State = "Interrupted";
                downloads.Add(new(saved));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var temp = historyPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(downloads.Take(100).Select(d => d.Saved)));
            File.Move(temp, historyPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { result.Text = "下载记录未能保存；文件下载不受影响。"; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            refresh.Dispose(); SaveHistory();
            foreach (var item in downloads.Where(d => d.Operation is not null))
            {
                item.Operation!.StateChanged -= item.Changed;
                item.Operation.BytesReceivedChanged -= item.Changed;
            }
        }
        base.Dispose(disposing);
    }
}
