FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711 AS base
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

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:d1823fecac3689a2eb959e02ee3bfe1c2142392808240039097ad70644566190 AS build
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
