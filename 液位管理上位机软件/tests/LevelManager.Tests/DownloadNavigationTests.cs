using Naide.LevelManager.Desktop;
using Xunit;
namespace Naide.LevelManager.Tests;
public class DownloadNavigationTests
{
    [Theory][InlineData("/api/capacity-tables/abc/export")][InlineData("/api/data-jobs/abc/download")][InlineData("/templates/capacity-main.xlsx")]
    public void KnownSameOriginFilesAreAttachments(string path)
    {var origin=new Uri("http://127.0.0.1:8780/");Assert.True(DownloadFilePolicy.IsAttachmentNavigation(origin,new Uri(origin,path).AbsoluteUri));Assert.False(DownloadFilePolicy.IsAttachmentNavigation(origin,"http://example.com"+path));}
    [Fact]public void CompletedPageSurvivesDownloadCancellationButRealLoadFailureStillShowsFallback()
    {
        Assert.True(DownloadFilePolicy.KeepDocumentAfterCancellation(true,true,false,false));
        Assert.True(DownloadFilePolicy.KeepDocumentAfterCancellation(true,false,true,true));
        Assert.False(DownloadFilePolicy.KeepDocumentAfterCancellation(false,true,false,true));
        Assert.False(DownloadFilePolicy.KeepDocumentAfterCancellation(true,false,true,false));
        Assert.False(DownloadFilePolicy.KeepDocumentAfterCancellation(true,false,false,true));
    }
}
