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
EXPOSE 8080

ENTRYPOINT ["dotnet", "Gitenberg.Web.dll"]
