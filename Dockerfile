FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Shared/CS2AccountHub.Contracts.csproj src/Shared/
COPY src/Backend/CS2AccountHub.Backend.csproj src/Backend/
RUN dotnet restore src/Backend/CS2AccountHub.Backend.csproj
COPY src ./src
RUN dotnet publish src/Backend/CS2AccountHub.Backend.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CS2AccountHub.Backend.dll"]
