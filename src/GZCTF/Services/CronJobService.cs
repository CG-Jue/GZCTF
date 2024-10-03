using System.Threading.Channels;
using GZCTF.Repositories;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using Microsoft.Extensions.Caching.Distributed;

namespace GZCTF.Services;

public class CronJobService(IServiceScopeFactory provider, ILogger<CronJobService> logger) : IHostedService, IDisposable
{
    // 定义一个Timer对象
    Timer? _timer;

    // 实现IDisposable接口的Dispose方法，用于释放资源
    public void Dispose()
    {
        // 释放Timer对象
        _timer?.Dispose();
        // 告诉垃圾回收器不再调用终结器
        GC.SuppressFinalize(this);
    }

    // 实现IHostedService接口的StartAsync方法，用于启动定时器
    public Task StartAsync(CancellationToken token)
    {
        // 创建一个Timer对象，每隔3分钟执行一次Execute方法
        _timer = new Timer(Execute, null, TimeSpan.Zero, TimeSpan.FromMinutes(3));
        // 记录系统日志，表示CronJob已启动
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.CronJob_Started)], TaskStatus.Success,
            LogLevel.Debug);
        // 返回一个已完成的Task
        return Task.CompletedTask;
    }

    // 实现IHostedService接口的StopAsync方法，用于停止定时器
    public Task StopAsync(CancellationToken token)
    {
        // 停止Timer对象
        _timer?.Change(Timeout.Infinite, 0);
        // 记录系统日志，表示CronJob已停止
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.CronJob_Stopped)], TaskStatus.Exit,
            LogLevel.Debug);
        // 返回一个已完成的Task
        return Task.CompletedTask;
    }

    // 检查容器是否过期
    async Task ContainerChecker(AsyncServiceScope scope)
    {
        // 获取容器仓库
        var containerRepo = scope.ServiceProvider.GetRequiredService<IContainerRepository>();

        // 获取所有即将过期的容器
        foreach (Models.Data.Container container in await containerRepo.GetDyingContainers())
        {
            // 销毁容器
            await containerRepo.DestroyContainer(container);
            // 记录系统日志，表示已移除过期容器
            logger.SystemLog(
                Program.StaticLocalizer[nameof(Resources.Program.CronJob_RemoveExpiredContainer),
                    container.ContainerId],
                TaskStatus.Success, LogLevel.Debug);
        }
    }

    // 初始化缓存
    async Task BootstrapCache(AsyncServiceScope scope)
    {
        // 获取游戏仓库
        var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
        // 获取即将开始的游戏
        var upcoming = await gameRepo.GetUpcomingGames();

        // 如果没有即将开始的游戏，则返回
        if (upcoming.Length <= 0)
            return;

        // 获取ChannelWriter和DistributedCache
        var channelWriter = scope.ServiceProvider.GetRequiredService<ChannelWriter<CacheRequest>>();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

        // 遍历即将开始的游戏
        foreach (var game in upcoming)
        {
            // 获取缓存键
            var key = CacheKey.ScoreBoard(game);
            // 获取缓存值
            var value = await cache.GetAsync(key);
            // 如果缓存值不为空，则跳过
            if (value is not null)
                continue;

            // 将缓存请求写入ChannelWriter
            await channelWriter.WriteAsync(ScoreboardCacheHandler.MakeCacheRequest(game));
            // 记录系统日志，表示已初始化缓存
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.CronJob_BootstrapRankingCache), key],
                TaskStatus.Success,
                LogLevel.Debug);
        }
    }

    // 执行定时任务
    async void Execute(object? state)
    {
        // 创建一个异步服务范围
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        // 检查容器是否过期
        await ContainerChecker(scope);
        // 初始化缓存
        await BootstrapCache(scope);
    }
}
