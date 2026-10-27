FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /source
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props ./
COPY CmsEventProcessor/ CmsEventProcessor/
RUN dotnet restore CmsEventProcessor/CmsEventProcessor.csproj --locked-mode
RUN dotnet publish CmsEventProcessor/CmsEventProcessor.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS final
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["dotnet", "CmsEventProcessor.dll"]
