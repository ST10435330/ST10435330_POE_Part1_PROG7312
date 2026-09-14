FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj
COPY src/SmartX.Api/ src/SmartX.Api/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
ENV DataDirectory=/app/data
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
