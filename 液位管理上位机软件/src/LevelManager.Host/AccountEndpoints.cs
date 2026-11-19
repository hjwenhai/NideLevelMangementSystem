using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Host;
public static class AccountEndpoints
{
    private sealed record PublicUser(string Id,string Login,string DisplayName,AccountRole Role,bool Enabled,long SecurityVersion);
    private sealed record UserInput(string Login,string DisplayName,AccountRole Role,bool Enabled,long SecurityVersion,string? NewPassword);
    private sealed record PasswordInput(string CurrentPassword,string NewPassword);
    private static PublicUser Public(UserAccount u)=>new(u.Id,u.Login,u.DisplayName,u.Role,u.Enabled,u.SecurityVersion);
    private static AuditActor Actor(HttpContext c)=>new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!,c.User.FindFirstValue(ClaimTypes.Name)!,Enum.Parse<AccountRole>(c.User.FindFirstValue(ClaimTypes.Role)!));
    private static void Password(string? value){if(value is null||value.Length is <8 or >128)throw new ArgumentException("密码长度应为8～128字符");}
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/users",async(IUserStore users,CancellationToken ct)=>Results.Ok((await users.ListUsersAsync(ct)).Select(Public))).RequireAuthorization("administrator");
        app.MapPost("/api/users",async(UserInput input,HttpContext ctx,IUserStore users,IPasswordHasher<UserAccount> hasher,CancellationToken ct)=>
        {
            Password(input.NewPassword);var u=new UserAccount(Guid.NewGuid().ToString(),input.Login,input.DisplayName,input.Role,"",input.Enabled,input.SecurityVersion);u=u with{PasswordHash=hasher.HashPassword(u,input.NewPassword!)};return Results.Ok(Public(await users.SaveUserAsync(u,true,Actor(ctx),ct)));
        }).RequireAuthorization("administrator");
        app.MapPut("/api/users/{id}",async(string id,UserInput input,HttpContext ctx,IUserStore users,IPasswordHasher<UserAccount> hasher,CancellationToken ct)=>
        {
            var before=await users.FindByIdAsync(id,ct)??throw new ArgumentException("账号不存在");var next=before with{Login=input.Login,DisplayName=input.DisplayName,Role=input.Role,Enabled=input.Enabled,SecurityVersion=input.SecurityVersion};if(!string.IsNullOrEmpty(input.NewPassword)){Password(input.NewPassword);next=next with{PasswordHash=hasher.HashPassword(next,input.NewPassword)};}
            var saved=await users.SaveUserAsync(next,false,Actor(ctx),ct);if(id==ctx.User.FindFirstValue(ClaimTypes.NameIdentifier))await ctx.SignOutAsync();return Results.Ok(Public(saved));
        }).RequireAuthorization("administrator");
        app.MapPost("/api/auth/password",async(PasswordInput input,HttpContext ctx,IUserStore users,IPasswordHasher<UserAccount> hasher,CancellationToken ct)=>
        {
            Password(input.NewPassword);if(input.CurrentPassword is null||input.CurrentPassword.Length>128)throw new ArgumentException("当前密码无效");var actor=Actor(ctx);var user=await users.FindByIdAsync(actor.AccountId,ct)??throw new UnauthorizedAccessException("账号已失效");if(hasher.VerifyHashedPassword(user,user.PasswordHash,input.CurrentPassword)==PasswordVerificationResult.Failed)throw new ArgumentException("当前密码不正确");
            await users.ChangeOwnPasswordAsync(user.Id,user.SecurityVersion,hasher.HashPassword(user,input.NewPassword),actor,ct);await ctx.SignOutAsync();return Results.Ok(new{message="密码已修改，请重新登录"});
        }).RequireAuthorization().RequireRateLimiting("authentication");
    }
}
