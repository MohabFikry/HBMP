# audit-service — multi-stage build (same image runs on Compose Tier 1 and k3s Tier 2/3).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Central build props + package versions first (better layer caching).
COPY Directory.Build.props Directory.Packages.props global.json nuget.config* ./
COPY libs/ libs/
COPY services/audit/ services/audit/
RUN dotnet restore services/audit/Api/Mersal.Audit.Api.csproj
RUN dotnet publish services/audit/Api/Mersal.Audit.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# Non-root (least privilege).
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
COPY --from=build /app ./
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mersal.Audit.Api.dll"]
