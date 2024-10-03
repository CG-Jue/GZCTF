using GZCTF.Hubs.Clients;
using GZCTF.Repositories.Interface;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;

namespace GZCTF.Hubs;

public class UserHub : Hub<IUserClient>
{
    // 重写OnConnectedAsync方法
    public override async Task OnConnectedAsync()
    {
        // 获取HttpContext
        HttpContext? context = Context.GetHttpContext();

        // 如果HttpContext为空，或者请求参数中没有game，或者game参数不能转换为int类型，则断开连接
        if (context is null
            || !context.Request.Query.TryGetValue("game", out StringValues gameId)
            || !int.TryParse(gameId, out var gId))
        {
            Context.Abort();
            return;
        }

        // 从依赖注入容器中获取IGameRepository实例
        var gameRepository = context.RequestServices.GetRequiredService<IGameRepository>();
        // 根据gameId获取游戏
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
