using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class RolePermissionTests
{
    private static readonly AuditActor Admin=new("permissions-qa","权限测试",AccountRole.Administrator);
    [Fact]public async Task DefaultsPreserveExistingRolesAndExplicitGrantCanDelegateConfiguration()
    {
        await using var f=new StoreFixture();await f.Initialize();var matrix=await f.Store.GetPermissionsAsync();
        var viewer=new UserAccount("v","viewer","浏览员",AccountRole.Viewer,"opaque",true);
        Assert.False((await f.Store.ResolveAsync(viewer)).Allows(AppPermission.ConfigurationEdit));
        var next=matrix with{Roles=matrix.Roles.Select(g=>g.Role==AccountRole.Viewer?g with{Permissions=g.Permissions.Append(AppPermission.ConfigurationEdit).ToArray()}:g).ToArray()};
        var saved=await f.Store.SavePermissionsAsync(next,Admin);Assert.Equal(1,saved.Revision);
        await f.Store.SaveAsync(Fixtures.Configuration(),await f.Store.ResolveAsync(viewer));
        var backup=Path.Combine(f.Root,"permissions-backup.db");await f.Store.BackupAsync(backup);var reopened=new SqliteConfigurationStore(backup);await reopened.InitializeAsync();
        Assert.True((await reopened.ResolveAsync(viewer)).Allows(AppPermission.ConfigurationEdit));
    }
    [Fact]public async Task RevocationIsLiveAndConflictsCannotOverwriteNewMatrix()
    {
        await using var f=new StoreFixture();await f.Initialize();var old=await f.Store.GetPermissionsAsync();
        var op=new UserAccount("o","operator","操作员",AccountRole.Operator,"opaque",true);
        Assert.True((await f.Store.ResolveAsync(op)).Allows(AppPermission.InstrumentControl));
        var saved=await f.Store.SavePermissionsAsync(old with{Roles=old.Roles.Select(g=>g.Role==AccountRole.Operator?g with{Permissions=g.Permissions.Where(p=>p!=AppPermission.InstrumentControl).ToArray()}:g).ToArray()},Admin);
        Assert.False((await f.Store.ResolveAsync(op)).Allows(AppPermission.InstrumentControl));
        Assert.True((await f.Store.ResolveAsync(op)).Allows(AppPermission.InstrumentStop));
        await Assert.ThrowsAsync<RevisionConflictException>(()=>f.Store.SavePermissionsAsync(old,Admin));Assert.Equal(saved.Revision,(await f.Store.GetPermissionsAsync()).Revision);
        Assert.Contains(await f.Store.QueryEventsAsync(new(Keyword:"岗位权限")),e=>e.Category=="操作日志");
    }
    [Fact]public async Task AuditFailureRollsBackPermissionEdit()
    {
        await using var f=new StoreFixture();await f.Initialize();var old=await f.Store.GetPermissionsAsync();await f.Scalar("CREATE TRIGGER reject_permissions_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'audit unavailable'); END");
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Store.SavePermissionsAsync(old,Admin));Assert.Equal(0,(await f.Store.GetPermissionsAsync()).Revision);
    }
    [Fact]public void MatrixRejectsLockedRightsMissingStopsDuplicatesAndUnknownPermissions()
    {
        var matrix=PermissionPolicy.DefaultMatrix();
        RolePermissionMatrix Replace(AccountRole role,Func<IReadOnlyList<AppPermission>,IReadOnlyList<AppPermission>> change)=>matrix with{Roles=matrix.Roles.Select(g=>g.Role==role?g with{Permissions=change(g.Permissions)}:g).ToArray()};
        Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(Replace(AccountRole.Operator,p=>p.Append(AppPermission.PermissionsManage).ToArray())));
        Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(Replace(AccountRole.Administrator,p=>p.Where(x=>x!=AppPermission.PermissionsManage).ToArray())));
        Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(Replace(AccountRole.Operator,p=>p.Where(x=>x!=AppPermission.InstrumentStop).ToArray())));
        Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(Replace(AccountRole.Viewer,p=>p.Append(AppPermission.HistoryRead).ToArray())));
        Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(Replace(AccountRole.Viewer,p=>p.Append((AppPermission)999).ToArray())));
        Assert.False(new AuditActor("v","浏览员",AccountRole.Viewer,[AppPermission.PermissionsManage]).Allows(AppPermission.PermissionsManage));
    }
    [Fact]public async Task NonAdministratorCannotSaveMatrixEvenWithForgedFunctionalGrant()
    {
        await using var f=new StoreFixture();await f.Initialize();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.SavePermissionsAsync(PermissionPolicy.DefaultMatrix(),new("v","浏览员",AccountRole.Viewer,[AppPermission.PermissionsManage])));
    }
    [Fact]public async Task UpgradeFromFourteenPreservesArchivedValuesAndInitialRoleDefaults()
    {
        await using var f=new StoreFixture();await f.Initialize();var config=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);
        var history=new SqliteHistoryStore(f.PathToDb);await history.AppendAsync([new(){TankId=config.Tanks[0].Id,ConfigurationRevision=config.Revision,AcquisitionSessionId=Guid.NewGuid(),AtUtcMs=1000,LevelMm=Naide.LevelManager.Domain.Measurement.Good(123)}]);
        await f.Scalar("DROP TABLE role_permissions;PRAGMA user_version=14");await f.Initialize();
        Assert.Equal(config.Revision,(await f.Store.GetAsync()).Revision);Assert.Equal(123,Assert.Single((await history.QueryAsync(new(1000,1001))).Rows).LevelMm.Value);Assert.Equal(0,(await f.Store.GetPermissionsAsync()).Revision);
    }
    [Fact]public void QueuedScheduledAuthorityRequiresSamePermissionAndAccountVersion()
    {
        var user=new UserAccount("s","scheduler","定时授权",AccountRole.Operator,"opaque",true,4);
        PermissionPolicy.RequireScheduledAuthority(user,2,2,4);
        Assert.Throws<UnauthorizedAccessException>(()=>PermissionPolicy.RequireScheduledAuthority(user,3,2,4));
        Assert.Throws<UnauthorizedAccessException>(()=>PermissionPolicy.RequireScheduledAuthority(user with{SecurityVersion=5},2,2,4));
        Assert.Throws<UnauthorizedAccessException>(()=>PermissionPolicy.RequireScheduledAuthority(user with{Enabled=false},2,2,4));
        Assert.Throws<UnauthorizedAccessException>(()=>PermissionPolicy.RequireScheduledAuthority(null,2,2,4));
        Assert.Throws<UnauthorizedAccessException>(()=>PermissionPolicy.RequireScheduledAuthority(user,2,2,null));
        PermissionPolicy.RequireScheduledAuthority(user,99,null,null);
    }
}
