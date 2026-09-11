FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS base
WORKDIR /app

# ✅ RENDER.COM: Instalar biblioteca faltante para PostgreSQL/Npgsql
# Resuelve el error: "Cannot load library libgssapi_krb5.so.2"
RUN apt-get update && apt-get install -y libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

# ✅ RENDER.COM: Exponer puerto (Render.com asignará dinámicamente via PORT)
# NO configurar PORT aquí - Render.com lo pasa dinámicamente y Program.cs lo lee
EXPOSE 10000

# ✅ RENDER.COM: Solo configurar entorno de producción
# NO configurar ASPNETCORE_URLS aquí - Program.cs lo configura dinámicamente leyendo PORT
ENV ASPNETCORE_ENVIRONMENT=Production

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src
COPY ["newApi.csproj", "./"]
RUN dotnet restore "newApi.csproj"
COPY . .
RUN dotnet build "newApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "newApi.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app

# ✅ SEGURIDAD: Usar el usuario no-root 'app' que viene en la imagen de .NET
# Las imágenes de .NET 8+ incluyen un usuario 'app' con UID 1654
# No es necesario crear un usuario personalizado
COPY --chown=app:app --from=publish /app/publish .

# ✅ SEGURIDAD: Cambiar a usuario no-root
USER app

ENTRYPOINT ["dotnet", "newApi.dll"]
