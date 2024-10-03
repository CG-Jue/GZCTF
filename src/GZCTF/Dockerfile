# 使用mcr.microsoft.com/dotnet/aspnet:8.0-alpine作为基础镜像
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS build
# 定义TARGETPLATFORM变量
ARG TARGETPLATFORM
# 将publish目录复制到/build目录下
COPY publish /build
# 将/build/${TARGETPLATFORM}目录复制到/publish目录下
RUN cp -r /build/${TARGETPLATFORM} /publish

# 使用mcr.microsoft.com/dotnet/aspnet:8.0-alpine作为基础镜像
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final

# 设置环境变量
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8

# 设置工作目录为/app
WORKDIR /app
# 暴露8080端口
EXPOSE 8080
# 安装wget、libpcap、icu-data-full、icu-libs、ca-certificates、libgdiplus、tzdata
RUN apk add --update --no-cache wget libpcap icu-data-full icu-libs ca-certificates libgdiplus tzdata && \
    update-ca-certificates

# 将build镜像中的/publish目录复制到当前目录下
COPY --from=build /publish .

# 设置健康检查，每5分钟检查一次，超时时间为3秒，启动期为10秒，重试次数为1
HEALTHCHECK --interval=5m --timeout=3s --start-period=10s --retries=1 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/healthz || exit 1

# 设置入口点为dotnet GZCTF.dll
ENTRYPOINT ["dotnet", "GZCTF.dll"]
