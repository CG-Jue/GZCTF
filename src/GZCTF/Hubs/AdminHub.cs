using GZCTF.Hubs.Clients;
using Microsoft.AspNetCore.SignalR;

namespace GZCTF.Hubs;

// 定义一个AdminHub类，继承自Hub类，并实现IAdminClient接口
public class AdminHub : Hub<IAdminClient>
{
    // 重写OnConnectedAsync方法，当有新的连接时调用
    public override async Task OnConnectedAsync()
    {
        // 调用HubHelper类的HasAdmin方法，判断当前连接是否为管理员
        if (!await HubHelper.HasAdmin(Context.GetHttpContext()!))
        {
            // 如果不是管理员，则终止连接
            Context.Abort();
            return;
        }

        // 如果是管理员，则调用基类的OnConnectedAsync方法
        await base.OnConnectedAsync();
    }
}
