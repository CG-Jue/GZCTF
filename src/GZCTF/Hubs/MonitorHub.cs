using GZCTF.Hubs.Clients;
using GZCTF.Repositories.Interface;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;

namespace GZCTF.Hubs;

// 监控Hub类
public class MonitorHub : Hub<IMonitorClient>
{
    // 当连接建立时调用
    public override async Task OnConnectedAsync()
    {
        // 获取HttpContext
        HttpContext? context = Context.GetHttpContext();

        // 如果HttpContext为空，或者没有监控权限，或者没有获取到游戏ID，或者游戏ID不能转换为整数，则断开连接
        if (context is null
            || !await HubHelper.HasMonitor(context)
            || !context.Request.Query.TryGetValue("game", out StringValues gameId)
            || !int.TryParse(gameId, out var gId))
        {
            Context.Abort();
            return;
        }

        // 获取游戏仓库
        var gameRepository = context.RequestServices.GetRequiredService<IGameRepository>();
        // 根据游戏ID获取游戏
        Game? game = await gameRepository.GetGameById(gId);

        // 如果游戏为空，则断开连接
        if (game is null)
        {
            Context.Abort();
            return;
        }

        // 调用基类的OnConnectedAsync方法
        await base.OnConnectedAsync();

        // 将连接添加到游戏组
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Game_{gId}");
    }
}
