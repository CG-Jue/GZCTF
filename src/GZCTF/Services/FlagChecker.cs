using System.Threading.Channels;
using GZCTF.Models.Internal;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

public class FlagChecker(
    ChannelReader<Submission> channelReader,
    ChannelWriter<Submission> channelWriter,
    ILogger<FlagChecker> logger,
    IServiceScopeFactory serviceScopeFactory) : IHostedService
{
    CancellationTokenSource TokenSource { get; set; } = new();
    const int MaxWorkerCount = 4;

    internal static int GetWorkerCount()
    {
        // if RAM < 2GiB or CPU <= 3, return 1
        // if RAM < 4GiB or CPU <= 6, return 2
        // otherwise, return 4
        var memoryInfo = GC.GetGCMemoryInfo();
        // 获取当前系统的内存信息
        double freeMemory = memoryInfo.TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        // 计算当前系统的可用内存，单位为GiB
        var cpuCount = Environment.ProcessorCount;

        // 获取当前系统的CPU核心数
        if (freeMemory < 2 || cpuCount <= 3)
            return 1;
        // 如果可用内存小于2GiB或者CPU核心数小于等于3，返回1
        if (freeMemory < 4 || cpuCount <= 6)
            return 2;
        // 如果可用内存小于4GiB或者CPU核心数小于等于6，返回2
        return MaxWorkerCount;
        // 否则，返回MaxWorkerCount
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 创建一个新的CancellationTokenSource
        TokenSource = new CancellationTokenSource();

        // 循环启动指定数量的检查器
        for (var i = 0; i < GetWorkerCount(); ++i)
        {
            // 使用Task.Factory.StartNew启动一个新的任务，执行Checker方法
            await Task.Factory.StartNew(() => Checker(i, TokenSource.Token), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // 使用AsyncServiceScope创建一个异步作用域
        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();

        // 从作用域中获取ISubmissionRepository服务
        var submissionRepository = scope.ServiceProvider.GetRequiredService<ISubmissionRepository>();
        // 从数据库中获取未检查的标志
        Submission[] flags = await submissionRepository.GetUncheckedFlags(TokenSource.Token);

        // 将标志写入通道
        foreach (Submission item in flags)
            await channelWriter.WriteAsync(item, TokenSource.Token);

        // 如果有未检查的标志，记录日志
        if (flags.Length > 0)
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_Recheck), flags.Length],
                TaskStatus.Pending,
                LogLevel.Debug);

        // 记录启动日志
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_Started)], TaskStatus.Success,
            LogLevel.Debug);
    }

// 停止异步方法
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 取消TokenSource
        TokenSource.Cancel();

        // 记录系统日志
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_Stopped)], TaskStatus.Exit,
            LogLevel.Debug);

        // 返回已完成任务
        return Task.CompletedTask;
    }

// 定义一个异步方法Checker，用于检查提交
    async Task Checker(int id, CancellationToken token = default)
    {
        // 记录系统日志，表示工作线程已启动
        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStarted), id],
            TaskStatus.Pending,
            LogLevel.Debug);

        try
        {
            // 使用await foreach循环，从channelReader中读取所有提交
            await foreach (Submission item in channelReader.ReadAllAsync(token))
            {
                // 记录系统日志，表示开始处理提交
                logger.SystemLog(
                    Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStartProcessing), id,
                        item.Answer],
                    TaskStatus.Pending, LogLevel.Debug);

                // 创建异步服务范围
                await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();

                // 从服务提供者中获取所需的缓存助手、事件存储库、游戏实例存储库、游戏通知存储库和提交存储库
                var cacheHelper = scope.ServiceProvider.GetRequiredService<CacheHelper>();
                var eventRepository = scope.ServiceProvider.GetRequiredService<IGameEventRepository>();
                var instanceRepository = scope.ServiceProvider.GetRequiredService<IGameInstanceRepository>();
                var gameNoticeRepository = scope.ServiceProvider.GetRequiredService<IGameNoticeRepository>();
                var submissionRepository = scope.ServiceProvider.GetRequiredService<ISubmissionRepository>();

                try
                {
                    // 验证提交的答案
                    (SubmissionType type, AnswerResult ans) = await instanceRepository.VerifyAnswer(item, token);

                    // 根据答案结果执行不同的操作
                    switch (ans)
                    {
                        case AnswerResult.NotFound:
                            // 记录警告日志，表示未找到实例
                            logger.Log(
                                Program.StaticLocalizer[nameof(Resources.Program.FlagChecker_UnknownInstance),
                                    item.Team.Name,
                                    item.GameChallenge.Title],
                                item.User,
                                TaskStatus.NotFound, LogLevel.Warning);
                            break;
                        case AnswerResult.Accepted:
                            {
                                // 记录信息日志，表示答案被接受
                                logger.Log(
                                    Program.StaticLocalizer[nameof(Resources.Program.FlagChecker_AnswerAccepted),
                                        item.Team.Name,
                                        item.GameChallenge.Title,
                                        item.Answer],
                                    item.User, TaskStatus.Success, LogLevel.Information);

                                // 添加事件
                                await eventRepository.AddEvent(
                                    GameEvent.FromSubmission(item, type, ans, Program.StaticLocalizer), token);

                                // only flush the scoreboard if the contest is not ended and the submission is accepted
                                if (item.Game.EndTimeUtc > item.SubmitTimeUtc)
                                    await cacheHelper.FlushScoreboardCache(item.GameId, token);
                                break;
                            }
                        default:
                            {
                                // 记录答案被拒绝的事件
                                logger.Log(
                                    Program.StaticLocalizer[nameof(Resources.Program.FlagChecker_AnswerRejected),
                                        item.Team.Name,
                                        item.GameChallenge.Title,
                                        item.Answer],
                                    item.User, TaskStatus.Failed, LogLevel.Information);

                                // 添加事件到事件仓库
                                await eventRepository.AddEvent(
                                    GameEvent.FromSubmission(item, type, ans, Program.StaticLocalizer), token);

                                // 检查作弊
                                CheatCheckInfo result = await instanceRepository.CheckCheat(item, token);
                                ans = result.AnswerResult;

                                // 如果作弊被检测到
                                if (ans == AnswerResult.CheatDetected)
                                {
                                    // 记录作弊被检测到的事件
                                    logger.Log(
                                        Program.StaticLocalizer[nameof(Resources.Program.FlagChecker_CheatDetected),
                                            item.Team.Name,
                                            item.GameChallenge.Title,
                                            result.SourceTeamName ?? ""],
                                        item.User, TaskStatus.Success, LogLevel.Information);

                                    // 添加事件到事件仓库
                                    await eventRepository.AddEvent(
                                        new()
                                        {
                                            Type = EventType.CheatDetected,
                                            Values =
                                                [item.GameChallenge.Title, item.Team.Name, result.SourceTeamName ?? ""],
                                            TeamId = item.TeamId,
                                            UserId = item.UserId,
                                            GameId = item.GameId
                                        }, token);
                                }

                                break;
                            }
                    }

// 如果item.Game.EndTimeUtc大于当前时间，且type不等于SubmissionType.Unaccepted和SubmissionType.Normal，则添加通知
                    if (item.Game.EndTimeUtc > DateTimeOffset.UtcNow
                        && type != SubmissionType.Unaccepted
                        && type != SubmissionType.Normal)
                        await gameNoticeRepository.AddNotice(
                            GameNotice.FromSubmission(item, type, Program.StaticLocalizer), token);

// 将item.Status设置为ans
                    item.Status = ans;
// 发送submission
                    await submissionRepository.SendSubmission(item);
                }
// 捕获DbUpdateConcurrencyException异常
                catch (DbUpdateConcurrencyException)
                {
    // 记录系统日志
                    logger.SystemLog(
                        Program.StaticLocalizer[nameof(Resources.Program.FlagChecker_ConcurrencyFailed), item.Id],
                        TaskStatus.Failed,
                        LogLevel.Warning);
    // 将item写入channelWriter
                    await channelWriter.WriteAsync(item, token);
                }
// 捕获其他异常
                catch (Exception e)
                {
    // 记录系统日志
                    logger.SystemLog(
                        Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerExceptionOccurred), id],
                        TaskStatus.Failed,
                        LogLevel.Debug);
    // 记录错误日志
                    logger.LogError(e.Message, e);
                }

// 如果token被取消，则抛出OperationCanceledException异常
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerCancelled), id],
                TaskStatus.Exit,
                LogLevel.Debug);
        }
        finally
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStopped), id],
                TaskStatus.Exit,
                LogLevel.Debug);
        }
    }
}
