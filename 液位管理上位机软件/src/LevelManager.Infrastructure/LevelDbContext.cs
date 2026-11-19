using Microsoft.EntityFrameworkCore;
namespace Naide.LevelManager.Infrastructure;
internal sealed class ConfigurationRow {public int Id{get;set;} public long Revision{get;set;} public string Payload{get;set;}="";}
internal sealed class ConfigurationVersionRow {public long Revision{get;set;} public string Payload{get;set;}="";public long AtUtcMs{get;set;}}
internal sealed class EventRow
{
 public long Id{get;set;}public long AtUtcMs{get;set;}public string Category{get;set;}="";public string ActorId{get;set;}="";public string ActorName{get;set;}="";
 public string? TankId{get;set;}public string? TankCode{get;set;}public string Content{get;set;}="";public string Result{get;set;}="";public string Details{get;set;}="";
}
internal sealed class UserRow
{
 public string Id{get;set;}="";public string Login{get;set;}="";public string DisplayName{get;set;}="";public int Role{get;set;}public string PasswordHash{get;set;}="";public bool Enabled{get;set;}public long SecurityVersion{get;set;}
}
internal sealed class LevelDbContext(DbContextOptions<LevelDbContext> options):DbContext(options)
{
 public DbSet<ConfigurationRow> Configuration=>Set<ConfigurationRow>();
 public DbSet<ConfigurationVersionRow> ConfigurationVersions=>Set<ConfigurationVersionRow>();
 public DbSet<EventRow> Events=>Set<EventRow>();public DbSet<UserRow> Users=>Set<UserRow>();
 protected override void OnModelCreating(ModelBuilder b)
 {
  b.Entity<ConfigurationRow>().ToTable("configuration").HasKey(x=>x.Id);b.Entity<ConfigurationRow>().Property(x=>x.Revision).IsConcurrencyToken();
  b.Entity<ConfigurationVersionRow>().ToTable("configuration_versions").HasKey(x=>x.Revision);b.Entity<ConfigurationVersionRow>().Property(x=>x.Revision).ValueGeneratedNever();
  b.Entity<EventRow>().ToTable("operation_events").HasKey(x=>x.Id);b.Entity<EventRow>().HasIndex(x=>new{x.TankId,x.AtUtcMs});b.Entity<EventRow>().HasIndex(x=>x.AtUtcMs);
  b.Entity<UserRow>().ToTable("users").HasKey(x=>x.Id);b.Entity<UserRow>().Property(x=>x.SecurityVersion).IsConcurrencyToken();b.Entity<UserRow>().HasIndex(x=>x.Login).IsUnique();
 }
}
