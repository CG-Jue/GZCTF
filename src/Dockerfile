# 使用mcr.microsoft.com/dotnet/sdk:8.0-jammy作为构建镜像
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

# 定义三个参数
ARG TIMESTAMP
ARG GIT_SHA
ARG GIT_NAME
# 设置环境变量
ENV VITE_APP_BUILD_TIMESTAMP=$TIMESTAMP \
    VITE_APP_GIT_SHA=$GIT_SHA \
    VITE_APP_GIT_NAME=$GIT_NAME

# 更新apt并安装wget、gnupg2、libpcap0.8
RUN apt update && apt install -y wget gnupg2 libpcap0.8 && \
    # 安装Node.js
    wget -qO- https://deb.nodesource.com/setup_20.x | bash - && \
    apt install -y build-essential nodejs

# 安装pnpm
RUN npm i -g pnpm

# 将当前目录下的所有文件复制到镜像的/src目录下
COPY [".", "/src"]

# 设置工作目录为/src/GZCTF
WORKDIR "/src/GZCTF"
# 使用dotnet构建项目
RUN dotnet build "GZCTF.csproj" -c Release -o /app/build

# 使用mcr.microsoft.com/dotnet/sdk:8.0-jammy作为发布镜像
FROM build AS publish
# 使用dotnet发布项目
RUN dotnet publish "GZCTF.csproj" -c Release -o /app/publish -r linux-x64 --no-self-contained /p:PublishReadyToRun=true

# 使用mcr.microsoft.com/dotnet/aspnet:8.0-alpine作为最终镜像
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final

# 设置环境变量
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8

# 设置工作目录为/app
WORKDIR /app
# 暴露8080端口
EXPOSE 8080
# 安装wget、libpcap、icu-data-full、icu-libs、ca-certificates、libgdiplus、tzdata，并更新证书
RUN apk add --update --no-cache wget libpcap icu-data-full icu-libs ca-certificates libgdiplus tzdata && \
    update-ca-certificates

# 从publish镜像中复制/app/publish到当前镜像的/app目录下
COPY --from=publish /app/publish .

# 健康检查，每5分钟检查一次，超时时间为3秒，启动期为10秒，重试次数为1，使用wget命令检查http://localhost:8080/healthz，如果失败则退出
HEALTHCHECK --interval=5m --timeout=3s --start-period=10s --retries=1 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/healthz || exit 1

# 设置容器启动时执行的命令，运行GZCTF.dll
ENTRYPOINT ["dotnet", "GZCTF.dll"]
