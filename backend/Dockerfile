# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore EduNexus.sln
RUN dotnet publish src/EduNexus.Api/EduNexus.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "EduNexus.Api.dll"]
