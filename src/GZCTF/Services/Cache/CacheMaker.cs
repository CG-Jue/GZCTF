using System.Threading.Channels;
using GZCTF.Repositories;
using MemoryPack;
using Microsoft.Extensions.Caching.Distributed;

namespace GZCTF.Services.Cache;

/// <summary>
/// Cache update request
/// </summary>
// 定义一个名为CacheRequest的公共类，该类接受一个字符串类型的key，一个可选的DistributedCacheEntryOptions类型的options，以及一个可选的字符串数组类型的params
public class CacheRequest(string key, DistributedCacheEntryOptions? options = null, params string[] @params)
{
    // 定义一个DateTimeOffset类型的Time属性，并初始化为当前时间
    public DateTimeOffset Time { get; } = DateTimeOffset.Now;
    // 定义一个字符串类型的Key属性，并初始化为传入的key参数
    public string Key { get; } = key;
    // 定义一个字符串数组类型的Params属性，并初始化为传入的params参数
    public string[] Params { get; } = @params;
    // 定义一个可选的DistributedCacheEntryOptions类型的Options属性，并初始化为传入的options参数
    public DistributedCacheEntryOptions? Options { get; } = options;
}

/// <summary>
/// Cache request handler
/// </summary>
// 定义一个接口，用于处理缓存请求
public interface ICacheRequestHandler
{
    // 根据请求生成缓存键
    public string? CacheKey(CacheRequest request);
    // 处理缓存请求，返回字节数组
    public Task<byte[]> Handler(AsyncServiceScope scope, CacheRequest request, CancellationToken token = default);
}

public class CacheMaker(
    ILogger<CacheMaker> logger,
    IDistributedCache cache,
    ChannelReader<CacheRequest> channelReader,
    IServiceScopeFactory serviceScopeFactory) : IHostedService
{
    // 定义一个字典，用于存储缓存请求处理器
    readonly Dictionary<string, ICacheRequestHandler> _cacheHandlers = new();
    // 定义一个CancellationTokenSource，用于取消任务
    CancellationTokenSource TokenSource { get; set; } = new();

    // 实现IHostedService接口的StartAsync方法，用于启动服务
    public async Task StartAsync(CancellationToken token)
    {
        // 创建一个新的CancellationTokenSource
        TokenSource = new CancellationTokenSource();

        #region Add Handlers

        // 添加一个缓存请求处理器
        AddCacheRequestHandler<ScoreboardCacheHandler>(CacheKey.ScoreBoardBase);

        #endregion

        // 启动一个新的任务，用于处理缓存请求
        await Task.Factory.StartNew(() => Maker(TokenSource.Token), token, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    // 实现IHostedService接口的StopAsync方法，用于停止服务
    public Task StopAsync(CancellationToken token)
    {
        // 取消任务
        TokenSource.Cancel();

        // 记录日志
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_Stopped)], TaskStatus.Success,
            LogLevel.Debug);

        // 返回一个完成的任务
        return Task.CompletedTask;
    }

    // 添加一个缓存请求处理器
    public void AddCacheRequestHandler<T>(string key) where T : ICacheRequestHandler, new() =>
        _cacheHandlers.Add(key, new T());

    // 异步方法，用于处理缓存请求
    async Task Maker(CancellationToken token = default)
    {
        // 记录缓存工作开始
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_WorkerStarted)], TaskStatus.Pending,
            LogLevel.Debug);

        try
        {
            // 异步遍历缓存请求通道
            await foreach (CacheRequest item in channelReader.ReadAllAsync(token))
            {
                // 尝试获取与请求键匹配的缓存请求处理器
                if (!_cacheHandlers.TryGetValue(item.Key, out ICacheRequestHandler? handler))
                {
                    // 如果没有找到匹配的处理器，记录警告日志
                    logger.SystemLog(
                        Program.StaticLocalizer[nameof(Resources.Program.Cache_NoMatchingRequest), item.Key],
                        TaskStatus.NotFound,
                        LogLevel.Warning);
                    continue;
                }

                // 获取缓存键
                var key = handler.CacheKey(item);

                // 如果缓存键为空，记录警告日志
                if (key is null)
                {
                    logger.SystemLog(
                        Program.StaticLocalizer[nameof(Resources.Program.Cache_InvalidUpdateRequest), item.Key],
                        TaskStatus.NotFound,
                        LogLevel.Warning);
                    continue;
                }

                // 获取更新锁
                var updateLock = CacheKey.UpdateLock(key);

                // 如果更新锁存在，记录调试日志并跳过
                if (await cache.GetAsync(updateLock, token) is not null)
                {
                    // only one GZCTF instance will never encounter this
                    logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_InvalidUpdateRequest), key],
                        TaskStatus.Pending,
                        LogLevel.Debug);
                    continue;
                }

                // 获取最后更新时间
                var lastUpdateKey = CacheKey.LastUpdateTime(key);
                var lastUpdateBytes = await cache.GetAsync(lastUpdateKey, token);
                if (lastUpdateBytes is not null && lastUpdateBytes.Length > 0)
                {
                    var lastUpdate = MemoryPackSerializer.Deserialize<DateTimeOffset>(lastUpdateBytes);
                    // if the cache is updated after the request, skip
                    // this will de-bounced the slow cache update request
                    if (lastUpdate > item.Time)
                        continue;
                }

                lastUpdateBytes = MemoryPackSerializer.Serialize(DateTimeOffset.UtcNow);
// 将当前时间序列化为字节数组
                await cache.SetAsync(lastUpdateKey, lastUpdateBytes, new(), token);

// 将字节数组存储到缓存中
                await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();

// 创建异步服务范围
                try
                {
                    await cache.SetAsync(updateLock, [],
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
                        token);

    // 设置更新锁，过期时间为1分钟
                    var bytes = await handler.Handler(scope, item, token);

    // 调用处理程序，获取字节数组
                    if (bytes.Length > 0)
                    {
                        await cache.SetAsync(key, bytes, item.Options ?? new DistributedCacheEntryOptions(), token);
        // 将字节数组存储到缓存中
                        logger.SystemLog(
                            Program.StaticLocalizer[
                                nameof(Resources.Program.Cache_Updated),
                                key, item.Time.ToString("HH:mm:ss.fff"), bytes.Length
                            ], TaskStatus.Success, LogLevel.Debug);
        // 记录缓存更新成功的日志
                    }
                    else
                    {
                        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_GenerationFailed), key],
                            TaskStatus.Failed,
                            LogLevel.Warning);
        // 记录缓存生成失败的日志
                    }
                }
                catch (Exception e)
                {
                    logger.SystemLog(
                        Program.StaticLocalizer[nameof(Resources.Program.Cache_UpdateWorkerFailed), key, e.Message],
                        TaskStatus.Failed,
                        LogLevel.Error);
    // 记录更新工作失败的日志
                }
                finally
                {
                    await cache.RemoveAsync(updateLock, token);
    // 移除更新锁
                }

                token.ThrowIfCancellationRequested();
// 如果请求被取消，则抛出异常
            }
catch (OperationCanceledException)
{
    logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_WorkerCancelled)], TaskStatus.Exit,
        LogLevel.Debug);
    // 记录工作被取消的日志
        }
        catch (OperationCanceledException)
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_WorkerCancelled)], TaskStatus.Exit,
                LogLevel.Debug);
        }
        finally
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.Cache_WorkerStopped)], TaskStatus.Exit,
                LogLevel.Debug);
        }
    }
}
