FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/HyundaiBridge/HyundaiBridge.csproj src/HyundaiBridge/
RUN dotnet restore src/HyundaiBridge --source https://api.nuget.org/v3/index.json
COPY src/HyundaiBridge/ src/HyundaiBridge/
RUN dotnet publish src/HyundaiBridge -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1 AS runtime
WORKDIR /app
USER root
RUN mkdir /data && chown app:app /data && chmod 700 /data
COPY --from=build /out/ ./
ENV HYUNDAI_REGION=EU HYUNDAI_SESSION_DIRECTORY=/data ASPNETCORE_URLS=http://+:8080 ASPNETCORE_HTTP_PORTS=""
USER app
ENTRYPOINT ["dotnet", "HyundaiBridge.dll"]
CMD ["--serve"]
