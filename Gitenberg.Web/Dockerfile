FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Gitenberg.Web/Gitenberg.Web.csproj", "Gitenberg.Web/"]
RUN dotnet restore "Gitenberg.Web/Gitenberg.Web.csproj"

COPY . .
WORKDIR /src/Gitenberg.Web
RUN dotnet publish "Gitenberg.Web.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Persistent state: SQLite database and Data Protection keys.
RUN mkdir -p /app/data /app/keys

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
# Absolute reminder times ("15:00") are parsed in the server's local zone and
# displayed as UTC+3 (FormatLocal) — pin the container to that zone so both
# sides agree regardless of the host clock.
ENV TZ=Europe/Moscow
EXPOSE 8080

ENTRYPOINT ["dotnet", "Gitenberg.Web.dll"]
