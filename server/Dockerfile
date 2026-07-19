# Multi-stage: build with the SDK, ship only the runtime.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore as its own layer so code edits don't re-download every package.
COPY DraftLeague.Web.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Fly terminates TLS at the edge and forwards plain HTTP to the container.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# SQLite lives on the mounted volume so a redeploy doesn't wipe the draft.
ENV ConnectionStrings__Default="Data Source=/data/draftleague.db"

ENTRYPOINT ["dotnet", "DraftLeague.Web.dll"]
