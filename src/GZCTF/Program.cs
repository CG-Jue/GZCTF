/*
 * GZ::CTF
 *
 * 版权所有 © 2022-present GZTimeWalker
 *
 * 此源代码根据 AGPLv3 许可证在 LICENSE 文件中提供的源代码树根目录下的 LICENSE 文件中授权。
 *
 * 与 "GZCTF"（包括变体和派生）相关的标识符受到保护。
 * 例如，包括 "GZCTF"，"GZ::CTF"，"GZCTF_FLAG" 和类似结构。
 *
 * 不得修改这些标识符的修改。
 *
 * 修改请参见 LICENSE_ADDENDUM.txt
 */

global using GZCTF.Models.Data;
global using GZCTF.Utils;
global using AppDbContext = GZCTF.Models.AppDbContext;
global using TaskStatus = GZCTF.Utils.TaskStatus;
using System.Globalization;
using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GZCTF.Extensions;
using GZCTF.Hubs;
using GZCTF.Middlewares;
using GZCTF.Models.Internal;
using GZCTF.Repositories;
using GZCTF.Repositories.Interface;
using GZCTF.Services;
using GZCTF.Services.Cache;
using GZCTF.Services.Config;
using GZCTF.Services.Container;
using GZCTF.Services.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Serilog;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

GZCTF.Program.Banner();

#region Host

// 添加本地化服务，并设置资源路径为Resources
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources")
    // 配置请求本地化选项
    .Configure<RequestLocalizationOptions>(options =>
    {
        // 设置支持的文化
        string[] supportedCultures = ["en-US", "zh-CN", "ja-JP"];

        // 添加支持的文化
        options
            .AddSupportedCultures(supportedCultures)
            // 添加支持的用户界面文化
            .AddSupportedUICultures(supportedCultures);

        // 将当前文化应用到响应头中
        options.ApplyCurrentCultureToResponseHeaders = true;
    });

// 配置Kestrel服务器
builder.WebHost.ConfigureKestrel(options =>
{
    // 获取Kestrel配置节
    IConfigurationSection kestrelSection = builder.Configuration.GetSection("Kestrel");
    // 配置Kestrel服务器
    options.Configure(kestrelSection);
    // 绑定Kestrel配置节
    kestrelSection.Bind(options);
});

// 清除所有日志提供程序
builder.Logging.ClearProviders();
// 设置最小日志级别为Trace
builder.Logging.SetMinimumLevel(LogLevel.Trace);
// 使用Serilog作为日志记录器
builder.Host.UseSerilog(dispose: true);
// 添加环境变量
builder.Configuration.AddEnvironmentVariables("GZCTF_");
// 初始化日志记录器
Log.Logger = LogHelper.GetInitLogger();

// 确保目录存在
await FilePath.EnsureDirsAsync(builder.Environment);

#endregion Host

#region AppDbContext

// 如果是测试环境或者开发环境且没有配置连接字符串，则使用内存数据库
if (GZCTF.Program.IsTesting || (builder.Environment.IsDevelopment() &&
                                !builder.Configuration.GetSection("ConnectionStrings").Exists()))
{
    builder.Services.AddDbContext<AppDbContext>(
        options => options.UseInMemoryDatabase("TestDb")
    );
}
else
{
    // 如果没有配置数据库连接字符串，则退出程序
    if (!builder.Configuration.GetSection("ConnectionStrings").GetSection("Database").Exists())
        GZCTF.Program.ExitWithFatalMessage(
            GZCTF.Program.StaticLocalizer[nameof(GZCTF.Resources.Program.Database_NoConnectionString)]);

    // 添加数据库上下文
    builder.Services.AddDbContext<AppDbContext>(
        options =>
        {
            // 使用PostgreSQL数据库
            options.UseNpgsql(builder.Configuration.GetConnectionString("Database"),
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));

            // 如果不是开发环境，则返回
            if (!builder.Environment.IsDevelopment())
                return;

            // 启用敏感数据日志记录
            options.EnableSensitiveDataLogging();
            // 启用详细错误
            options.EnableDetailedErrors();
        }
    );
}

#endregion AppDbContext

#region Configuration

// 如果不是测试模式
if (!GZCTF.Program.IsTesting)
    try
    {
        // 添加实体配置
        builder.Configuration.AddEntityConfiguration(options =>
        {
            // 如果存在数据库连接字符串
            if (builder.Configuration.GetSection("ConnectionStrings").GetSection("Database").Exists())
                // 使用PostgreSQL数据库
                options.UseNpgsql(builder.Configuration.GetConnectionString("Database"));
            else
                // 使用内存数据库
                options.UseInMemoryDatabase("TestDb");
        });
    }
    catch (Exception e)
    {
        // 如果存在数据库连接字符串
        if (builder.Configuration.GetSection("ConnectionStrings").GetSection("Database").Exists())
            // 记录错误日志
            Log.Logger.Error(GZCTF.Program.StaticLocalizer[
                nameof(GZCTF.Resources.Program.Database_CurrentConnectionString),
                builder.Configuration.GetConnectionString("Database") ?? "null"]);
        // 退出程序并记录错误信息
        GZCTF.Program.ExitWithFatalMessage(
            GZCTF.Program.StaticLocalizer[nameof(GZCTF.Resources.Program.Database_ConnectionFailed), e.Message]);
    }

#endregion Configuration

#region OpenApiDocument

// 添加路由服务，将URL转换为小写
builder.Services.AddRouting(options => options.LowercaseUrls = true);
// 添加OpenApi文档服务
builder.Services.AddOpenApiDocument(settings =>
{
    // 设置文档名称
    settings.DocumentName = "v1";
    // 设置版本号
    settings.Version = "v1";
    // 设置标题
    settings.Title = "GZCTF Server API";
    // 设置描述
    settings.Description = "GZCTF Server API Document";
    // 使用控制器摘要作为标签描述
    settings.UseControllerSummaryAsTagDescription = true;
});

#endregion OpenApiDocument

#region SignalR & Cache

// 添加SignalR服务
ISignalRServerBuilder signalrBuilder = builder.Services.AddSignalR().AddJsonProtocol();

// 获取Redis连接字符串
var redisConStr = builder.Configuration.GetConnectionString("RedisCache");
// 如果Redis连接字符串为空，则添加内存缓存
if (string.IsNullOrWhiteSpace(redisConStr))
{
    builder.Services.AddDistributedMemoryCache();
}
// 否则，添加StackExchangeRedis缓存
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConStr;
    });

    // 添加StackExchangeRedis作为SignalR的持久化存储
    signalrBuilder.AddStackExchangeRedis(redisConStr, options =>
    {
        options.Configuration.ChannelPrefix = new RedisChannel("GZCTF", RedisChannel.PatternMode.Literal);
    });
}

#endregion SignalR & Cache

#region Identity

// 添加数据保护服务，并将密钥持久化到AppDbContext中
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

// 添加身份验证服务，设置默认的方案
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies(options =>
{
    // 配置身份验证的Cookie
    options.ApplicationCookie?.Configure(auth =>
    {
        auth.Cookie.Name = "GZCTF_Token";
        auth.SlidingExpiration = true;
        auth.ExpireTimeSpan = TimeSpan.FromDays(7);
    });
});

// 添加身份验证核心服务，并配置用户信息
// 添加IdentityCore服务，并配置用户信息
builder.Services.AddIdentityCore<UserInfo>(options =>
    {
        // 用户必须使用唯一的电子邮件地址
        options.User.RequireUniqueEmail = true;
        // 密码不需要包含非字母数字字符
        options.Password.RequireNonAlphanumeric = false;
        // 用户必须通过电子邮件确认登录
        options.SignIn.RequireConfirmedEmail = true;

        // Allow all characters in username
        options.User.AllowedUserNameCharacters = string.Empty;
    })
    .AddSignInManager<SignInManager<UserInfo>>()
    .AddUserManager<UserManager<UserInfo>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddErrorDescriber<TranslatedIdentityErrorDescriber>()
    .AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
    o.TokenLifespan = TimeSpan.FromHours(3)
);

#endregion Identity

#region Telemetry

// 从配置文件中获取Telemetry配置
var telemetryOptions = builder.Configuration.GetSection("Telemetry").Get<TelemetryConfig>();
// 将Telemetry配置添加到服务集合中
builder.Services.AddTelemetry(telemetryOptions);

#endregion

#region Services and Repositories

// 添加IMailSender接口的实例，并配置EmailConfig
builder.Services.AddSingleton<IMailSender, MailSender>()
    .Configure<EmailConfig>(builder.Configuration.GetSection(nameof(EmailConfig)));

// 配置RegistryConfig、AccountPolicy、GlobalConfig、ContainerPolicy、ContainerProvider
builder.Services.Configure<RegistryConfig>(builder.Configuration.GetSection(nameof(RegistryConfig)));
builder.Services.Configure<AccountPolicy>(builder.Configuration.GetSection(nameof(AccountPolicy)));
builder.Services.Configure<GlobalConfig>(builder.Configuration.GetSection(nameof(GlobalConfig)));
builder.Services.Configure<ContainerPolicy>(builder.Configuration.GetSection(nameof(ContainerPolicy)));
builder.Services.Configure<ContainerProvider>(builder.Configuration.GetSection(nameof(ContainerProvider)));

// 获取ForwardedOptions配置，如果为空，则配置ForwardedHeadersOptions
var forwardedOptions = builder.Configuration.GetSection(nameof(ForwardedOptions)).Get<ForwardedOptions>();
if (forwardedOptions is null)
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    });
else
    builder.Services.Configure<ForwardedHeadersOptions>(forwardedOptions.ToForwardedHeadersOptions);

// 添加CaptchaService和ContainerService
builder.Services.AddCaptchaService(builder.Configuration);
builder.Services.AddContainerService(builder.Configuration);

// 添加各种Repository
builder.Services.AddScoped<IConfigService, ConfigService>();
builder.Services.AddScoped<ILogRepository, LogRepository>();
builder.Services.AddScoped<IFileRepository, FileRepository>();
builder.Services.AddScoped<IPostRepository, PostRepository>();
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<IContainerRepository, ContainerRepository>();
builder.Services.AddScoped<IGameEventRepository, GameEventRepository>();
builder.Services.AddScoped<ICheatInfoRepository, CheatInfoRepository>();
builder.Services.AddScoped<IGameNoticeRepository, GameNoticeRepository>();
builder.Services.AddScoped<ISubmissionRepository, SubmissionRepository>();
builder.Services.AddScoped<IGameInstanceRepository, GameInstanceRepository>();
builder.Services.AddScoped<IGameChallengeRepository, GameChallengeRepository>();
builder.Services.AddScoped<IParticipationRepository, ParticipationRepository>();
builder.Services.AddScoped<IExerciseInstanceRepository, ExerciseInstanceRepository>();
builder.Services.AddScoped<IExerciseChallengeRepository, ExerciseChallengeRepository>();

// 添加ExcelHelper
builder.Services.AddScoped<ExcelHelper>();

// 添加各种Channel
builder.Services.AddChannel<Submission>();
builder.Services.AddChannel<CacheRequest>();
builder.Services.AddSingleton<CacheHelper>();

// 添加各种HostedService
builder.Services.AddHostedService<CacheMaker>();
builder.Services.AddHostedService<FlagChecker>();
builder.Services.AddHostedService<CronJobService>();

// 添加HealthChecks和RateLimiter
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(RateLimiter.ConfigureRateLimiter);
// 添加ResponseCompression
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<ZStandardCompressionProvider>();
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        [
            // See others in ResponseCompressionDefaults.MimeTypes
            MediaTypeNames.Application.Pdf
        ]
    );
    options.EnableForHttps = true;
});

// 添加ControllersWithViews和DataAnnotationsLocalization
// 添加控制器和视图
builder.Services.AddControllersWithViews().ConfigureApiBehaviorOptions(options =>
{
    // 设置无效模型状态响应工厂
    options.InvalidModelStateResponseFactory = GZCTF.Program.InvalidModelStateHandler;
}).AddDataAnnotationsLocalization(options =>
{
    // 设置数据注释本地化提供程序
    options.DataAnnotationLocalizerProvider = (_, factory) =>
        factory.Create(typeof(GZCTF.Resources.Program));
});

#endregion Services and Repositories

#region Middlewares

// 创建Web应用程序
WebApplication app = builder.Build();

// 获取日志记录器
Log.Logger = LogHelper.GetLogger(app.Configuration, app.Services);

// 运行预启动工作
await app.RunPrelaunchWork();

// 使用请求本地化
app.UseRequestLocalization();

// 使用响应压缩
app.UseResponseCompression();

// 使用自定义图标
app.UseCustomFavicon();
app.UseStaticFiles(new StaticFileOptions
{
    // 设置缓存控制头
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = $"public, max-age={60 * 60 * 24 * 7}";
    }
});

// 使用转发头
app.UseForwardedHeaders();

// 如果是开发环境，使用开发者异常页面和Swagger UI
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseOpenApi(options =>
    {
        options.PostProcess += (document, _) => document.Servers.Clear();
    });
    app.UseSwaggerUi();
}
// 否则，使用错误处理程序和HSTS
else
{
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
}

// 使用路由
app.UseRouting();

// 如果没有禁用速率限制，使用速率限制
if (app.Configuration.GetValue<bool>("DisableRateLimit") is not true)
    app.UseRateLimiter();

// 使用身份验证和授权
app.UseAuthentication();
app.UseAuthorization();

// 如果是开发环境或启用了请求日志记录，使用请求日志记录
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("RequestLogging"))
    app.UseRequestLogging();

// 使用WebSocket
app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromMinutes(30) });

// 使用遥测
app.UseTelemetry(telemetryOptions);

// 映射健康检查
app.MapHealthChecks("/healthz");
// 映射控制器
app.MapControllers();

// 映射用户Hub
app.MapHub<UserHub>("/hub/user");
// 映射监控Hub
app.MapHub<MonitorHub>("/hub/monitor");
// 映射管理员Hub
app.MapHub<AdminHub>("/hub/admin");

// 使用索引
await app.UseIndexAsync();

#endregion Middlewares

// 创建一个异步服务范围
await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
// 从服务范围中获取ILogger服务
var logger = scope.ServiceProvider.GetRequiredService<ILogger<GZCTF.Program>>();

try
{
    // 获取程序集的描述信息
    var version = typeof(GZCTF.Program).Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
    // 记录系统日志
    logger.SystemLog(version ?? "GZ::CTF", TaskStatus.Pending, LogLevel.Debug);
    // 运行应用程序
    await app.RunAsync();
}
catch (Exception exception)
{
    // 记录错误日志
    logger.LogError(exception, GZCTF.Program.StaticLocalizer[nameof(GZCTF.Resources.Program.Server_Failed)]);
    // 抛出异常
    throw;
}
finally
{
    // 记录退出日志
    logger.SystemLog(GZCTF.Program.StaticLocalizer[nameof(GZCTF.Resources.Program.Server_Exited)], TaskStatus.Exit,
        LogLevel.Debug);
    // 关闭并刷新日志
    Log.CloseAndFlush();
}

namespace GZCTF
{
    // 定义一个Program类
    public class Program
    {
        // 静态构造函数，在类加载时执行
        static Program()
        {
            // 使用using语句创建一个Stream对象，读取程序集资源中的favicon.webp文件
            using Stream stream = typeof(Program).Assembly
                .GetManifestResourceStream("GZCTF.Resources.favicon.webp")!;
            // 创建一个byte数组，用于存储读取的文件内容
            DefaultFavicon = new byte[stream.Length];

            // 从Stream对象中读取文件内容，并存储到byte数组中
            stream.ReadExactly(DefaultFavicon);
            // 使用SHA256算法对byte数组进行哈希，并将结果转换为字符串
            DefaultFaviconHash = BitConverter.ToString(SHA256.HashData(DefaultFavicon))
                .Replace("-", "").ToLowerInvariant();
        }

        public static bool IsTesting { get; set; }

        // 是否处于测试状态
        internal static IStringLocalizer<Program> StaticLocalizer { get; } =
            new CulturedLocalizer<Program>(CultureInfo.CurrentCulture);

        // 静态本地化器
        internal static byte[] DefaultFavicon { get; }
        internal static string DefaultFaviconHash { get; }

        // 默认的favicon和其哈希值
        internal static void Banner()
        {
            // The GZCTF identifier is protected by the License.
            // DO NOT REMOVE OR MODIFY THE FOLLOWING LINE.
            // Please see LICENSE_ADDENDUM.txt for details.
            const string banner =
                """
                      ___           ___           ___                       ___
                     /  /\         /  /\         /  /\          ___        /  /\
                    /  /:/_       /  /::|       /  /:/         /  /\      /  /:/_
                   /  /:/ /\     /  /:/:|      /  /:/         /  /:/     /  /:/ /\
                  /  /:/_/::\   /  /:/|:|__   /  /:/  ___    /  /:/     /  /:/ /:/
                 /__/:/__\/\:\ /__/:/ |:| /\ /__/:/  /  /\  /  /::\    /__/:/ /:/
                 \  \:\ /~~/:/ \__\/  |:|/:/ \  \:\ /  /:/ /__/:/\:\   \  \:\/:/
                  \  \:\  /:/      |  |:/:/   \  \:\  /:/  \__\/  \:\   \  \::/
                   \  \:\/:/       |  |::/     \  \:\/:/        \  \:\   \  \:\
                    \  \::/        |  |:/       \  \::/          \__\/    \  \:\
                     \__\/         |__|/         \__\/                     \__\/
                """ + "\n";
            Console.WriteLine(banner);

            var versionStr = "";
            Version? version = typeof(Program).Assembly.GetName().Version;
            // 获取程序集的版本号
            if (version is not null)
                versionStr = $"Version: {version.Major}.{version.Minor}.{version.Build}";

            // ReSharper disable once LocalizableElement
            // 输出版权信息和版本号
            Console.WriteLine($"GZCTF © 2022-present GZTimeWalker {versionStr,33}\n");
        }

        // 打印banner
        // 定义一个静态方法，用于在程序退出时输出致命错误信息
        public static void ExitWithFatalMessage(string msg)
        {
            // 使用Log4Net记录致命错误信息
            Log.Logger.Fatal(msg);
            // 等待30秒
            Thread.Sleep(30000);
            // 退出程序，返回1
            Environment.Exit(1);
        }

        // 以致命错误退出程序
        // 定义一个静态方法，用于处理无效的模型状态
        public static IActionResult InvalidModelStateHandler(ActionContext context)
        {
            // 获取本地化服务
            var localizer = context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<Program>>();
            // 如果模型状态中没有错误，则返回一个包含错误信息的JsonResult
            if (context.ModelState.ErrorCount <= 0)
                return new JsonResult(new RequestResponse(
                    localizer[nameof(Resources.Program.Model_ValidationFailed)]))
                { StatusCode = 400 };

            // 获取模型状态中的第一个错误信息
            var error = context.ModelState.Values.Where(v => v.Errors.Count > 0)
                .Select(v => v.Errors.FirstOrDefault()?.ErrorMessage).FirstOrDefault();

            // 返回一个包含错误信息的JsonResult
            return new JsonResult(new RequestResponse(error is [_, ..]
                ? error
                : localizer[nameof(Resources.Program.Model_ValidationFailed)]))
            { StatusCode = 400 };
        }
    }
}