FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:eaa79205c3ade4792a7f7bf310a3aac51fe0e1d91c44e40f70b7c6423d475fe0 AS base
WORKDIR /app

# ✅ RENDER.COM: Instalar biblioteca faltante para PostgreSQL/Npgsql
# Resuelve el error: "Cannot load library libgssapi_krb5.so.2"
RUN apt-get update && apt-get install -y libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

# ✅ RENDER.COM: Exponer puerto 10000 (puerto por defecto de Render.com)
# Render.com usa la variable PORT que puede ser 10000 u otro valor
EXPOSE 10000

# ✅ RENDER.COM: Configurar variables de entorno para puerto
# Render.com pasará PORT como variable de entorno, pero configuramos un valor por defecto
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV PORT=10000

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

# ✅ SEGURIDAD: Usar el usuario no-root 'app' que viene en la imagen de .NET
# Las imágenes de .NET 8+ incluyen un usuario 'app' con UID 1654
# No es necesario crear un usuario personalizado
COPY --chown=app:app --from=publish /app/publish .

# ✅ SEGURIDAD: Cambiar a usuario no-root
USER app

ENTRYPOINT ["dotnet", "newApi.dll"]
