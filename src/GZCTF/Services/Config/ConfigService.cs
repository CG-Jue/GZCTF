using System.ComponentModel;
using System.Reflection;
using GZCTF.Models.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ConfigModel = GZCTF.Models.Data.Config;

namespace GZCTF.Services.Config;

public class ConfigService(
    AppDbContext context,
    IDistributedCache cache,
    ILogger<ConfigService> logger,
    IConfiguration configuration) : IConfigService
{
    readonly IConfigurationRoot? _configuration = configuration as IConfigurationRoot;

// 保存配置
    public Task SaveConfig(Type type, object? value, CancellationToken token = default) =>
        SaveConfigSet(GetConfigs(type, value), token);

// 保存配置
    public Task SaveConfig<T>(T config, CancellationToken token = default) where T : class =>
        SaveConfigSet(GetConfigs(config), token);

// 重新加载配置
    public void ReloadConfig() => _configuration?.Reload();

    public async Task SaveConfigSet(HashSet<ConfigModel> configs, CancellationToken token = default)
    {
        // 从数据库中获取所有配置
        Dictionary<string, ConfigModel> dbConfigs = await context.Configs
            .ToDictionaryAsync(c => c.ConfigKey, c => c, token);
        HashSet<string> cacheKeys = [];

        // 遍历所有配置
        foreach (ConfigModel conf in configs)
        {
            // 如果配置已经存在
            if (dbConfigs.TryGetValue(conf.ConfigKey, out ConfigModel? dbConf))
            {
                // 如果配置值没有变化，则跳过
                if (dbConf.Value == conf.Value)
                    continue;

                // 更新配置值
                dbConf.Value = conf.Value;

                // 如果配置有缓存键，则添加到缓存键集合中
                if (conf.CacheKeys is not null)
                    cacheKeys.UnionWith(conf.CacheKeys);

                // 记录日志
                logger.SystemLog(
                    Program.StaticLocalizer[nameof(Resources.Program.Config_GlobalConfigUpdated), conf.ConfigKey,
                        conf.Value ?? "null"],
                    TaskStatus.Success, LogLevel.Debug);
            }
            else
            {
                // 如果配置不存在，则添加到数据库中
                if (conf.CacheKeys is not null)
                    cacheKeys.UnionWith(conf.CacheKeys);

                await context.Configs.AddAsync(conf, token);

                // 记录日志
                logger.SystemLog(
                    Program.StaticLocalizer[nameof(Resources.Program.Config_GlobalConfigAdded), conf.ConfigKey,
                        conf.Value ?? "null"],
                    TaskStatus.Success, LogLevel.Debug);
            }
        }

        // 保存配置到数据库
        await context.SaveChangesAsync(token);
        _configuration?.Reload();

        // flush cache
        foreach (var key in cacheKeys)
            await cache.RemoveAsync(key, token);
    }

    static void MapConfigsInternal(string key, HashSet<ConfigModel> configs, PropertyInfo info, object? value)
    {
        // ignore when value with `AutoSaveIgnoreAttribute`
        if (value is null || info.GetCustomAttribute<AutoSaveIgnoreAttribute>() != null)
            return;

        Type type = info.PropertyType;
        // ignore when type is array or array-like interface
        if (type.IsArray || IsArrayLikeInterface(type))
            throw new NotSupportedException(Program.StaticLocalizer[nameof(Resources.Program.Config_TypeNotSupported)]);

        TypeConverter converter = TypeDescriptor.GetConverter(type);

        if (type == typeof(string) || type.IsValueType)
        {
            // get cache flush keys
            var keys = info.GetCustomAttributes<CacheFlushAttribute>()
                .Select(a => a.CacheKey).ToArray();

            // add config to configs
            configs.Add(new(key, converter.ConvertToString(value) ?? string.Empty, keys));
        }
        else if (type.IsClass)
        {
            // recursively map configs for class type
            foreach (PropertyInfo item in type.GetProperties())
                MapConfigsInternal($"{key}:{item.Name}", configs, item, item.GetValue(value));
        }
    }

// 静态方法，获取指定类型的配置
    static HashSet<ConfigModel> GetConfigs(Type type, object? value)
    {
    // 创建一个HashSet，用于存储配置
        HashSet<ConfigModel> configs = [];

    // 遍历指定类型的所有属性
        foreach (PropertyInfo item in type.GetProperties())
        // 调用内部方法，将属性名和属性值映射到配置中
            MapConfigsInternal($"{type.Name}:{item.Name}", configs, item, item.GetValue(value));

    // 返回配置
        return configs;
    }

// 定义一个泛型方法，用于获取指定类型的配置
    public static HashSet<ConfigModel> GetConfigs<T>(T config) where T : class
    {
        // 创建一个HashSet，用于存储配置
        HashSet<ConfigModel> configs = [];
        // 获取指定类型的Type对象
        Type type = typeof(T);

        // 遍历指定类型的所有属性
        foreach (PropertyInfo item in type.GetProperties())
            // 调用内部方法，将属性名和属性值映射为ConfigModel对象，并添加到HashSet中
            MapConfigsInternal($"{type.Name}:{item.Name}", configs, item, item.GetValue(config));

        // 返回HashSet
        return configs;
    }

// 判断一个类型是否实现了IEnumerable、ICollection、IList、IDictionary或ISet接口
    static bool IsArrayLikeInterface(Type type)
    {
        // 如果类型不是接口或者不是泛型类型，则返回false
        if (!type.IsInterface || !type.IsConstructedGenericType)
            return false;

        // 获取泛型类型的定义
        Type genericTypeDefinition = type.GetGenericTypeDefinition();
        // 判断泛型类型是否为IEnumerable、ICollection、IList、IDictionary或ISet接口
        return genericTypeDefinition == typeof(IEnumerable<>)
               || genericTypeDefinition == typeof(ICollection<>)
               || genericTypeDefinition == typeof(IList<>)
               || genericTypeDefinition == typeof(IDictionary<,>)
               || genericTypeDefinition == typeof(ISet<>);
    }
}
