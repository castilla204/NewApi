FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:7c4246c1c384319346d45b3e24a10a21d5b6fc9b36a04790e1588148ff8055b0 AS base
WORKDIR /app
EXPOSE 7124

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71 AS build
WORKDIR /src
COPY ["newApi.csproj", "./"]
RUN dotnet restore "newApi.csproj"
COPY . .
RUN dotnet build "newApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "newApi.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app

# ✅ SEGURIDAD: Crear usuario no-root con UID 1000 para ejecutar la aplicación
RUN groupadd -r -g 1000 appuser && useradd -r -u 1000 -g appuser appuser

COPY --from=publish /app/publish .

# ✅ SEGURIDAD: Cambiar ownership de los archivos al usuario no-root
RUN chown -R appuser:appuser /app

# ✅ SEGURIDAD: Cambiar a usuario no-root
USER appuser

ENTRYPOINT ["dotnet", "newApi.dll"]
