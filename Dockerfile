FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Carolina.fsproj ./
RUN dotnet restore
COPY Program.fs ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV PORT=8080
ENV ASPNETCORE_URLS=http://[::]:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Carolina.dll"]
