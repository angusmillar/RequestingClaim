FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8083

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Abm.Requesting.Claim/Abm.Requesting.Claim.csproj", "Abm.Requesting.Claim/"]
RUN dotnet restore "Abm.Requesting.Claim/Abm.Requesting.Claim.csproj"
COPY . .
WORKDIR "/src/Abm.Requesting.Claim"
RUN dotnet build "Abm.Requesting.Claim.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Abm.Requesting.Claim.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Abm.Requesting.Claim.dll"]
