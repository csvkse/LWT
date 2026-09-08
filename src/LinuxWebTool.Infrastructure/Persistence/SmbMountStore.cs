using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>SMB 挂载配置仓储。</summary>
public class SmbMountStore(ISqlSugarClient db)
{
    public Task<List<Entities.SmbMount>> GetAllAsync()
    {
        return db.Queryable<Entities.SmbMount>()
            .OrderBy(m => m.CreateTime)
            .ToListAsync();
    }

    public async Task<Entities.SmbMount?> GetByIdAsync(Guid id)
    {
        return (Entities.SmbMount?)await db.Queryable<Entities.SmbMount>().FirstAsync(m => m.Id == id);
    }

    public Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        return db.Queryable<Entities.SmbMount>()
            .AnyAsync(m => m.Name == name && (excludeId == null || m.Id != excludeId));
    }

    public Task<bool> ExistsLocalPathAsync(string localPath, Guid? excludeId)
    {
        return db.Queryable<Entities.SmbMount>()
            .AnyAsync(m => m.LocalPath == localPath && (excludeId == null || m.Id != excludeId));
    }

    public async Task InsertAsync(Entities.SmbMount mount)
    {
        mount.CreateTime = DateTime.Now;
        mount.UpdateTime = DateTime.Now;
        await db.Insertable(mount).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(Entities.SmbMount mount)
    {
        mount.UpdateTime = DateTime.Now;
        await db.Updateable(mount).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<Entities.SmbMount>().Where(m => m.Id == id).ExecuteCommandAsync();
    }
}
