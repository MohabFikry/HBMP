FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json nuget.config* ./
COPY libs/ libs/
COPY services/identity/ services/identity/
RUN dotnet restore services/identity/Api/Mersal.Identity.Api.csproj
RUN dotnet publish services/identity/Api/Mersal.Identity.Api.csproj -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
# The data-protection key ring lives here and is mounted as a named volume by Compose. Created AND CHOWNED in
# the image because Docker initialises a fresh named volume from the image's contents and ownership: without
# this the volume comes up owned by root, the non-root app cannot write to it, and the first anti-forgery token
# fails with "Access to the path … is denied" — a 500 on the login page, at first use rather than at startup.
RUN mkdir -p /var/lib/hbmp/dpkeys && chown -R appuser:appuser /var/lib/hbmp
COPY --from=build /app ./
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mersal.Identity.Api.dll"]
