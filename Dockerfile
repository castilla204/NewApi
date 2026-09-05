FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS base
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

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
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
