namespace GZCTF.Utils;

enum DirType : byte
{
    Logs,
    Uploads,
    Capture
}

static class FilePath
{
    const string Base = "files";

    internal static readonly string Logs = GetDir(DirType.Logs);
    internal static readonly string Uploads = GetDir(DirType.Uploads);
    internal static readonly string Capture = GetDir(DirType.Capture);

    // 判断是否允许在开发环境中创建基础
    internal static bool AllowBaseCreate(IHostEnvironment environment)
    {
        // 如果当前环境是开发环境，则返回true
        if (environment.IsDevelopment())
            return true;

        // 获取环境变量YES_I_KNOW_FILES_ARE_NOT_PERSISTED_GO_AHEAD_PLEASE的值
        var know = Environment.GetEnvironmentVariable("YES_I_KNOW_FILES_ARE_NOT_PERSISTED_GO_AHEAD_PLEASE");
        // 如果该环境变量不为空，则返回true
        return know is not null;
    }

// 内部静态异步方法，用于确保目录存在
    internal static async Task EnsureDirsAsync(IHostEnvironment environment)
    {
        // 如果Base目录不存在
        if (!Directory.Exists(Base))
        {
            // 如果允许创建Base目录
            if (AllowBaseCreate(environment))
                // 创建Base目录
                Directory.CreateDirectory(Base);
            else
                // 否则退出程序，并显示错误信息
                Program.ExitWithFatalMessage(
                    Program.StaticLocalizer[nameof(Resources.Program.Init_NoFilesDir), Path.GetFullPath(Base)]);
        }

        // 使用using语句打开version.txt文件，并创建StreamWriter对象
        await using (FileStream versionFile = File.Open(Path.Combine(Base, "version.txt"), FileMode.Create))
        await using (var writer = new StreamWriter(versionFile))
        {
            // 将程序集的版本号写入version.txt文件
            await writer.WriteLineAsync(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
        }

        // 遍历DirType枚举的值
        foreach (DirType type in Enum.GetValues<DirType>())
        {
            // 获取每个类型的路径
            var path = Path.Combine(Base, type.ToString().ToLower());
            // 如果路径不存在
            if (!Directory.Exists(path))
                // 创建路径
                Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// 获取文件夹路径
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    static string GetDir(DirType type) => Path.Combine(Base, type.ToString().ToLower());

    /// <summary>
    /// 获取文件夹内容
    /// </summary>
    /// <param name="dir">文件夹</param>
    /// <param name="totSize">总大小</param>
    /// <returns></returns>
    internal static List<FileRecord> GetFileRecords(string dir, out long totSize)
    {
        // 初始化总大小为0
        totSize = 0;
        // 创建一个FileRecord类型的列表
        var records = new List<FileRecord>();

        // 遍历指定目录下的所有文件
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            // 获取文件信息
            var info = new FileInfo(file);

            // 将文件信息添加到列表中
            records.Add(FileRecord.FromFileInfo(info));

            // 将文件大小累加到总大小中
            totSize += info.Length;
        }

        // 返回文件记录列表
        return records;
    }
}
