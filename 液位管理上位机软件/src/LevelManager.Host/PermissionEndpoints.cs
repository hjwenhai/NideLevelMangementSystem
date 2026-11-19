using System.Security.Claims;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Host;

public static class PermissionEndpoints
{
    public const string ClaimType="level.permission";
    public static AuditActor CurrentActor(this HttpContext ctx)=>new(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,ctx.User.FindFirstValue(ClaimTypes.Name)!,Enum.Parse<AccountRole>(ctx.User.FindFirstValue(ClaimTypes.Role)!),ctx.User.FindAll(ClaimType).Select(c=>Enum.Parse<AppPermission>(c.Value)).ToArray());
    public static void MapPermissionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/permissions",async(IRolePermissionStore store,ProductAuthorization authorization,CancellationToken ct)=>Results.Ok(new{definitions=PermissionPolicy.Definitions.Where(d=>d.Id!=AppPermission.AverageTemperatureStart||authorization.HasAverageModule),matrix=await store.GetPermissionsAsync(ct)})).RequireAuthorization("administrator");
        app.MapPut("/api/permissions",async(RolePermissionMatrix matrix,HttpContext ctx,IRolePermissionStore store,IUserStore users,RuntimeOperationGate gate,CancellationToken ct)=>
        {
            // Serialize permission edits with new dispatch admission. Already dispatched work
            // retains its safe completion/stop path, but queued new writes recheck the store.
            await gate.Gate.WaitAsync(ct);try{var user=await users.FindByIdAsync(ctx.CurrentActor().AccountId,ct);if(user is null||!user.Enabled||user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)!=ctx.User.FindFirstValue("securityVersion"))throw new UnauthorizedAccessException("当前账号已改变，请重新登录");return Results.Ok(await store.SavePermissionsAsync(matrix,await store.ResolveAsync(user,ct),ct));}finally{gate.Gate.Release();}
        }).RequireAuthorization("administrator");
    }
}
