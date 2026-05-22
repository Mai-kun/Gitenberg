using Gitenberg.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=gitenberg.db");
        }
    }
}