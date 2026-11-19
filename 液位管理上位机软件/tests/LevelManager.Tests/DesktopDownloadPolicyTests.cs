using Naide.LevelManager.Desktop;
using Xunit;

namespace Naide.LevelManager.Tests;

public class DesktopDownloadPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8780/api/export", true)]
    [InlineData("blob:http://127.0.0.1:8780/abc", true)]
    [InlineData("data:image/png;base64,AA==", true)]
    [InlineData("data:text/html;base64,AA==", false)]
    [InlineData("blob:http://127.0.0.1:9999/abc", false)]
    [InlineData("https://127.0.0.1:8780/api/export", false)]
    [InlineData("http://127.0.0.1:8781/api/export", false)]
    [InlineData("http://evil.test/api/export", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("http://user@127.0.0.1:8780/api/export", false)]
    public void OnlyApplicationDownloadsAndPngAreAccepted(string uri, bool expected)
        => Assert.Equal(expected, DownloadFilePolicy.TrustedDownload(new("http://127.0.0.1:8780/"), uri));

    [Theory]
    [InlineData("储罐配置.XLSX", true)]
    [InlineData("备份.zip", true)]
    [InlineData("安装授权请求.json", true)]
    [InlineData("趋势曲线.png", true)]
    [InlineData("报表.xlsx.exe", false)]
    [InlineData("文件.lnk", false)]
    [InlineData("文件.url", false)]
    [InlineData("文件.cmd", false)]
    public void ShellOpeningIsLimitedToDocumentTypes(string file, bool expected)
        => Assert.Equal(expected, DownloadFilePolicy.CanOpenDocument(file));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("相对文件.xlsx")]
    [InlineData("https://example.test/a.xlsx")]
    public void MissingAndNonFilePathsCannotBeOpened(string? file)
        => Assert.False(DownloadFilePolicy.TryExistingFile(file, out _));
}
