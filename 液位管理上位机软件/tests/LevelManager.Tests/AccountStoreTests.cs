using Naide.LevelManager.Application;
using Xunit;
namespace Naide.LevelManager.Tests;
public class AccountStoreTests
{
    private static readonly AuditActor Admin=new("a","管理员",AccountRole.Administrator);
    private static async Task Initialize(StoreFixture f){await f.Initialize();await f.Store.CreateInitialAdministratorAsync(new("a","admin","管理员",AccountRole.Administrator,"opaque-initial-hash",true));}
    [Fact]public async Task AccountsNormalizeNamesAndVersionChangesInvalidatePreviousSessions()
    {
        await using var f=new StoreFixture();await Initialize(f);var u=await f.Store.SaveUserAsync(new("b","operator","操作员",AccountRole.Operator,"opaque-password-hash",true),true,Admin);Assert.Equal("OPERATOR",u.Login);u=await f.Store.SaveUserAsync(u with{Role=AccountRole.Viewer},false,Admin);Assert.Equal(1,u.SecurityVersion);Assert.Equal(1,(await f.Store.FindAsync("operator"))!.SecurityVersion);Assert.Equal(AccountRole.Viewer,(await f.Store.FindByIdAsync("b"))!.Role);Assert.Equal(2,(await f.Store.ListUsersAsync()).Count);
        var logs=await f.Store.QueryEventsAsync(new(Keyword:"账号"));Assert.DoesNotContain(logs,l=>l.Details.Contains("opaque-password-hash"));await Assert.ThrowsAsync<RevisionConflictException>(()=>f.Store.SaveUserAsync(u with{SecurityVersion=0},false,Admin));
    }
    [Fact]public async Task LastAdministratorAndOwnRoleCannotBeDisabled()
    {
        await using var f=new StoreFixture();await Initialize(f);var a=(await f.Store.FindByIdAsync("a"))!;await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.SaveUserAsync(a with{Enabled=false},false,Admin));await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.SaveUserAsync(a with{Role=AccountRole.Operator},false,Admin));Assert.True((await f.Store.FindByIdAsync("a"))!.Enabled);
    }
    [Fact]public async Task DuplicateAndViewerManagementAreRejected()
    {
        await using var f=new StoreFixture();await Initialize(f);var candidate=new UserAccount("b","ADMIN","另一个",AccountRole.Operator,"opaque-hash",true);await Assert.ThrowsAsync<ArgumentException>(()=>f.Store.SaveUserAsync(candidate,true,Admin));await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.SaveUserAsync(candidate with{Login="newuser"},true,Admin with{Role=AccountRole.Viewer}));
    }
    [Fact]public async Task AuditFailureRollsBackChangesAndPasswordChangeUsesExpectedVersion()
    {
        await using var f=new StoreFixture();await Initialize(f);var a=(await f.Store.FindByIdAsync("a"))!;await f.Scalar("CREATE TRIGGER reject_account_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");await Assert.ThrowsAnyAsync<Exception>(()=>f.Store.SaveUserAsync(a with{DisplayName="修改"},false,Admin));Assert.Equal(a.DisplayName,(await f.Store.FindByIdAsync("a"))!.DisplayName);await f.Scalar("DROP TRIGGER reject_account_audit");await f.Store.ChangeOwnPasswordAsync("a",0,"new-opaque-hash",Admin);Assert.Equal(1,(await f.Store.FindByIdAsync("a"))!.SecurityVersion);await Assert.ThrowsAsync<RevisionConflictException>(()=>f.Store.ChangeOwnPasswordAsync("a",0,"other-hash",Admin));
    }
}
