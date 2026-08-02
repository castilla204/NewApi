FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS base
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

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
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
