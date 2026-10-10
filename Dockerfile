FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Carolina.fsproj global.json Directory.Build.props ./
RUN dotnet restore Carolina.fsproj -r linux-x64 -p:PublishReadyToRun=true
COPY Program.fs ./
RUN dotnet publish Carolina.fsproj -c Release -r linux-x64 -o /app --no-restore --self-contained false -p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV PORT=8080
ENV ASPNETCORE_URLS=http://[::]:8080
ENV DOTNET_gcServer=0
ENV DOTNET_ReadyToRun=1
ENV DOTNET_TieredPGO=0
ENV DOTNET_TC_QuickJitForLoops=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "Carolina.dll"]
