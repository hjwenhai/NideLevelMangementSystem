using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Infrastructure;
public sealed partial class SqliteConfigurationStore
{
    private static UserAccount Account(UserRow u)=>new(u.Id,u.Login,u.DisplayName,(AccountRole)u.Role,u.PasswordHash,u.Enabled,u.SecurityVersion);
    public async Task<UserAccount?> FindByIdAsync(string id,CancellationToken ct=default)
    {await using var c=await OpenAsync(ct);await using var db=Context(c);var u=await db.Users.AsNoTracking().FirstOrDefaultAsync(u=>u.Id==id,ct);return u is null?null:Account(u);}
    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken ct=default)
    {await using var c=await OpenAsync(ct);await using var db=Context(c);return (await db.Users.AsNoTracking().OrderBy(u=>u.Login).Take(200).ToListAsync(ct)).Select(Account).ToArray();}
    private static object Public(UserAccount a)=>new{a.Id,a.Login,a.DisplayName,a.Role,a.Enabled,a.SecurityVersion};
    public async Task<UserAccount> SaveUserAsync(UserAccount account,bool create,AuditActor actor,CancellationToken ct=default)
    {
        if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("无账号管理权限");
        if(string.IsNullOrWhiteSpace(account.Id)||account.Login is null||!Regex.IsMatch(account.Login,"^[A-Za-z0-9_-]{3,60}$")||string.IsNullOrWhiteSpace(account.DisplayName)||account.DisplayName.Length>80||!Enum.IsDefined(account.Role)||string.IsNullOrWhiteSpace(account.PasswordHash))throw new ArgumentException("账号、姓名、角色或密码无效");
        await using var c=await OpenAsync(ct);await using var db=Context(c);await using var tx=await db.Database.BeginTransactionAsync(ct);var row=await db.Users.FirstOrDefaultAsync(u=>u.Id==account.Id,ct);UserAccount? before=null;
        if(create)
        {
            if(row is not null||await db.Users.CountAsync(ct)>=200)throw new InvalidOperationException("账号已存在或已达到200个账号上限");if(account.SecurityVersion!=0)throw new ArgumentException("新账号版本无效");row=new(){Id=account.Id};db.Users.Add(row);
        }
        else
        {
            if(row is null)throw new ArgumentException("原账号不存在");before=Account(row);if(before.SecurityVersion!=account.SecurityVersion)throw new RevisionConflictException();
            if(before.Login!=account.Login.ToUpperInvariant())throw new ArgumentException("账号登录名不能修改");
            if(actor.AccountId==account.Id&&(!account.Enabled||account.Role!=AccountRole.Administrator))throw new InvalidOperationException("不能停用本人账号或降低本人的管理员角色");
            if(before.Role==AccountRole.Administrator&&before.Enabled&&(!account.Enabled||account.Role!=AccountRole.Administrator)&&await db.Users.CountAsync(u=>u.Enabled&&u.Role==(int)AccountRole.Administrator,ct)<=1)throw new InvalidOperationException("至少保留一个启用的管理员账号");
        }
        var login=account.Login.ToUpperInvariant();if(await db.Users.AnyAsync(u=>u.Id!=account.Id&&u.Login==login,ct))throw new ArgumentException("账号登录名已存在");
        row.Login=login;row.DisplayName=account.DisplayName.Trim();row.Role=(int)account.Role;row.Enabled=account.Enabled;row.PasswordHash=account.PasswordHash;row.SecurityVersion=create?0:checked(row.SecurityVersion+1);
        var saved=Account(row);db.Events.Add(new(){AtUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Category="操作日志",ActorId=actor.AccountId,ActorName=actor.DisplayName,Content=(create?"新增账号 ":"修改账号 ")+row.Login,Result="已保存，原登录会话失效",Details=JsonSerializer.Serialize(new{EventCode="account.save",Before=before is null?null:Public(before),After=Public(saved),PasswordChanged=before is null||before.PasswordHash!=saved.PasswordHash},Json)});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return saved;
    }
    public async Task ChangeOwnPasswordAsync(string id,long expectedVersion,string newHash,AuditActor actor,CancellationToken ct=default)
    {
        if(id!=actor.AccountId||string.IsNullOrWhiteSpace(newHash))throw new UnauthorizedAccessException("只能修改本人的密码");await using var c=await OpenAsync(ct);await using var db=Context(c);await using var tx=await db.Database.BeginTransactionAsync(ct);var row=await db.Users.FirstOrDefaultAsync(u=>u.Id==id,ct)??throw new UnauthorizedAccessException("账号不存在");if(!row.Enabled||row.SecurityVersion!=expectedVersion)throw new RevisionConflictException();row.PasswordHash=newHash;row.SecurityVersion=checked(row.SecurityVersion+1);
        db.Events.Add(new(){AtUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Category="操作日志",ActorId=actor.AccountId,ActorName=actor.DisplayName,Content="修改本人登录密码",Result="已保存，请重新登录",Details="{\"eventCode\":\"account.password\"}"});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
