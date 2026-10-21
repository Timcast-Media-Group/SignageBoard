FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SignageBoard.Web/SignageBoard.Web.csproj SignageBoard.Web/
RUN dotnet restore SignageBoard.Web/SignageBoard.Web.csproj

COPY SignageBoard.Web/ SignageBoard.Web/
RUN dotnet publish SignageBoard.Web/SignageBoard.Web.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /data/keys && chown -R $APP_UID:$APP_UID /data

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME /data

USER $APP_UID
ENTRYPOINT ["dotnet", "SignageBoard.Web.dll"]
