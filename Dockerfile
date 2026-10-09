FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY MarketLens.csproj NuGet.Config ./
RUN dotnet restore MarketLens.csproj
COPY . .
RUN dotnet publish MarketLens.csproj -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /publish .
RUN mkdir -p /app/App_Data/keys && chown -R app:app /app/App_Data
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000
USER app
ENTRYPOINT ["dotnet", "MarketLens.dll"]
