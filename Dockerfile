FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/HyundaiBridge/HyundaiBridge.csproj src/HyundaiBridge/
RUN dotnet restore src/HyundaiBridge --source https://api.nuget.org/v3/index.json
COPY src/HyundaiBridge/ src/HyundaiBridge/
RUN dotnet publish src/HyundaiBridge -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
USER root
RUN mkdir /data && chown app:app /data && chmod 700 /data
COPY --from=build /out/ ./
ENV HYUNDAI_REGION=EU HYUNDAI_SESSION_DIRECTORY=/data
USER app
ENTRYPOINT ["dotnet", "HyundaiBridge.dll"]
CMD ["--serve"]
