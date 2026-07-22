FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json nuget.config* ./
COPY libs/ libs/
COPY services/hello/ services/hello/
RUN dotnet restore services/hello/Api/Mersal.Hello.Api.csproj
RUN dotnet publish services/hello/Api/Mersal.Hello.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
COPY --from=build /app ./
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mersal.Hello.Api.dll"]
