# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY . ./
RUN dotnet restore EduNexus.sln
RUN dotnet publish src/EduNexus.Api/EduNexus.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app ./
# Run as the image's built-in non-root user.
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "EduNexus.Api.dll"]
